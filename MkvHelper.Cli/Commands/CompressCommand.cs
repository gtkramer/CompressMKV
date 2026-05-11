using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (string.IsNullOrWhiteSpace(settings.Input))
        {
            AnsiConsole.MarkupLine("[red]--input is required.[/]  See `mkvhelper compress --help`.");
            return 2;
        }

        var cfg = BuildConfig(settings.Input!, settings.Output);

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

        var discovered = await VideoFileDiscovery.DiscoverAsync(cfg, cfg.InputFolder, cts.Token);

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

        var pool = new ResourcePool(cfg.CpuPool, cfg.NvencSlots, cfg.NvdecSlots, cfg.CudaSlots);
        var overall = new OverallSummary { StartedUtc = DateTime.UtcNow, Config = cfg };
        var reporter = new StageReporter(discovered.Count);

        var workTask = ProcessAllFilesAsync(
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

        overall.FinishedUtc = DateTime.UtcNow;
        var overallPath = Path.Combine(cfg.OutputFolder, $"overall_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        await JsonIO.WriteAsync(overallPath, overall, cts.Token);

        AnsiConsole.WriteLine();
        var rule = new Rule("[bold]Run summary[/]") { Justification = Justify.Left };
        AnsiConsole.Write(rule);
        AnsiConsole.MarkupLine($"  [green]Completed:[/] {overall.Videos.Count - overall.Errors.Count} files");
        AnsiConsole.MarkupLine($"  [yellow]Skipped via resume:[/] {overall.Videos.Count(v => v.Tuning == null && v.OutputVerification == null)}");
        if (overall.Errors.Count > 0)
            AnsiConsole.MarkupLine($"  [red]Errored:[/] {overall.Errors.Count}");
        var marginalCount = overall.Videos.Count(v => v.Tuning?.Selection?.IsMarginal == true);
        if (marginalCount > 0)
            AnsiConsole.MarkupLine($"  [yellow]Marginal passes:[/] {marginalCount}");
        var passthroughCount = overall.Videos.Count(v => v.SizeGuard?.FellBack == true);
        if (passthroughCount > 0)
            AnsiConsole.MarkupLine($"  [yellow]Passthrough fallback:[/] {passthroughCount} (source already efficiently compressed)");
        AnsiConsole.MarkupLine($"  [grey]Wrote {overallPath}[/]");

        return overall.Errors.Count == 0 ? 0 : 1;
    }

    // ----------------------------------------------------------------
    //  Per-file work loop
    // ----------------------------------------------------------------

    private static async Task ProcessAllFilesAsync(
        Config cfg, ResourcePool pool, List<DiscoveredVideo> files,
        bool forceRedo, OverallSummary overall, StageReporter reporter, CancellationToken ct)
    {
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
                var (discovered, idx) = item;
                var file = discovered.Path;
                var id = SafeId(Path.GetFileNameWithoutExtension(file));
                var outDir = Path.Combine(cfg.OutputFolder, id);
                var logPath = Path.Combine(outDir, "log.json");
                var fileName = Path.GetFileName(file);

                if (!forceRedo && File.Exists(logPath))
                {
                    try
                    {
                        var prior = await JsonIO.ReadAsync<VideoSummary>(logPath, fileCt);
                        if (prior != null)
                        {
                            lock (overall) overall.Videos.Add(prior);
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

                using var logger = new PerFileLogger(
                    Path.Combine(outDir, "decisions.log"), reporter, slot);

                try
                {
                    var summary = await ProcessOneAsync(cfg, pool, file, discovered.Probe, fileCt, logger);
                    lock (overall) overall.Videos.Add(summary);
                    var (resultText, level) = SummariseResult(summary);
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
                    lock (overall) overall.Errors.Add(new RunError { File = file, Error = ex.ToString() });
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
        NvencSlots = 2,
        NvdecSlots = 2,
        CudaSlots = 2,

        // CQ binary search range — full NVENC AV1 range is 0..63; we
        // narrow to the practically-useful window (see Config docs).
        MinCq = 8,
        MaxCq = 55,

        SampleCount = 16,
        SampleWindowSeconds = 12,
        RandomSeed = 12345,

        TargetMeanVmaf = 97.0,
        TargetP05Vmaf = 95.0,
        TargetP01Vmaf = 90.0,

        NvencPreset = "p7",
        RcLookahead = 48,

        PreviewMaxConfidenceToGenerate = 0.60,
        PreviewCount = 3,
        PreviewDurationSeconds = 10.0,

        OutputExtension = ".mkv",
    };

    private static async Task<VideoSummary> ProcessOneAsync(
        Config cfg, ResourcePool pool, string input, FfprobeRoot probe,
        CancellationToken ct, IPipelineLogger logger)
    {
        await Task.CompletedTask;
        var totalSw = Stopwatch.StartNew();
        var timings = new PhaseTimings();
        var id = SafeId(Path.GetFileNameWithoutExtension(input));
        var outDir = Path.Combine(cfg.OutputFolder, id);
        Directory.CreateDirectory(outDir);

        logger.LogInfo($"Input: {input}");
        logger.LogInfo($"Output dir: {outDir}");
        logger.LogInfo($"Container image: {ContainerTools.ImageTag}");

        var vstream = probe.Streams?.FirstOrDefault(s => s.IsActualVideo())
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

        var pipelineFormat = PipelineFormat.FromStream(vstream);
        logger.LogInfo($"Pipeline format: {pipelineFormat}");

        string vmafModelVersion = cfg.ResolveVmafModelVersion(vstream.Width, vstream.Height);
        logger.LogInfo($"VMAF model: {vmafModelVersion} (source {vstream.Width}x{vstream.Height})");

        var sw = Stopwatch.StartNew();
        ContentDetectionResult detection;
        using (await pool.AcquireAsync(cfg.DetectionRequest, ct))
        {
            detection = await ContentDetector.DetectAsync(cfg, input, vstream, ct, logger);
        }
        timings.Detection = sw.Elapsed;

        var restore = RestoreStrategyMapper.MapToRestore(detection);
        logger.LogInfo($"Restore decision: {restore.Mode} (filter=\"{restore.FilterGraph}\", fps={restore.OutputFps?.ToString() ?? "native"})");
        logger.LogInfo($"  Reason: {restore.Notes}");

        bool needPreviews =
            restore.Confidence < cfg.PreviewMaxConfidenceToGenerate ||
            detection.ParityMismatch;

        if (needPreviews)
        {
            sw.Restart();
            logger.SetStage("Previews", $"generating {cfg.PreviewCount}×IVTC + {cfg.PreviewCount}×Deint");
            logger.LogInfo($"Generating {cfg.PreviewCount} preview pairs (low confidence or parity mismatch).");
            var previewDir = Path.Combine(outDir, "previews");
            Directory.CreateDirectory(previewDir);
            restore.Previews = new List<PreviewArtifact>();

            var dur = probe.Format?.DurationSeconds ?? 0;
            var previewTs = new List<double>();
            if (dur > 0)
            {
                double step = dur / (cfg.PreviewCount + 1);
                for (int pi = 1; pi <= cfg.PreviewCount; pi++) previewTs.Add(pi * step);
            }

            // IVTC and Deint previews for the same timestamp are independent —
            // fire both concurrently and let the pool gate admission.
            async Task RunPreview(string outPath, RestoreMode mode, double t)
            {
                using (await pool.AcquireAsync(cfg.PreviewRequest, ct))
                    await PreviewGenerator.MakeLosslessPreviewAsync(
                        cfg, input, outPath, mode, detection.DetectedParity, t, ct);
            }

            foreach (var t in previewTs)
            {
                ct.ThrowIfCancellationRequested();
                string baseTag = $"t{t.ToString("F1", CultureInfo.InvariantCulture)}";
                string ivtcPrev = Path.Combine(previewDir, $"preview_ivtc_{baseTag}.mkv");
                string deintPrev = Path.Combine(previewDir, $"preview_deint_{baseTag}.mkv");

                await Task.WhenAll(
                    RunPreview(ivtcPrev, RestoreMode.Ivtc, t),
                    RunPreview(deintPrev, RestoreMode.Deinterlace, t));

                restore.Previews.Add(new PreviewArtifact { TimestampSeconds = t, IvtcPath = ivtcPrev, DeintPath = deintPrev });
            }
            timings.Previews = sw.Elapsed;
        }

        logger.LogInfo(isHdr
            ? "VMAF execution: GPU (libvmaf_cuda); HDR tonemap chain on CPU"
            : "VMAF execution: GPU (libvmaf_cuda)");

        var tuning = await VmafTuner.TuneAsync(cfg, pool, input, outDir, restore,
            isHdr, hdrMetadata, pipelineFormat, vmafModelVersion, ct, logger);
        timings.TuningPhase1 = tuning.Phase1Elapsed;
        timings.TuningPhase2 = tuning.Phase2Elapsed;

        if (tuning.Selection.IsMarginal)
        {
            logger.LogWarning("Selection is MARGINAL — within "
                + $"{Selector.MarginalThresholdPoints:F1} VMAF points of threshold:");
            foreach (var r in tuning.Selection.MarginalReasons)
                logger.LogWarning($"  ! {r}");
        }

        int finalCq = tuning.Selection.SelectedCq;
        var finalOut = Path.Combine(outDir, $"{id}_av1_cq{finalCq}{cfg.OutputExtension}");
        sw.Restart();
        await FinalEncoder.EncodeAsync(cfg, pool, input, finalOut, restore, finalCq, pipelineFormat, ct, logger);
        timings.FinalEncode = sw.Elapsed;
        logger.LogInfo($"Final encode written: {finalOut}");

        // SizeGuard does the (instant) size check inline; it acquires a small
        // CPU slice from the pool only if the remux fallback actually fires.
        var sizeGuard = await SizeGuard.MaybeFallbackAsync(cfg, pool, input, finalOut, restore, logger, ct);
        finalOut = sizeGuard.OutputPath;

        sw.Restart();
        OutputVerificationResult verification;
        using (await pool.AcquireAsync(cfg.VerificationRequest, ct))
        {
            verification = await OutputVerifier.VerifyAsync(cfg, finalOut, restore, ct, logger);
        }
        timings.Verification = sw.Elapsed;
        timings.Total = totalSw.Elapsed;

        logger.LogInfo(
            $"Timings (total {timings.Total.TotalSeconds:F1}s): " +
            $"detect={timings.Detection.TotalSeconds:F1}s, " +
            $"prev={timings.Previews.TotalSeconds:F1}s, " +
            $"tune.p1={timings.TuningPhase1.TotalSeconds:F1}s, " +
            $"tune.p2={timings.TuningPhase2.TotalSeconds:F1}s, " +
            $"final={timings.FinalEncode.TotalSeconds:F1}s, " +
            $"verify={timings.Verification.TotalSeconds:F1}s.");

        var summary = new VideoSummary
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
        foreach (var subDir in new[] { "refs", "samples", "vmaf", "previews" })
        {
            var path = Path.Combine(outDir, subDir);
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
    }

    private static string SafeId(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        return sb.ToString();
    }
}
