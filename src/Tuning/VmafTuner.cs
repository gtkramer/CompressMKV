using System.Globalization;

namespace CompressMkv;

/// <summary>
/// VMAF-guided CQ tuning with two-phase pipeline:
///   Phase 1: Extract lossless reference clips (restored cadence) — one per sample window.
///   Phase 2: Encode each reference clip at candidate CQ levels (high→low) and measure VMAF.
///            Early termination when a CQ meets frame-level thresholds.
///
/// This reduces N_samples × N_cq full source decodes to just N_samples fast-seeking decodes.
/// VMAF runs are CPU-only and overlap naturally with GPU encodes.
/// HDR CQ ladder shift is applied here.
/// </summary>
public static class VmafTuner
{
    public static async Task<TuningResult> TuneAsync(
        Config cfg, GpuGate gpu, string input, string outDir,
        RestoreDecision restore, bool isHdr, string vmafModelPath,
        CancellationToken ct)
    {
        var probe = await Ffprobe.RunAsync(cfg, input, ct);
        var dur = probe.Format?.DurationSeconds ?? throw new InvalidOperationException("No duration.");

        var sampler = new Sampler(cfg.RandomSeed);
        var windows = sampler.StratifiedRandomWindows(dur, cfg.SampleCount, cfg.SampleWindowSeconds);

        var refsDir = Path.Combine(outDir, "refs");
        var samplesDir = Path.Combine(outDir, "samples");
        var vmafDir = Path.Combine(outDir, "vmaf");
        Directory.CreateDirectory(refsDir);
        Directory.CreateDirectory(samplesDir);
        Directory.CreateDirectory(vmafDir);

        // ---- CQ ladder (with optional HDR shift) ----
        var baseCq = cfg.CandidateCq.ToList();
        bool hdrShiftApplied = isHdr && cfg.HdrApplyCqLadderShift && cfg.HdrCqLadderDelta > 0;

        var effectiveCq = hdrShiftApplied
            ? baseCq.Select(cq => Math.Max(cfg.MinCq, cq - cfg.HdrCqLadderDelta)).Distinct().OrderBy(x => x).ToList()
            : baseCq.Distinct().OrderBy(x => x).ToList();

        if (hdrShiftApplied)
            Console.WriteLine($"  HDR tuning: CQ ladder shifted by -{cfg.HdrCqLadderDelta}. Base=[{string.Join(",", baseCq)}] Effective=[{string.Join(",", effectiveCq)}]");

        // =======================================================
        //  Phase 1: Extract lossless FFV1 reference clips.
        //  Each clip has the restore filter applied (IVTC/deinterlace)
        //  so it represents the true progressive reference for VMAF.
        //  Fast-seek (-ss before -i) → minimal decode overhead.
        // =======================================================
        Console.WriteLine($"  Phase 1: Extracting {windows.Count} reference clips (FFV1 lossless, cadence-restored)...");
        var refClips = new string[windows.Count];

        var refTasks = windows.Select(async (w, i) =>
        {
            string refPath = Path.Combine(refsDir, $"ref_s{i:00}.mkv");
            await Pipelines.ExtractReferenceClipAsync(cfg, input, refPath, w, restore, ct);
            return (Index: i, Path: refPath);
        }).ToList();

        // Moderate parallelism for CPU-bound restore filter work.
        foreach (var batch in refTasks.Chunk(4))
        {
            var results = await Task.WhenAll(batch);
            foreach (var (idx, path) in results)
                refClips[idx] = path;
        }
        Console.WriteLine("  Phase 1 complete: reference clips extracted.");

        // =======================================================
        //  Phase 2: Encode + VMAF per CQ level.
        //  Test from highest CQ (most compression) to lowest.
        //  Early termination when a CQ meets frame-level thresholds.
        //  VMAF is CPU-only — overlaps naturally with GPU encodes.
        // =======================================================
        var effectiveCqDesc = effectiveCq.OrderByDescending(x => x).ToList();
        var cqResults = new List<CqAggregate>();

        foreach (var cq in effectiveCqDesc)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"  Tuning CQ={cq}...");

            var tasks = Enumerable.Range(0, windows.Count).Select(async i =>
            {
                string refClip = refClips[i];
                string tag = $"s{i:00}";
                string encPath = Path.Combine(samplesDir, $"cq{cq}_{tag}.mkv");
                string vmafLog = Path.Combine(vmafDir, $"cq{cq}_{tag}.json");

                // Encode: consumes 1 NVENC slot.
                using (await gpu.AcquireAsync(nvenc: 1, nvdec: 0, ct))
                {
                    await Pipelines.EncodeSampleFromRefAsync(cfg, refClip, encPath, cq, ct);
                }

                // VMAF: CPU-only — no GPU gating, runs freely alongside encodes.
                await Pipelines.RunVmafDirectAsync(cfg, refClip, encPath, isHdr, vmafLog, vmafModelPath, ct);

                var vmafResult = await Vmaf.ParseAsync(vmafLog, ct);
                return new SampleMetric
                {
                    SampleIndex = i,
                    Window = windows[i],
                    ReferenceClipPath = refClip,
                    EncodedPath = encPath,
                    VmafLogPath = vmafLog,
                    VmafMean = vmafResult.Mean,
                    VmafHarmonicMean = vmafResult.HarmonicMean,
                    FrameVmafScores = vmafResult.FrameScores,
                };
            }).ToList();

            var metrics = new List<SampleMetric>();
            foreach (var batch in tasks.Chunk(8))
                metrics.AddRange(await Task.WhenAll(batch));

            var agg = CqAggregate.From(cq, metrics);
            cqResults.Add(agg);

            Console.WriteLine($"    mean={agg.MeanVmaf:F2} harmonic={agg.HarmonicMeanVmaf:F2} " +
                $"p05={agg.P05Vmaf:F2} p01={agg.P01Vmaf:F2} min={agg.MinVmaf:F2} " +
                $"frames={agg.TotalFrameCount}");

            // Early termination: highest CQ passing both thresholds is the answer.
            if (agg.MeanVmaf >= cfg.TargetMeanVmaf && agg.P05Vmaf >= cfg.TargetP05Vmaf)
            {
                Console.WriteLine($"    CQ={cq} meets thresholds " +
                    $"(mean>={cfg.TargetMeanVmaf:F1}, p05>={cfg.TargetP05Vmaf:F1}) — stopping search.");
                break;
            }
        }

        var selection = Selector.Select(cfg, cqResults);

        return new TuningResult
        {
            SampleWindows = windows,
            CqResults = cqResults,
            Selection = selection,

            BaseCqList = baseCq,
            EffectiveCqList = effectiveCq,
            HdrCqShiftApplied = hdrShiftApplied,
            HdrCqShiftDelta = hdrShiftApplied ? cfg.HdrCqLadderDelta : 0
        };
    }
}
