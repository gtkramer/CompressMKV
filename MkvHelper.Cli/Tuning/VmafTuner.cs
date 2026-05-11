using System.Diagnostics;

namespace MkvHelper;

/// <summary>
/// VMAF-guided CQ tuning with two-phase pipeline:
///
///   Phase 1: Extract lossless FFV1 reference clips (cadence-restored) —
///            one per sample window.  Reused across every CQ probe.
///
///   Phase 2: Binary-search the integer CQ range [MinCq, MaxCq] for the
///            highest CQ where the encode meets all three VMAF gates
///            (mean / p05 / p01).  Each probe encodes every reference
///            clip at the candidate CQ and aggregates per-frame VMAF
///            scores across them.
///
/// Binary search converges in ~log2(MaxCq - MinCq + 1) probes regardless
/// of where the answer lies — uniformly bounded vs. the older linear
/// high→low ladder, which was fast on easy content but slow on hard
/// content.  HDR sources are not given a fixed CQ shift; if HDR
/// quantization is more visible at a given CQ, libvmaf_cuda will score it
/// lower, fail the gates, and the search will descend on its own.
///
/// Concurrency model:
///   - CPU-heavy ffmpeg ops (ref extraction, VMAF) gate on a global
///     <see cref="CpuGate"/> so work flows naturally across files
///     without oversubscribing the CPU.
///   - GPU NVENC sample encodes gate on <see cref="GpuGate"/> separately
///     — they're mostly idle on CPU and run in parallel with CPU-heavy VMAF.
///   - Per-process thread counts come from <see cref="Config.FfmpegCpuThreads"/>,
///     <see cref="Config.FfmpegGpuThreads"/>, and <see cref="Config.LibvmafThreads"/>.
/// </summary>
public static class VmafTuner
{
    public static async Task<TuningResult> TuneAsync(
        Config cfg, GpuGate gpu, CpuGate cpu, string input, string outDir,
        RestoreDecision restore, bool isHdr, HdrMetadata? hdrMetadata,
        PipelineFormat format, string vmafModelVersion, bool useCudaVmaf,
        CancellationToken ct, IPipelineLogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        var probe = await Ffprobe.RunAsync(cfg, input, ct);
        var dur = probe.Format?.DurationSeconds ?? throw new InvalidOperationException("No duration.");

        ValidateSearchRange(cfg);

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
        //  Phase 2: Binary search the [MinCq, MaxCq] integer range
        //  for the highest CQ that passes all three VMAF gates.
        //
        //  At each step:
        //    mid = (lo + hi + 1) / 2          // bias upward on ties
        //    encode + measure every ref at CQ=mid
        //    if all three gates pass: lo = mid     (try a higher CQ)
        //    else:                    hi = mid - 1 (try a lower CQ)
        //  Converges when lo == hi.  Result is whichever CQ ended up at
        //  `lo`, with a fallback to "best mean" if nothing ever passed.
        //
        //  Each probe fires all sample tasks in parallel:
        //    1. acquire NVENC slot (GpuGate) → encode → release
        //    2. acquire CUDA-VMAF or CPU slot → VMAF → release
        // =======================================================
        var phase2Sw = Stopwatch.StartNew();
        int probeCount = 0;
        int expectedProbes = (int)Math.Ceiling(Math.Log2(cfg.MaxCq - cfg.MinCq + 2));
        var cqResults = new List<CqAggregate>();

        logger.LogInfo(
            $"Phase 2: binary search CQ in [{cfg.MinCq}..{cfg.MaxCq}] " +
            $"(≤{expectedProbes} probes).");

        var bestPassingCq = await CqBinarySearch.FindHighestPassingAsync(
            cfg.MinCq, cfg.MaxCq,
            async cq =>
            {
                probeCount++;
                var probeSw = Stopwatch.StartNew();
                logger.SetStage("Phase 2", $"probe {probeCount} CQ={cq}");
                logger.LogInfo($"Phase 2 probe {probeCount}/~{expectedProbes}: CQ={cq}...");

                var agg = await ProbeCqAsync(
                    cfg, gpu, cpu, refClips, windows, samplesDir, vmafDir,
                    isHdr, hdrMetadata, format, vmafModelVersion, useCudaVmaf,
                    cq, logger, ct);
                probeSw.Stop();
                cqResults.Add(agg);

                bool pass = agg.MeanVmaf >= cfg.TargetMeanVmaf
                         && agg.P05Vmaf  >= cfg.TargetP05Vmaf
                         && agg.P01Vmaf  >= cfg.TargetP01Vmaf;

                logger.LogInfo(
                    $"  CQ={cq}: mean={agg.MeanVmaf:F2} harmonic={agg.HarmonicMeanVmaf:F2} " +
                    $"p05={agg.P05Vmaf:F2} p01={agg.P01Vmaf:F2} min={agg.MinVmaf:F2} " +
                    $"frames={agg.TotalFrameCount} ({probeSw.Elapsed.TotalSeconds:F1}s) — " +
                    (pass ? "[PASS]" : "[FAIL]"));

                return pass;
            }, ct);

        phase2Sw.Stop();

        // Selection: the highest passing CQ if any probe passed; otherwise
        // Selector falls back to the CQ with the best mean and flags MARGINAL.
        // Sort the probe list by CQ (not probe order) so the selector sees a
        // canonical view.
        var selection = Selector.Select(cfg, cqResults.OrderBy(r => r.Cq).ToList());
        logger.LogInfo(
            $"Phase 2 complete in {phase2Sw.Elapsed.TotalSeconds:F1}s " +
            $"({probeCount} probe(s); " +
            (bestPassingCq is not null
                ? $"selected CQ={selection.SelectedCq})."
                : "no CQ met all gates — fell back to best mean)."));

        return new TuningResult
        {
            SampleWindows = windows,
            CqResults = cqResults,
            Selection = selection,
            SearchMinCq = cfg.MinCq,
            SearchMaxCq = cfg.MaxCq,
            Phase1Elapsed = phase1Sw.Elapsed,
            Phase2Elapsed = phase2Sw.Elapsed,
        };
    }

    /// <summary>
    /// Encode every reference clip at <paramref name="cq"/>, measure VMAF,
    /// aggregate.  Sample tasks fire in parallel and self-throttle via the
    /// GPU/CPU gates.
    /// </summary>
    private static async Task<CqAggregate> ProbeCqAsync(
        Config cfg, GpuGate gpu, CpuGate cpu, string[] refClips,
        IReadOnlyList<SampleWindow> windows, string samplesDir, string vmafDir,
        bool isHdr, HdrMetadata? hdrMetadata, PipelineFormat format,
        string vmafModelVersion, bool useCudaVmaf,
        int cq, IPipelineLogger logger, CancellationToken ct)
    {
        int doneSamples = 0;
        var sampleTasks = Enumerable.Range(0, windows.Count).Select(async i =>
        {
            string refClip = refClips[i];
            string tag = $"s{i:00}";
            string encPath = Path.Combine(samplesDir, $"cq{cq}_{tag}.mkv");
            string vmafLog = Path.Combine(vmafDir, $"cq{cq}_{tag}.json");

            // 1. GPU encode — competes for NVENC slot via GpuGate.
            using (await gpu.AcquireAsync(nvenc: 1, nvdec: 0, cuda: 0, ct))
                await Pipelines.EncodeSampleFromRefAsync(cfg, refClip, encPath, cq, format, ct);

            // 2. VMAF — gate selection follows the per-file decision the
            //    caller already resolved: GpuGate.Cuda when libvmaf_cuda
            //    will run, CpuGate when CPU libvmaf will run.
            if (useCudaVmaf)
            {
                using (await gpu.AcquireAsync(nvenc: 0, nvdec: 0, cuda: 1, ct))
                    await Pipelines.RunVmafDirectAsync(cfg, refClip, encPath, isHdr, hdrMetadata, format, vmafLog, vmafModelVersion, useCudaVmaf, ct);
            }
            else
            {
                using (await cpu.AcquireAsync(ct))
                    await Pipelines.RunVmafDirectAsync(cfg, refClip, encPath, isHdr, hdrMetadata, format, vmafLog, vmafModelVersion, useCudaVmaf, ct);
            }

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
        return CqAggregate.From(cq, metrics);
    }

    /// <summary>
    /// Catch obviously-bad search ranges before kicking off Phase 1.  All
    /// of these would otherwise produce confusing late failures (or, worse,
    /// silently undefined behavior in the binary search loop).
    /// </summary>
    private static void ValidateSearchRange(Config cfg)
    {
        if (cfg.MinCq < 0 || cfg.MinCq > 63)
            throw new InvalidOperationException($"Config.MinCq={cfg.MinCq} is out of range [0..63] (NVENC AV1).");
        if (cfg.MaxCq < 0 || cfg.MaxCq > 63)
            throw new InvalidOperationException($"Config.MaxCq={cfg.MaxCq} is out of range [0..63] (NVENC AV1).");
        if (cfg.MinCq > cfg.MaxCq)
            throw new InvalidOperationException($"Config.MinCq={cfg.MinCq} > MaxCq={cfg.MaxCq}.");
    }
}
