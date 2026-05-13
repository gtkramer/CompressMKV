using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MkvHelper;

public sealed class CompressSettings : CommandSettings
{
    [CommandOption("-i|--input <FOLDER>")]
    [Description("Folder containing input video files.  Discovered via ffprobe (extension-agnostic).")]
    public string? Input { get; init; }

    [CommandOption("-o|--output <FOLDER>")]
    [Description("Output folder for encoded files + per-file logs.  Defaults to ./out.")]
    public string? Output { get; init; }

    [CommandOption("--force-redo")]
    [Description("Ignore prior log.json files and re-process every input.")]
    public bool ForceRedo { get; init; }
}

/// <summary>
/// Main entry point for `mkvhelper compress`.  Bootstraps the container
/// (auto-builds if missing), then hands off to the parallel work loop.
/// All ffmpeg/ffprobe invocations downstream go through the container
/// via <see cref="ContainerTools"/>.
/// </summary>
public sealed class CompressCommand : AsyncCommand<CompressSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CompressSettings settings, CancellationToken token)
    {
        using CancellationTokenSource cts = ConsoleCancellation.LinkToConsole(token);

        if (string.IsNullOrWhiteSpace(settings.Input))
        {
            AnsiConsole.MarkupLine("[red]--input is required.[/]  See `mkvhelper compress --help`.");
            return 2;
        }

        Config cfg = BuildConfig(settings.Input!, settings.Output);

        if (!Directory.Exists(cfg.InputFolder))
        {
            AnsiConsole.MarkupLine($"[red]Input folder not found:[/] {Markup.Escape(cfg.InputFolder)}");
            AnsiConsole.MarkupLine("[grey]Tip: paths with spaces must be quoted, e.g. --input \"/some/path with spaces\"[/]");
            return 2;
        }

        Directory.CreateDirectory(cfg.OutputFolder);

        // Ensure the dependency container is built and configure ContainerTools
        // to route ffmpeg/ffprobe through it with input + output dirs mounted.
        BuildState state;
        try
        {
            state = await ContainerBuilder.EnsureReadyAsync(
                mounts: [cfg.InputFolder, cfg.OutputFolder],
                ct: cts.Token);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Container setup failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }
        AnsiConsole.MarkupLine(
            $"[grey]Using container[/] [bold]{Markup.Escape(state.ImageTag)}[/] " +
            $"[grey](built {state.BuiltUtc:yyyy-MM-dd HH:mm}Z).[/]");

        List<DiscoveredVideo> discovered = await VideoFileDiscovery.DiscoverAsync(cfg, cfg.InputFolder, cts.Token);

        AnsiConsole.MarkupLine($"[bold]Found {discovered.Count} video file(s)[/] under {Markup.Escape(cfg.InputFolder)}");
        if (discovered.Count == 0)
        {
            AnsiConsole.MarkupLine(
                "[yellow]No video files to process.[/]  If the input path contains spaces, " +
                "make sure to quote it on the command line.");
            return 0;
        }
        AnsiConsole.MarkupLine(
            $"[grey]Resource pool:[/] {cfg.CpuPool} CPU threads, " +
            $"{cfg.NvencSlots} NVENC + {cfg.NvdecSlots} NVDEC + {cfg.CudaSlots} CUDA lanes.  " +
            $"[grey]Files admit unbounded; pool gates per-op.[/]");
        if (settings.ForceRedo)
            AnsiConsole.MarkupLine("[yellow]--force-redo:[/] existing log.json files will be ignored and files re-processed.");
        AnsiConsole.WriteLine();

        // Initialize Serilog as the run-wide logger.  Two file sinks open
        // events.jsonl (machine-readable, canonical for post-hoc analysis)
        // and events.log (human-readable mirror).  Per-file loggers come up
        // independently inside the work loop and forward events here too.
        Serilog.Log.Logger = LoggerSetup.BuildGlobalLogger(cfg.OutputFolder);
        Serilog.Log.Logger.Information(
            "Run start: input={Input}, output={Output}, pool=CPU:{Cpu} NVENC:{Nvenc} " +
            "NVDEC:{Nvdec} CUDA:{Cuda}, files={FileCount}, forceRedo={ForceRedo}",
            cfg.InputFolder, cfg.OutputFolder, cfg.CpuPool, cfg.NvencSlots,
            cfg.NvdecSlots, cfg.CudaSlots, discovered.Count, settings.ForceRedo);

        ResourcePool pool = new(cfg.CpuPool, cfg.NvencSlots, cfg.NvdecSlots, cfg.CudaSlots);
        OverallSummary overall = new() { StartedUtc = DateTime.UtcNow, Config = cfg };
        StageReporter reporter = new(discovered.Count);

        // Background sampler: real CPU + GPU + pool snapshot every N seconds.
        // Disabled when SystemSamplerIntervalSeconds is 0.
        SystemSampler? sampler = cfg.SystemSamplerIntervalSeconds > 0
            ? new SystemSampler(pool, TimeSpan.FromSeconds(cfg.SystemSamplerIntervalSeconds), cts.Token)
            : null;

        Task workTask = ProcessAllFilesAsync(
            cfg, pool, discovered, settings.ForceRedo,
            overall, reporter, cts.Token);

        await AnsiConsole.Live(reporter.BuildRenderable())
            .AutoClear(false)
            .Overflow(VerticalOverflow.Ellipsis)
            .StartAsync(async ctx =>
            {
                while (!workTask.IsCompleted)
                {
                    ctx.UpdateTarget(reporter.BuildRenderable());
                    try { await Task.Delay(200, cts.Token); }
                    catch (OperationCanceledException) { break; }
                }
                ctx.UpdateTarget(reporter.BuildRenderable());
            });

        try { await workTask; }
        catch (OperationCanceledException) { /* user-initiated */ }

        // Snapshot the sampler BEFORE disposing so the run summary has the
        // pool-vs-hardware comparison even on graceful shutdown.  The sampler's
        // accumulator is safe to read concurrently, but we wait for the work
        // task to settle first to get a stable end-of-run view.
        SystemSamplerSummary? samplerSummary = sampler?.Summarize();
        overall.SamplerSummary = samplerSummary;
        if (sampler is not null) await sampler.DisposeAsync();

        DateTime finishedUtc = DateTime.UtcNow;
        overall.FinishedUtc = finishedUtc;
        string overallPath = Path.Combine(cfg.OutputFolder, $"overall_{finishedUtc:yyyyMMdd_HHmmss}.json");
        await JsonIO.WriteAsync(overallPath, overall, cts.Token);

        Serilog.Log.Logger.Information(
            "Run end: completed={Completed}, errors={Errors}, duration={DurationS:F1}s; " +
            "sampler={@Sampler}",
            overall.Videos.Count - overall.Errors.Count, overall.Errors.Count,
            (finishedUtc - overall.StartedUtc).TotalSeconds,
            samplerSummary);
        await Serilog.Log.CloseAndFlushAsync();

        AnsiConsole.WriteLine();
        Rule rule = new("[bold]Run summary[/]") { Justification = Justify.Left };
        AnsiConsole.Write(rule);
        AnsiConsole.MarkupLine($"  [green]Completed:[/] {overall.Videos.Count - overall.Errors.Count} files");
        AnsiConsole.MarkupLine($"  [yellow]Skipped via resume:[/] {overall.Videos.Count(v => v.Tuning == null && v.OutputVerification == null)}");
        if (overall.Errors.Count > 0)
            AnsiConsole.MarkupLine($"  [red]Errored:[/] {overall.Errors.Count}");
        int marginalCount = overall.Videos.Count(v => v.Tuning?.Selection?.IsMarginal == true);
        if (marginalCount > 0)
            AnsiConsole.MarkupLine($"  [yellow]Marginal passes:[/] {marginalCount}");
        int passthroughCount = overall.Videos.Count(v => v.SizeGuard?.FellBack == true);
        if (passthroughCount > 0)
            AnsiConsole.MarkupLine($"  [yellow]Passthrough fallback:[/] {passthroughCount} (source already efficiently compressed)");

        RenderSamplerSummary(samplerSummary);

        AnsiConsole.MarkupLine($"  [grey]Wrote {overallPath}[/]");

        return overall.Errors.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Side-by-side render of pool declared-busy% next to the real CPU/GPU/
    /// NVENC numbers, plus a one-line hint when a pool is ≥ 75% busy while
    /// the hardware proxy says ≤ 30% — the textbook "raise the slot count"
    /// signal.  Heuristic, not authoritative: a single hint can be wrong
    /// (e.g. on very short runs with too few samples), so we mark it as
    /// "consider" rather than "do".
    /// </summary>
    private static void RenderSamplerSummary(SystemSamplerSummary? s)
    {
        if (s is null || s.SampleCount == 0) return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"  [bold]Pool vs hardware[/] [grey](averaged over {s.SampleCount} samples)[/]");

        // Each line: declared pool busy% next to the closest real-hardware
        // proxy.  GPU% is the same proxy for NVDEC and CUDA — nvidia-smi
        // doesn't break out decode/compute, and that's fine for the
        // over/under-subscription signal we're looking for.
        AnsiConsole.MarkupLine(
            $"    [grey]CPU   [/] pool [yellow]{s.CpuBusyPct,5:F1}%[/] busy / actual CPU [cyan]{s.CpuPctAvg,5:F1}%[/] avg, peak {s.CpuPctMax:F0}%");

        string nvencActual = s.NvencSessionAvg is { } na
            ? $"NVENC sessions [cyan]{na:F1}[/] avg of {s.NvencTotal} declared (peak {s.NvencSessionMax})"
            : "[grey]NVENC sessions n/a[/]";
        AnsiConsole.MarkupLine(
            $"    [grey]NVENC [/] pool [yellow]{s.NvencBusyPct,5:F1}%[/] busy / {nvencActual}");

        string gpuActualNvdec = s.GpuPctAvg is { } g1
            ? $"GPU compute [cyan]{g1,5:F1}%[/] avg, peak {s.GpuPctMax}%"
            : "[grey]GPU compute n/a[/]";
        AnsiConsole.MarkupLine(
            $"    [grey]NVDEC [/] pool [yellow]{s.NvdecBusyPct,5:F1}%[/] busy / {gpuActualNvdec}");

        string gpuActualCuda = s.GpuPctAvg is { } g2
            ? $"GPU compute [cyan]{g2,5:F1}%[/] avg, peak {s.GpuPctMax}%"
            : "[grey]GPU compute n/a[/]";
        AnsiConsole.MarkupLine(
            $"    [grey]CUDA  [/] pool [yellow]{s.CudaBusyPct,5:F1}%[/] busy / {gpuActualCuda}");

        // Hints — pool ≥ 75% busy but the hardware proxy says ≤ 30%.
        List<string> hints = [];
        const double BusyThreshold = 75.0;
        const double IdleThreshold = 30.0;
        if (s.CpuBusyPct >= BusyThreshold && s.CpuPctAvg <= IdleThreshold)
            hints.Add($"CPU pool {s.CpuBusyPct:F0}% busy but actual CPU only {s.CpuPctAvg:F0}% — declared thread costs may be inflated.");
        if (s.NvencBusyPct >= BusyThreshold && s.NvencSessionAvg is { } nv && s.NvencTotal > 0 && nv / s.NvencTotal * 100 <= IdleThreshold)
            hints.Add($"NVENC pool {s.NvencBusyPct:F0}% busy but only {nv:F1} of {s.NvencTotal} sessions used on average — consider raising NvencSlots.");
        if (s.NvdecBusyPct >= BusyThreshold && s.GpuPctAvg is { } gd && gd <= IdleThreshold)
            hints.Add($"NVDEC pool {s.NvdecBusyPct:F0}% busy but GPU only {gd:F1}% — consider raising NvdecSlots.");
        if (s.CudaBusyPct >= BusyThreshold && s.GpuPctAvg is { } gc && gc <= IdleThreshold)
            hints.Add($"CUDA pool {s.CudaBusyPct:F0}% busy but GPU only {gc:F1}% — consider raising CudaSlots.");

        foreach (string h in hints)
            AnsiConsole.MarkupLine($"    [yellow]Hint:[/] {Markup.Escape(h)}");
    }

    // ----------------------------------------------------------------
    //  Per-file work loop
    // ----------------------------------------------------------------

    private static async Task ProcessAllFilesAsync(
        Config cfg, ResourcePool pool, List<DiscoveredVideo> files,
        bool forceRedo, OverallSummary overall, StageReporter reporter, CancellationToken ct)
    {
        // Local lock guarding writes to the shared OverallSummary (Videos +
        // Errors lists) from parallel per-file workers.  A dedicated lock
        // object is preferred over locking on `overall` itself: it keeps the
        // synchronization invariant visible at one site and avoids implicit
        // contention with any future use of the OverallSummary instance.
        Lock overallLock = new();

        // Files admit unbounded — every file's tasks queue against the pool
        // immediately on startup.  The pool's strict-FIFO ordering means the
        // earlier files in the list naturally get resources first; later
        // files opportunistically run their CPU-only phases (Detection,
        // Phase 1, Verification) while earlier files hold the GPU pools
        // for Phase 2 sample encodes and the final encode.
        await Parallel.ForEachAsync(
            files.Select((dv, i) => (dv, idx: i + 1)),
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = -1,
            },
            async (item, fileCt) =>
            {
                (DiscoveredVideo discovered, int idx) = item;
                string file = discovered.Path;
                string id = SafeId(Path.GetFileNameWithoutExtension(file));
                string outDir = Path.Combine(cfg.OutputFolder, id);
                string logPath = Path.Combine(outDir, "log.json");
                string fileName = Path.GetFileName(file);

                if (!forceRedo && File.Exists(logPath))
                {
                    try
                    {
                        VideoSummary? prior = await JsonIO.ReadAsync<VideoSummary>(logPath, fileCt);
                        if (prior != null)
                        {
                            lock (overallLock) overall.Videos.Add(prior);
                            reporter.SkipFile(idx, fileName, "prior log.json found");
                            return;
                        }
                    }
                    catch { /* corrupt — reprocess */ }
                }

                if (forceRedo && File.Exists(logPath)) File.Delete(logPath);
                CleanIntermediates(outDir);
                Directory.CreateDirectory(outDir);

                int slot = idx;
                reporter.BeginFile(slot, idx, fileName);

                using PerFileLogger logger = new(
                    Path.Combine(outDir, "decisions.log"), id, reporter, slot);

                try
                {
                    VideoSummary summary = await ProcessOneAsync(cfg, pool, file, discovered.Probe, fileCt, logger);
                    lock (overallLock) overall.Videos.Add(summary);
                    (string resultText, ResultLevel level) = SummariseResult(summary);
                    reporter.CompleteFile(slot, resultText, level);
                }
                catch (OperationCanceledException)
                {
                    reporter.CompleteFile(slot, "cancelled", ResultLevel.Failure);
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex.ToString());
                    lock (overallLock) overall.Errors.Add(new RunError { File = file, Error = ex.ToString() });
                    reporter.CompleteFile(slot, $"error: {Truncate(ex.Message, 60)}", ResultLevel.Failure);
                }
            });
    }

    private static (string text, ResultLevel level) SummariseResult(VideoSummary s)
    {
        bool marginal = s.Tuning?.Selection?.IsMarginal == true;
        bool verifyFailed = s.OutputVerification?.Passed == false;
        bool fellBack = s.SizeGuard?.FellBack == true;

        if (verifyFailed)
        {
            string label = fellBack ? "passthrough" : $"CQ={s.FinalCq}";
            return ($"verify FAILED — {label}", ResultLevel.Failure);
        }
        if (fellBack)
            return ("⚠ passthrough — source already compressed", ResultLevel.Warning);
        if (marginal)
            return ($"⚠ marginal — CQ={s.FinalCq}", ResultLevel.Warning);
        return ($"✓ CQ={s.FinalCq}", ResultLevel.Success);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Config BuildConfig(string input, string? output) => new()
    {
        InputFolder = input,
        OutputFolder = output ?? "./out",

        // Pool capacities — defaults are the right values for the target
        // machine (20-core CPU + RTX 5080).  See Config docs.
        NvencSlots = 4,
        NvdecSlots = 4,
        CudaSlots = 4,

        // CQ binary search range — resolution-tiered.  Each tier ≤ 31 wide
        // so search completes in ≤ 5 probes regardless of where the answer
        // lands.  Tier-aware ranges put the first probe near the expected
        // answer cluster for that resolution class (see Config docs).
        MinCqUhd = 25, MaxCqUhd = 55,   // first probe 40
        MinCqFhd = 28, MaxCqFhd = 58,   // first probe 43
        MinCqHd  = 32, MaxCqHd  = 62,   // first probe 47
        MinCqSd  = 38, MaxCqSd  = 63,   // first probe 51

        SampleCount = 16,
        SampleWindowSeconds = 12,
        RandomSeed = 12345,

        TargetMeanVmaf = 97.0,
        TargetP05Vmaf = 95.0,
        TargetP01Vmaf = 90.0,

        NvencPreset = "p7",
        RcLookahead = 48,

        OutputExtension = ".mkv",
    };

    private static async Task<VideoSummary> ProcessOneAsync(
        Config cfg, ResourcePool pool, string input, FfprobeRoot probe,
        CancellationToken ct, IPipelineLogger logger)
    {
        await Task.CompletedTask;
        Stopwatch totalSw = Stopwatch.StartNew();
        PhaseTimings timings = new();
        string id = SafeId(Path.GetFileNameWithoutExtension(input));
        string outDir = Path.Combine(cfg.OutputFolder, id);
        Directory.CreateDirectory(outDir);

        logger.LogInfo($"Input: {input}");
        logger.LogInfo($"Output dir: {outDir}");
        logger.LogInfo($"Container image: {ContainerTools.ImageTag}");

        FfprobeStream vstream = probe.Streams?.FirstOrDefault(s => s.IsActualVideo())
                     ?? throw new InvalidOperationException("No usable video stream found.");

        bool isHdr = SourceClassifier.IsHdr(vstream);
        logger.LogInfo($"HDR: {isHdr}");

        HdrMetadata? hdrMetadata = isHdr
            ? await SourceClassifier.ExtractHdrMetadataAsync(cfg, input, ct)
            : null;
        if (isHdr)
        {
            int npl = (hdrMetadata ?? new HdrMetadata()).ResolveNpl();
            string source = hdrMetadata?.MaxCll is { } ? "MaxCLL"
                          : hdrMetadata?.MasteringDisplayMaxLuminance is { } ? "mastering display"
                          : "HDR10 default (no metadata found)";
            logger.LogInfo($"HDR npl: {npl} nits (from {source})");
        }

        PipelineFormat pipelineFormat = PipelineFormat.FromStream(vstream);
        logger.LogInfo($"Pipeline format: {pipelineFormat}");

        string vmafModelVersion = cfg.ResolveVmafModelVersion(vstream.Width, vstream.Height);
        logger.LogInfo($"VMAF model: {vmafModelVersion} (source {vstream.Width}x{vstream.Height})");

        Stopwatch sw = Stopwatch.StartNew();
        ContentDetectionResult detection;
        AcquireResult detectAdmit = await pool.AcquireAnyAsync(
            cfg.DetectionAlternatives, ct, file: id, op: "detection");
        Stopwatch detectHold = Stopwatch.StartNew();
        using (detectAdmit.Lease)
        {
            bool useHwaccel = detectAdmit.Granted.Nvdec > 0;
            logger.LogInfo(
                $"Detection acquired: decoder={(useHwaccel ? "NVDEC" : "CPU")} " +
                $"(alt {detectAdmit.AlternativeIndex}, waited {detectAdmit.WaitMs}ms); " +
                $"pool now {FormatPool(detectAdmit.PoolAfter, pool)}");
            detection = await ContentDetector.DetectAsync(cfg, input, vstream, useHwaccel, ct, logger);
        }
        detectHold.Stop();
        logger.LogInfo(
            $"Detection released: held resources for {detectHold.Elapsed.TotalSeconds:F1}s " +
            $"(wait was {detectAdmit.WaitMs}ms).");
        timings.Detection = sw.Elapsed;

        RestoreDecision restore = RestoreStrategyMapper.MapToRestore(detection);
        logger.LogInfo($"Restore decision: {restore.Mode} (filter=\"{restore.FilterGraph}\", fps={restore.OutputFps?.ToString() ?? "native"})");
        logger.LogInfo($"  Reason: {restore.Notes}");

        logger.LogInfo(isHdr
            ? "VMAF execution: GPU (libvmaf_cuda); AV1 sample decode on NVDEC; HDR tonemap chain on CPU"
            : "VMAF execution: GPU (libvmaf_cuda); AV1 sample decode on NVDEC");

        TuningResult tuning = await VmafTuner.TuneAsync(cfg, pool, input, outDir, restore,
            isHdr, hdrMetadata, pipelineFormat, vmafModelVersion, vstream, ct, logger);
        timings.TuningPhase1 = tuning.Phase1Elapsed;
        timings.TuningPhase2 = tuning.Phase2Elapsed;

        if (tuning.Selection.IsMarginal)
        {
            logger.LogWarning("Selection is MARGINAL — within "
                + $"{Selector.MarginalThresholdPoints:F1} VMAF points of threshold:");
            foreach (string r in tuning.Selection.MarginalReasons)
                logger.LogWarning($"  ! {r}");
        }

        int finalCq = tuning.Selection.SelectedCq;
        string finalOut = Path.Combine(outDir, $"{id}_av1_cq{finalCq}{cfg.OutputExtension}");
        sw.Restart();
        await FinalEncoder.EncodeAsync(cfg, pool, input, finalOut, restore, finalCq, pipelineFormat, ct, logger);
        timings.FinalEncode = sw.Elapsed;
        logger.LogInfo($"Final encode written: {finalOut}");

        // SizeGuard does the (instant) size check inline; it acquires a small
        // CPU slice from the pool only if the remux fallback actually fires.
        SizeGuardOutcome sizeGuard = await SizeGuard.MaybeFallbackAsync(cfg, pool, input, finalOut, restore, logger, ct);
        finalOut = sizeGuard.OutputPath;

        sw.Restart();
        OutputVerificationResult verification;
        AcquireResult verifyAdmit = await pool.AcquireAnyAsync(
            cfg.VerificationAlternatives, ct, file: id, op: "verification");
        Stopwatch verifyHold = Stopwatch.StartNew();
        using (verifyAdmit.Lease)
        {
            bool useHwaccel = verifyAdmit.Granted.Nvdec > 0;
            logger.LogInfo(
                $"Verification acquired: decoder={(useHwaccel ? "NVDEC" : "CPU")} " +
                $"(alt {verifyAdmit.AlternativeIndex}, waited {verifyAdmit.WaitMs}ms); " +
                $"pool now {FormatPool(verifyAdmit.PoolAfter, pool)}");
            verification = await OutputVerifier.VerifyAsync(cfg, finalOut, restore, useHwaccel, ct, logger);
        }
        verifyHold.Stop();
        logger.LogInfo(
            $"Verification released: held resources for {verifyHold.Elapsed.TotalSeconds:F1}s " +
            $"(wait was {verifyAdmit.WaitMs}ms).");
        timings.Verification = sw.Elapsed;
        timings.Total = totalSw.Elapsed;

        logger.LogInfo(
            $"Timings (total {timings.Total.TotalSeconds:F1}s): " +
            $"detect={timings.Detection.TotalSeconds:F1}s, " +
            $"tune.p1={timings.TuningPhase1.TotalSeconds:F1}s, " +
            $"tune.p2={timings.TuningPhase2.TotalSeconds:F1}s, " +
            $"final={timings.FinalEncode.TotalSeconds:F1}s, " +
            $"verify={timings.Verification.TotalSeconds:F1}s.");

        VideoSummary summary = new()
        {
            VideoId = id,
            InputPath = input,
            FinalOutputPath = finalOut,
            GeneratedUtc = DateTime.UtcNow,
            Probe = probe,
            IsHdr = isHdr,
            ContentDetection = detection,
            Restore = restore,
            Tuning = tuning,
            FinalCq = finalCq,
            OutputVerification = verification,
            SizeGuard = sizeGuard,
            Timings = timings,
            HdrMetadata = hdrMetadata,
        };

        await JsonIO.WriteAsync(Path.Combine(outDir, "log.json"), summary, ct);
        CleanIntermediates(outDir);

        return summary;
    }

    private static void CleanIntermediates(string outDir)
    {
        foreach (string subDir in (string[])["refs", "samples", "vmaf"])
        {
            string path = Path.Combine(outDir, subDir);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }

    private static string SafeId(string s)
    {
        System.Text.StringBuilder sb = new(s.Length);
        foreach (char ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        return sb.ToString();
    }

    /// <summary>
    /// Human-readable rendering of a post-grant pool snapshot for inclusion in
    /// per-file decisions.log lines.  Format: "CPU 16/20, NVENC 1/2, NVDEC 2/2,
    /// CUDA 1/2".  The corresponding structured properties are emitted by the
    /// pool itself into events.jsonl, so this string is purely for the human
    /// reading the per-file log.
    /// </summary>
    internal static string FormatPool(PoolSnapshot s, ResourcePool pool) =>
        $"CPU {s.Cpu}/{pool.CpuTotal}, " +
        $"NVENC {s.Nvenc}/{pool.NvencTotal}, " +
        $"NVDEC {s.Nvdec}/{pool.NvdecTotal}, " +
        $"CUDA {s.Cuda}/{pool.CudaTotal}";
}
