using System.Diagnostics;

namespace CompressMkv;

/// <summary>
/// VMAF-guided CQ tuning with two-phase pipeline:
///   Phase 1: Extract lossless reference clips (restored cadence) — one per sample window.
///   Phase 2: Encode each reference clip at candidate CQ levels (high→low) and measure VMAF.
///            Early termination when a CQ meets frame-level thresholds.
///
/// This reduces N_samples × N_cq full source decodes to just N_samples fast-seeking decodes.
///
/// Concurrency model:
///   - CPU-heavy ffmpeg ops (ref extraction, VMAF) gate on a global <see cref="CpuGate"/>
///     so that work flows naturally across files without oversubscribing the CPU.
///   - GPU NVENC sample encodes gate on <see cref="GpuGate"/> separately — they're
///     mostly idle on CPU and run in parallel with CPU-heavy VMAF.
///   - Per-process thread counts come from <see cref="Config.FfmpegCpuThreads"/>,
///     <see cref="Config.FfmpegGpuThreads"/>, and <see cref="Config.LibvmafThreads"/>.
/// </summary>
public static class VmafTuner
{
    public static async Task<TuningResult> TuneAsync(
        Config cfg, GpuGate gpu, CpuGate cpu, string input, string outDir,
        RestoreDecision restore, bool isHdr, HdrMetadata? hdrMetadata,
        PipelineFormat format, string vmafModelVersion,
        CancellationToken ct, IPipelineLogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var probe = await Ffprobe.RunAsync(cfg, input, ct);
        var dur = probe.Format?.DurationSeconds ?? throw new InvalidOperationException("No duration.");

        var sampler = new Sampler(cfg.RandomSeed);
        var windows = sampler.StratifiedRandomWindows(dur, cfg.SampleCount, cfg.SampleWindowSeconds);

        // Surface adaptive sampling: if the source was too short to fit the
        // configured budget, the user should see what we actually used.
        if (windows.Count != cfg.SampleCount)
        {
            double covered = windows.Sum(w => w.LengthSeconds);
            double pct = dur > 0 ? covered / dur * 100 : 0;
            logger.LogInfo(
                $"Adaptive sampling: source is {dur:F1}s — using {windows.Count} window(s) " +
                $"covering {covered:F1}s ({pct:F0}% of source) instead of the configured " +
                $"{cfg.SampleCount}×{cfg.SampleWindowSeconds}s budget.");
        }

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
            logger.LogInfo($"HDR tuning: CQ ladder shifted by -{cfg.HdrCqLadderDelta}. Base=[{string.Join(",", baseCq)}] Effective=[{string.Join(",", effectiveCq)}]");

        // =======================================================
        //  Phase 1: Extract lossless FFV1 reference clips.
        //  Each clip has the restore filter applied (IVTC/deinterlace)
        //  so it represents the true progressive reference for VMAF.
        //  Fast-seek (-ss before -i) → minimal decode overhead.
        //
        //  Throttled by the CpuGate — the global gate caps total in-flight
        //  CPU ffmpeg ops (across all files) at Config.MaxConcurrentCpuFfmpegOps,
        //  so this saturates the CPU without thrashing.  No batch barriers.
        // =======================================================
        logger.SetStage("Phase 1", $"extracting {windows.Count} reference clips");
        logger.LogInfo($"Phase 1: extracting {windows.Count} FFV1 reference clips (cadence-restored).");
        var phase1Sw = Stopwatch.StartNew();
        var refClips = new string[windows.Count];

        int extractedCount = 0;
        var refTasks = windows.Select(async (w, i) =>
        {
            string refPath = Path.Combine(refsDir, $"ref_s{i:00}.mkv");
            using (await cpu.AcquireAsync(ct))
                await Pipelines.ExtractReferenceClipAsync(cfg, input, refPath, w, restore, ct);
            refClips[i] = refPath;
            int done = Interlocked.Increment(ref extractedCount);
            logger.SetStage("Phase 1", $"extracted {done}/{windows.Count} refs");
        }).ToArray();

        await Task.WhenAll(refTasks);
        phase1Sw.Stop();
        logger.LogInfo($"Phase 1 complete in {phase1Sw.Elapsed.TotalSeconds:F1}s.");

        // =======================================================
        //  Phase 2: Encode + VMAF per CQ level.
        //  Test from highest CQ (most compression) to lowest.
        //  Early termination when a CQ meets frame-level thresholds.
        //
        //  All 16 sample tasks for a CQ fire in parallel.  Each task does:
        //    1. acquire NVENC slot (GpuGate) → encode → release
        //    2. acquire CPU slot   (CpuGate) → VMAF   → release
        //
        //  Concurrency emerges naturally from the gates:
        //    - Up to 2 NVENC encodes in flight at once (across all files).
        //    - Up to MaxConcurrentCpuFfmpegOps VMAFs in flight at once
        //      (across all files).
        //    - With 2 files in flight, each gets ~1 NVENC continuously and
        //      shares the CPU pool with the other file's Phase 1 / VMAFs.
        //    - With 1 file in flight, that file uses BOTH NVENC engines for
        //      sample encodes — no idle silicon at the tail of a batch.
        // =======================================================
        var effectiveCqDesc = effectiveCq.OrderByDescending(x => x).ToList();
        var cqResults = new List<CqAggregate>();
        var phase2Sw = Stopwatch.StartNew();

        foreach (var cq in effectiveCqDesc)
        {
            ct.ThrowIfCancellationRequested();
            var cqSw = Stopwatch.StartNew();
            logger.SetStage("Phase 2", $"CQ={cq} (0/{windows.Count})");
            logger.LogInfo($"Phase 2: tuning CQ={cq}...");

            int doneSamples = 0;
            var sampleTasks = Enumerable.Range(0, windows.Count).Select(async i =>
            {
                string refClip = refClips[i];
                string tag = $"s{i:00}";
                string encPath = Path.Combine(samplesDir, $"cq{cq}_{tag}.mkv");
                string vmafLog = Path.Combine(vmafDir, $"cq{cq}_{tag}.json");

                // 1. GPU encode — competes for NVENC slot via GpuGate.
                using (await gpu.AcquireAsync(nvenc: 1, nvdec: 0, ct))
                    await Pipelines.EncodeSampleFromRefAsync(cfg, refClip, encPath, cq, format, ct);

                // 2. VMAF — competes for CPU slot via CpuGate.  Held only for
                //    the ffmpeg/libvmaf process; the JSON parse is unmetered.
                using (await cpu.AcquireAsync(ct))
                    await Pipelines.RunVmafDirectAsync(cfg, refClip, encPath, isHdr, hdrMetadata, format, vmafLog, vmafModelVersion, ct);

                var vmafResult = await Vmaf.ParseAsync(vmafLog, ct);
                int done = Interlocked.Increment(ref doneSamples);
                logger.SetStage("Phase 2", $"CQ={cq} ({done}/{windows.Count})");
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
            }).ToArray();

            var metrics = (await Task.WhenAll(sampleTasks)).ToList();
            cqSw.Stop();

            var agg = CqAggregate.From(cq, metrics);
            cqResults.Add(agg);

            logger.LogInfo($"  CQ={cq}: mean={agg.MeanVmaf:F2} harmonic={agg.HarmonicMeanVmaf:F2} " +
                $"p05={agg.P05Vmaf:F2} p01={agg.P01Vmaf:F2} min={agg.MinVmaf:F2} " +
                $"frames={agg.TotalFrameCount} ({cqSw.Elapsed.TotalSeconds:F1}s)");

            // Early termination: highest CQ passing all three gates is the answer.
            if (agg.MeanVmaf >= cfg.TargetMeanVmaf &&
                agg.P05Vmaf  >= cfg.TargetP05Vmaf  &&
                agg.P01Vmaf  >= cfg.TargetP01Vmaf)
            {
                logger.LogInfo($"CQ={cq} meets all thresholds " +
                    $"(mean≥{cfg.TargetMeanVmaf:F1}, p05≥{cfg.TargetP05Vmaf:F1}, " +
                    $"p01≥{cfg.TargetP01Vmaf:F1}) — stopping search.");
                break;
            }
        }

        phase2Sw.Stop();
        var selection = Selector.Select(cfg, cqResults);
        logger.LogInfo($"Phase 2 complete in {phase2Sw.Elapsed.TotalSeconds:F1}s ({cqResults.Count} CQ levels evaluated).");

        return new TuningResult
        {
            SampleWindows = windows,
            CqResults = cqResults,
            Selection = selection,

            BaseCqList = baseCq,
            EffectiveCqList = effectiveCq,
            HdrCqShiftApplied = hdrShiftApplied,
            HdrCqShiftDelta = hdrShiftApplied ? cfg.HdrCqLadderDelta : 0,

            Phase1Elapsed = phase1Sw.Elapsed,
            Phase2Elapsed = phase2Sw.Elapsed,
        };
    }
}
