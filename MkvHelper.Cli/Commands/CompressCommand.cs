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

        // Run-wide config snapshot for reproducibility.  Everything that
        // shapes a run's outcome that isn't otherwise in source control:
        // CQ search ranges (per resolution tier), sample budget + RNG seed,
        // VMAF thresholds and model versions, NVENC encoder settings.  One
        // structured event makes after-the-fact "what settings did this run
        // use" trivial without reading the Config.cs source.
        Serilog.Log.Logger.Information(
            "Run config: cqRangeUhd=[{MinUhd}..{MaxUhd}], cqRangeFhd=[{MinFhd}..{MaxFhd}], " +
            "cqRangeHd=[{MinHd}..{MaxHd}], cqRangeSd=[{MinSd}..{MaxSd}]; " +
            "sampleCount={SampleCount}, sampleWindowSec={SampleWindowSec}, rngSeed={Seed}; " +
            "vmafThresholds(mean/p05/p01)={MeanT}/{P05T}/{P01T}; " +
            "vmafModels(std/4k)={StdModel}/{Model4k}; " +
            "nvencPreset={Preset}, rcLookahead={Lookahead}; " +
            "samplerIntervalSec={SamplerInterval}",
            cfg.MinCqUhd, cfg.MaxCqUhd, cfg.MinCqFhd, cfg.MaxCqFhd,
            cfg.MinCqHd, cfg.MaxCqHd, cfg.MinCqSd, cfg.MaxCqSd,
            cfg.SampleCount, cfg.SampleWindowSeconds, cfg.RandomSeed,
            cfg.TargetMeanVmaf, cfg.TargetP05Vmaf, cfg.TargetP01Vmaf,
            cfg.VmafStandardModelVersion, cfg.Vmaf4kModelVersion,
            cfg.NvencPreset, cfg.RcLookahead,
            cfg.SystemSamplerIntervalSeconds);

        // Run-once context lines used to scroll past per file — pull them up
        // here since they don't change between files.
        Serilog.Log.Logger.Information(
            "Container image: {ImageTag}", ContainerTools.ImageTag);
        Serilog.Log.Logger.Information(
            "VMAF execution mode: GPU (libvmaf_cuda); AV1 sample decode on NVDEC; " +
            "HDR sources tonemap on CPU before hwupload_cuda.");

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
        RenderPoolShareByFile(overall.Videos);

        AnsiConsole.MarkupLine($"  [grey]Wrote {overallPath}[/]");

        return overall.Errors.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Cross-file rollup of pool ownership.  For each resource, ranks files
    /// by their share of total slot-ms consumed.  Surfaces "this one file
    /// held 38% of CUDA across the whole batch" — useful when one outlier
    /// is the bottleneck on a many-file run.  Top 5 files per resource are
    /// listed; remainder is rolled up as "others".
    /// </summary>
    private static void RenderPoolShareByFile(List<VideoSummary> videos)
    {
        ResourceTimeShare totals = new();
        foreach (VideoSummary v in videos) totals += v.ResourceShare;
        if (totals.CpuMs == 0 && totals.NvencMs == 0 && totals.NvdecMs == 0 && totals.CudaMs == 0)
            return;

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("  [bold]Pool share by file[/] [grey](top consumers per resource)[/]");

        RenderOne("CPU",   v => v.ResourceShare.CpuMs,   totals.CpuMs);
        RenderOne("NVENC", v => v.ResourceShare.NvencMs, totals.NvencMs);
        RenderOne("NVDEC", v => v.ResourceShare.NvdecMs, totals.NvdecMs);
        RenderOne("CUDA",  v => v.ResourceShare.CudaMs,  totals.CudaMs);

        void RenderOne(string label, Func<VideoSummary, long> select, long total)
        {
            if (total <= 0) return;
            List<(string Id, long Ms)> ranked = videos
                .Select(v => (v.VideoId, select(v)))
                .Where(t => t.Item2 > 0)
                .OrderByDescending(t => t.Item2)
                .ToList();
            if (ranked.Count == 0) return;
            AnsiConsole.MarkupLine($"    [grey]{label,-5}[/]");
            int shown = 0;
            long shownSum = 0;
            foreach ((string Id, long Ms) row in ranked.Take(5))
            {
                double pct = 100.0 * row.Ms / total;
                AnsiConsole.MarkupLine(
                    $"      {Markup.Escape(Truncate(row.Id, 50)),-50} " +
                    $"[cyan]{pct,5:F1}%[/] ({row.Ms / 1000.0:F1} slot-s)");
                shown++;
                shownSum += row.Ms;
            }
            int remaining = ranked.Count - shown;
            if (remaining > 0)
            {
                double restPct = 100.0 * (total - shownSum) / total;
                AnsiConsole.MarkupLine(
                    $"      {$"({remaining} other file(s))",-50} " +
                    $"[grey]{restPct,5:F1}%[/] ({(total - shownSum) / 1000.0:F1} slot-s)");
            }
        }
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

        // VRAM peak vs ceiling.  Config.cs sizes CudaSlots so the realistic
        // worst-case mix lands just under the card's total memory — show the
        // observed peak alongside the ceiling so a user can confirm the sizing
        // held at runtime (or notice they're at the edge and should back off).
        if (s.VramMbMax is { } vmax)
        {
            string ceilingPart = s.VramMbTotal is { } vtotal
                ? $" / {vtotal / 1024.0:F1} GiB total ({(vtotal - vmax) / 1024.0:F1} GiB headroom)"
                : "";
            AnsiConsole.MarkupLine(
                $"    [grey]VRAM  [/] peak [yellow]{vmax / 1024.0:F1} GiB[/]" + ceilingPart);
        }

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

        // VRAM-edge hint — if peak landed within 1 GiB of the card total,
        // the user is at the line and probably wants to back off slots
        // before adding any.  Cross-checks the slot-tuning hints above
        // against the actual memory the run consumed.
        if (s.VramMbMax is { } vmaxH && s.VramMbTotal is { } vtotalH && vtotalH - vmaxH < 1024)
            hints.Add(
                $"VRAM peak {vmaxH / 1024.0:F1} GiB is within 1 GiB of the {vtotalH / 1024.0:F1} GiB " +
                "card ceiling — back off CudaSlots or NvencSlots before raising any GPU pool further.");

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

        FfprobeStream vstream = probe.Streams?.FirstOrDefault(s => s.IsActualVideo())
                     ?? throw new InvalidOperationException("No usable video stream found.");

        bool isHdr = SourceClassifier.IsHdr(vstream);
        // HDR detection is keyed off color_transfer — include the trigger
        // so a reader can see exactly which colorimetry tag drove the call.
        logger.LogInfo($"HDR: {isHdr} (color_transfer={vstream.ColorTransfer ?? "—"})");

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

        Stopwatch detectionSw = Stopwatch.StartNew();
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
            if (detectAdmit.AlternativeIndex > 0)
                // NVDEC-first ordering; fallback to alt 1 = CPU means NVDEC
                // was saturated.  Worth a per-file note since it changes the
                // detection latency profile.
                logger.LogInfo(
                    "Detection fell back to CPU decode (NVDEC pool saturated at admission).");
            detection = await ContentDetector.DetectAsync(cfg, input, vstream, useHwaccel, ct, logger);
        }
        detectHold.Stop();
        int detectHoldMs = (int)detectHold.Elapsed.TotalMilliseconds;
        logger.RecordOp("detection", detectAdmit.Granted, detectAdmit.WaitMs, detectHoldMs);
        logger.LogInfo(
            $"Detection released: held resources for {detectHold.Elapsed.TotalSeconds:F1}s " +
            $"(wait was {detectAdmit.WaitMs}ms).");
        timings.Detection = new PhaseTiming(
            Wall: detectionSw.Elapsed,
            QueueSum: TimeSpan.FromMilliseconds(detectAdmit.WaitMs),
            RunSum: detectHold.Elapsed,
            OpCount: 1);

        RestoreDecision restore = RestoreStrategyMapper.MapToRestore(detection);
        logger.LogInfo(
            $"Restore decision: {restore.Mode} (confidence={restore.Confidence:P0}, " +
            $"filter=\"{restore.FilterGraph}\", fps={restore.OutputFps?.ToString() ?? "native"})");
        logger.LogInfo($"  Reason: {restore.Notes}");

        TuningResult tuning = await VmafTuner.TuneAsync(cfg, pool, input, outDir, restore,
            isHdr, hdrMetadata, pipelineFormat, vmafModelVersion, vstream, ct, logger);
        timings.TuningPhase1 = tuning.Phase1Timing;
        timings.TuningPhase2 = tuning.Phase2Timing;

        if (tuning.Selection.IsMarginal)
        {
            logger.LogWarning("Selection is MARGINAL — within "
                + $"{Selector.MarginalThresholdPoints:F1} VMAF points of threshold:");
            foreach (string r in tuning.Selection.MarginalReasons)
                logger.LogWarning($"  ! {r}");
        }

        int finalCq = tuning.Selection.SelectedCq;
        string finalOut = Path.Combine(outDir, $"{id}_av1_cq{finalCq}{cfg.OutputExtension}");
        Stopwatch finalSw = Stopwatch.StartNew();
        // Snapshot metrics before/after so we can split the final-encode op
        // into wall (whole call) and queue/run (the pool acquire).
        int finalOpsBefore = logger.Metrics?.Snapshot().Count ?? 0;
        await FinalEncoder.EncodeAsync(cfg, pool, input, finalOut, restore, finalCq, pipelineFormat, ct, logger);
        finalSw.Stop();
        IReadOnlyList<FileMetricsCollector.OpRecord> finalOps =
            logger.Metrics?.Snapshot().Skip(finalOpsBefore).ToArray()
            ?? (IReadOnlyList<FileMetricsCollector.OpRecord>)[];
        FileMetricsCollector.OpRecord? finalRec = finalOps.FirstOrDefault(o => o.Op == "final-encode");
        timings.FinalEncode = new PhaseTiming(
            Wall: finalSw.Elapsed,
            QueueSum: TimeSpan.FromMilliseconds(finalRec?.WaitMs ?? 0),
            RunSum: TimeSpan.FromMilliseconds(finalRec?.HoldMs ?? 0),
            OpCount: 1);
        logger.LogInfo($"Final encode written: {finalOut}");

        // SizeGuard does the (instant) size check inline; it acquires a small
        // CPU slice from the pool only if the remux fallback actually fires.
        SizeGuardOutcome sizeGuard = await SizeGuard.MaybeFallbackAsync(cfg, pool, input, finalOut, restore, logger, ct);
        finalOut = sizeGuard.OutputPath;

        Stopwatch verifySw = Stopwatch.StartNew();
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
            if (verifyAdmit.AlternativeIndex > 0)
                logger.LogInfo(
                    "Verification fell back to CPU decode (NVDEC pool saturated at admission).");
            verification = await OutputVerifier.VerifyAsync(cfg, finalOut, restore, useHwaccel, ct, logger);
        }
        verifyHold.Stop();
        int verifyHoldMs = (int)verifyHold.Elapsed.TotalMilliseconds;
        logger.RecordOp("verification", verifyAdmit.Granted, verifyAdmit.WaitMs, verifyHoldMs);
        logger.LogInfo(
            $"Verification released: held resources for {verifyHold.Elapsed.TotalSeconds:F1}s " +
            $"(wait was {verifyAdmit.WaitMs}ms).");
        timings.Verification = new PhaseTiming(
            Wall: verifySw.Elapsed,
            QueueSum: TimeSpan.FromMilliseconds(verifyAdmit.WaitMs),
            RunSum: verifyHold.Elapsed,
            OpCount: 1);
        timings.Total = totalSw.Elapsed;

        // Per-file time card — replaces the one-line "Timings…" summary
        // with a phase-by-phase breakdown that names queue vs run and gives
        // each phase's share of the total.  When run time is dominated by
        // queue waits, the queue column makes the contention pattern obvious
        // without grep over events.jsonl.
        RenderTimeCard(logger, timings);

        // Top-3 longest waits across every pool acquire this file made.  A
        // big wait buried in 80+ acquires is hard to find; surfacing the
        // worst three answers "which op held this file up the longest."
        RenderTopWaits(logger);

        ResourceTimeShare share = logger.Metrics?.ResourceShare()
            ?? new ResourceTimeShare();

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
            ResourceShare = share,
        };

        await JsonIO.WriteAsync(Path.Combine(outDir, "log.json"), summary, ct);
        CleanIntermediates(outDir);

        return summary;
    }

    /// <summary>
    /// Per-file end-of-pipeline timing table.  One row per phase showing
    /// wall-clock, percent of total, queue (sum of pool waits in that phase),
    /// and run (sum of pool holds, may exceed wall on parallel phases).
    /// </summary>
    private static void RenderTimeCard(IPipelineLogger logger, PhaseTimings t)
    {
        double total = t.Total.TotalSeconds;
        logger.LogInfo($"Time card (total {FormatHms(t.Total)}):");
        Emit("Detection",   t.Detection,   total, logger);
        Emit("Phase 1",     t.TuningPhase1, total, logger);
        Emit("Phase 2",     t.TuningPhase2, total, logger);
        Emit("Final encode", t.FinalEncode, total, logger);
        Emit("Verify",      t.Verification, total, logger);

        static void Emit(string name, PhaseTiming p, double totalSec, IPipelineLogger l)
        {
            double pct = totalSec > 0 ? p.Wall.TotalSeconds / totalSec * 100 : 0;
            l.LogInfo(
                $"  {name,-12}  wall {FormatHms(p.Wall),8}  {pct,5:F1}%  " +
                $"queue-sum {FormatHms(p.QueueSum),8}  run-sum {FormatHms(p.RunSum),8}  " +
                $"({p.OpCount} op{(p.OpCount == 1 ? "" : "s")})");
        }
    }

    /// <summary>
    /// Surface the top three longest pool waits this file experienced — the
    /// "which op was the worst offender" view.  Skipped when no acquires
    /// recorded a wait greater than 0 (every op ran fast-path).
    /// </summary>
    private static void RenderTopWaits(IPipelineLogger logger)
    {
        IReadOnlyList<FileMetricsCollector.OpRecord> top =
            logger.Metrics?.TopWaits(3)
            ?? (IReadOnlyList<FileMetricsCollector.OpRecord>)[];
        if (top.Count == 0 || top.All(r => r.WaitMs == 0)) return;

        logger.LogInfo("Top pool waits this file:");
        foreach (FileMetricsCollector.OpRecord r in top.Where(r => r.WaitMs > 0))
            logger.LogInfo(
                $"  {r.Op,-32}  waited {r.WaitMs,7}ms  held {r.HoldMs,7}ms  " +
                $"granted {{cpu={r.Granted.Cpu} nvenc={r.Granted.Nvenc} " +
                $"nvdec={r.Granted.Nvdec} cuda={r.Granted.Cuda}}}");
    }

    private static string FormatHms(TimeSpan t)
    {
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        if (t.TotalMinutes >= 1)
            return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
        return $"{t.TotalSeconds:F1}s";
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
