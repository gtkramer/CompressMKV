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
///   Every op declares its cost via <see cref="ResourceRequest"/> and
///   acquires from the global <see cref="ResourcePool"/>.  Phase 1 ops
///   request CPU only; Phase 2 sample encodes request CPU + NVENC;
///   VMAF measurements request CPU + CUDA.  The pool's strict-FIFO
///   ordering means earlier files in the batch take priority; later
///   files run opportunistically when resources are free.
/// </summary>
public static class VmafTuner
{
    public static async Task<TuningResult> TuneAsync(
        Config cfg, ResourcePool pool, string input, string outDir,
        RestoreDecision restore, bool isHdr, HdrMetadata? hdrMetadata,
        PipelineFormat format, string vmafModelVersion, FfprobeStream vstream,
        CancellationToken ct, IPipelineLogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        FfprobeRoot probe = await Ffprobe.RunAsync(cfg, input, ct);
        double dur = probe.Format?.DurationSeconds ?? throw new InvalidOperationException("No duration.");

        ValidateSearchRange(cfg);

        Sampler sampler = new(cfg.RandomSeed);
        List<SampleWindow> windows = sampler.StratifiedRandomWindows(dur, cfg.SampleCount, cfg.SampleWindowSeconds);

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

        string refsDir = Path.Combine(outDir, "refs");
        string samplesDir = Path.Combine(outDir, "samples");
        string vmafDir = Path.Combine(outDir, "vmaf");
        Directory.CreateDirectory(refsDir);
        Directory.CreateDirectory(samplesDir);
        Directory.CreateDirectory(vmafDir);

        // =======================================================
        //  Phase 1: Extract lossless FFV1 reference clips.
        //  Each clip has the restore filter applied (IVTC/deinterlace)
        //  so it represents the true progressive reference for VMAF.
        //  Fast-seek (-ss before -i) → minimal decode overhead.
        //
        //  Throttled by ResourcePool — each extraction declares its CPU
        //  cost via cfg.RefExtractRequest; the pool admits as many as the
        //  CPU pool has room for, naturally balancing against Phase 2 /
        //  Detection / VMAF work on other files.
        // =======================================================
        logger.SetStage("Phase 1", $"extracting {windows.Count} reference clips");
        logger.LogInfo($"Phase 1: extracting {windows.Count} FFV1 reference clips (cadence-restored).");
        // Echo the chosen sample windows once so the decisions.log records
        // what the run actually measured — useful for reproducibility analysis
        // and to verify the seeded RNG produced the expected distribution.
        logger.LogInfo("Sample windows: " + string.Join(", ",
            windows.Select(w => $"{w.StartSeconds:F1}s+{w.LengthSeconds:F1}s")));
        Stopwatch phase1Sw = Stopwatch.StartNew();
        string[] refClips = new string[windows.Count];

        int extractedCount = 0;
        IReadOnlyList<ResourceRequest> refExtractAlternatives = cfg.RefExtractAlternativesFor(restore);
        // Phase 1 fires 16+ acquires per file in tight parallelism.  Per-acquire
        // logging would flood the per-file decisions.log, so we accumulate
        // wait stats here and emit one summary at phase end.  The structured
        // events.jsonl still has every individual acquire/release.
        int[] refWaitMs = new int[windows.Count];
        Task[] refTasks = windows.Select(async (w, i) =>
        {
            string refPath = Path.Combine(refsDir, $"ref_s{i:00}.mkv");
            AcquireResult admit = await pool.AcquireAnyAsync(
                refExtractAlternatives, ct, file: logger.VideoId, op: "phase1.ref-extract");
            refWaitMs[i] = admit.WaitMs;
            using (admit.Lease)
            {
                bool useHwaccel = admit.Granted.Nvdec > 0;
                await Pipelines.ExtractReferenceClipAsync(
                    cfg, input, refPath, w, restore, vstream, useHwaccel, admit.Granted.Cpu, ct);
            }
            refClips[i] = refPath;
            int done = Interlocked.Increment(ref extractedCount);
            logger.SetStage("Phase 1", $"extracted {done}/{windows.Count} refs");
        }).ToArray();

        await Task.WhenAll(refTasks);
        phase1Sw.Stop();
        int p1MaxWait = refWaitMs.Length > 0 ? refWaitMs.Max() : 0;
        double p1AvgWait = refWaitMs.Length > 0 ? refWaitMs.Average() : 0;
        logger.LogInfo(
            $"Phase 1 complete in {phase1Sw.Elapsed.TotalSeconds:F1}s " +
            $"({refWaitMs.Length} acquires; pool wait avg {p1AvgWait:F0}ms, max {p1MaxWait}ms).");

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
        //    1. acquire (CPU + NVENC) from ResourcePool → encode → release
        //    2. acquire (CPU + CUDA) from ResourcePool   → VMAF → release
        // =======================================================
        Stopwatch phase2Sw = Stopwatch.StartNew();
        int probeCount = 0;
        int expectedProbes = (int)Math.Ceiling(Math.Log2(cfg.MaxCq - cfg.MinCq + 2));
        List<CqAggregate> cqResults = [];
        // Probe order trajectory — CQ tried at each step and its pass/fail
        // verdict.  Logged at Phase 2 end so the decisions.log shows the
        // path the binary search actually walked, not just the final result.
        List<(int Cq, bool Pass)> trajectory = [];

        logger.LogInfo(
            $"Phase 2: binary search CQ in [{cfg.MinCq}..{cfg.MaxCq}] " +
            $"(≤{expectedProbes} probes).");

        int? bestPassingCq = await CqBinarySearch.FindHighestPassingAsync(
            cfg.MinCq, cfg.MaxCq,
            async cq =>
            {
                probeCount++;
                Stopwatch probeSw = Stopwatch.StartNew();
                logger.SetStage("Phase 2", $"probe {probeCount} CQ={cq}");
                logger.LogInfo($"Phase 2 probe {probeCount}/~{expectedProbes}: CQ={cq}...");

                CqAggregate agg = await ProbeCqAsync(
                    cfg, pool, refClips, windows, samplesDir, vmafDir,
                    isHdr, hdrMetadata, format, vmafModelVersion,
                    cq, logger, ct);
                probeSw.Stop();
                cqResults.Add(agg);

                bool pass = agg.MeanVmaf >= cfg.TargetMeanVmaf
                         && agg.P05Vmaf  >= cfg.TargetP05Vmaf
                         && agg.P01Vmaf  >= cfg.TargetP01Vmaf;

                trajectory.Add((cq, pass));

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
        Selection selection = Selector.Select(cfg, cqResults.OrderBy(r => r.Cq).ToList());

        // One-line search-trajectory recap before the phase-end summary, so a
        // reader can see exactly which CQs were probed and in what order.
        logger.LogInfo("Search trajectory: " + string.Join(" → ",
            trajectory.Select(t => $"CQ={t.Cq} {(t.Pass ? "PASS" : "FAIL")}"))
            + $"; selected CQ={selection.SelectedCq}.");

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
        Config cfg, ResourcePool pool, string[] refClips,
        IReadOnlyList<SampleWindow> windows, string samplesDir, string vmafDir,
        bool isHdr, HdrMetadata? hdrMetadata, PipelineFormat format,
        string vmafModelVersion,
        int cq, IPipelineLogger logger, CancellationToken ct)
    {
        int doneSamples = 0;
        // Per-probe pool wait accumulation (per window).  Aggregated into a
        // probe summary at the bottom — individual acquires still emit their
        // own structured events into events.jsonl.
        int[] sampleWaitMs = new int[windows.Count];
        int[] vmafWaitMs   = new int[windows.Count];

        Task<SampleMetric>[] sampleTasks = Enumerable.Range(0, windows.Count).Select(async i =>
        {
            string refClip = refClips[i];
            string tag = $"s{i:00}";
            string encPath = Path.Combine(samplesDir, $"cq{cq}_{tag}.mkv");
            string vmafLog = Path.Combine(vmafDir, $"cq{cq}_{tag}.json");

            // 1. Sample encode — declares CPU (for FFV1 decode + orchestration)
            //    and one NVENC slot.
            AcquireResult sampleAdmit = await pool.AcquireAnyAsync(
                [cfg.SampleEncodeRequest], ct,
                file: logger.VideoId, op: $"phase2.sample-encode.cq{cq}");
            sampleWaitMs[i] = sampleAdmit.WaitMs;
            using (sampleAdmit.Lease)
                await Pipelines.EncodeSampleFromRefAsync(cfg, refClip, encPath, cq, format, ct);

            // 2. VMAF — declares CPU (for FFV1+AV1 decode + zscale/tonemap on
            //    HDR + format conversion) and one CUDA lane.  See
            //    Pipelines.RunVmafDirectAsync for the HDR tonemap chain.
            AcquireResult vmafAdmit = await pool.AcquireAnyAsync(
                [cfg.VmafRequest], ct,
                file: logger.VideoId, op: $"phase2.vmaf.cq{cq}");
            vmafWaitMs[i] = vmafAdmit.WaitMs;
            using (vmafAdmit.Lease)
                await Pipelines.RunVmafDirectAsync(cfg, refClip, encPath, isHdr, hdrMetadata, format, vmafLog, vmafModelVersion, ct);

            VmafResult vmafResult = await Vmaf.ParseAsync(vmafLog, ct);
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

        List<SampleMetric> metrics = (await Task.WhenAll(sampleTasks)).ToList();
        logger.LogInfo(
            $"  CQ={cq} pool waits: sample-encode avg {sampleWaitMs.Average():F0}ms / " +
            $"max {sampleWaitMs.Max()}ms; vmaf avg {vmafWaitMs.Average():F0}ms / max {vmafWaitMs.Max()}ms.");
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
