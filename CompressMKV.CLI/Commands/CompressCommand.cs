using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CompressMkv;

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

    [CommandOption("--no-container")]
    [Description("Use system ffmpeg/ffprobe instead of the containerised CUDA build (fallback only — VMAF will run on CPU).")]
    public bool NoContainer { get; init; }
}

/// <summary>
/// Main entry point for `compressmkv compress`.  Bootstraps the runtime
/// (container vs native ffmpeg, capability probe, file discovery), then
/// hands off to <see cref="ProcessAllFilesAsync"/> for the parallel work.
/// </summary>
public sealed class CompressCommand : AsyncCommand<CompressSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, CompressSettings settings, CancellationToken token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        if (string.IsNullOrWhiteSpace(settings.Input))
        {
            AnsiConsole.MarkupLine("[red]--input is required.[/]  See `compressmkv compress --help`.");
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

        // ---- Configure ffmpeg routing (container vs native) ----
        var routingOk = await ConfigureFfmpegRoutingAsync(cfg, settings.NoContainer, cts.Token);
        if (!routingOk) return 1;

        // ---- Validate ffmpeg capabilities ----
        AnsiConsole.MarkupLine("[grey]Validating ffmpeg capabilities...[/]");
        var caps = await FfmpegCapabilities.ValidateAsync(cts.Token);
        AnsiConsole.MarkupLine(
            $"[green]OK:[/] libvmaf={caps.HasLibvmaf}, libvmaf_cuda={caps.HasLibvmafCuda}, zscale={caps.HasZscale}");
        if (FfmpegRunner.UsingContainer && !caps.HasLibvmafCuda)
            AnsiConsole.MarkupLine(
                "[yellow]Warning:[/] container is built but libvmaf_cuda was not detected — VMAF will run on CPU.  " +
                "This is unexpected — try `compressmkv dependency build` to rebuild.");

        // ---- Discover input files ----
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
            $"[grey]Concurrency:[/] {cfg.MaxConcurrentFiles} files in flight, " +
            $"{cfg.MaxConcurrentCpuFfmpegOps} CPU ffmpeg ops, " +
            $"{cfg.NvencSlots} NVENC + {cfg.NvdecSlots} NVDEC + {cfg.CudaVmafSlots} CUDA-VMAF, " +
            $"{cfg.FfmpegCpuThreads} threads/CPU op, " +
            $"{cfg.LibvmafThreads} threads/libvmaf.");
        if (settings.ForceRedo)
            AnsiConsole.MarkupLine("[yellow]--force-redo:[/] existing log.json files will be ignored and files re-processed.");
        AnsiConsole.WriteLine();

        var gpu = new GpuGate(cfg.NvencSlots, cfg.NvdecSlots, cfg.CudaVmafSlots);
        var cpu = new CpuGate(cfg.MaxConcurrentCpuFfmpegOps);
        var overall = new OverallSummary { StartedUtc = DateTime.UtcNow, Config = cfg };
        var reporter = new StageReporter(discovered.Count);

        var workTask = ProcessAllFilesAsync(
            cfg, gpu, cpu, discovered, caps, settings.ForceRedo,
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

    /// <summary>
    /// Decides whether to run ffmpeg/ffprobe via the bundled container or via
    /// the system binary, building the container on first use if needed.
    /// </summary>
    private static async Task<bool> ConfigureFfmpegRoutingAsync(Config cfg, bool noContainer, CancellationToken ct)
    {
        if (noContainer)
        {
            FfmpegRunner.ConfigureNative(cfg.Ffmpeg, cfg.Ffprobe);
            AnsiConsole.MarkupLine("[grey]--no-container: using system ffmpeg/ffprobe (VMAF will run on CPU).[/]");
            return true;
        }

        var state = await BuildState.LoadAsync(ct);

        // If we have a state file, verify the image is still in podman storage.
        // If podman wiped it, we need to rebuild.
        if (state is not null && await Podman.IsAvailableAsync(ct))
        {
            if (!await Podman.ImageExistsAsync(state.ImageTag, ct))
            {
                AnsiConsole.MarkupLine(
                    $"[yellow]Warning:[/] state references image [bold]{Markup.Escape(state.ImageTag)}[/] " +
                    "but it's gone from podman storage.  Rebuilding...");
                state = null;
            }
        }

        if (state is null)
        {
            // First run (or rebuilt podman storage): explain the build, then do it.
            if (!await Podman.IsAvailableAsync(ct))
            {
                AnsiConsole.MarkupLine(
                    "[yellow]podman is not available[/] — falling back to system ffmpeg/ffprobe.  " +
                    "Install `podman` + the NVIDIA Container Toolkit to use the GPU-accelerated VMAF pipeline " +
                    "(see `compressmkv dependency build --help`).");
                FfmpegRunner.ConfigureNative(cfg.Ffmpeg, cfg.Ffprobe);
                return true;
            }

            AnsiConsole.MarkupLine("[bold]First-run dependency build[/]");
            AnsiConsole.MarkupLine(
                "[grey]No CUDA-enabled ffmpeg+VMAF build was found.  compressmkv runs VMAF " +
                "on the GPU (libvmaf_cuda) for a large speedup over the CPU path; the build " +
                "comes from the Netflix/vmaf Dockerfile and is bundled in a podman image.  " +
                "First-time build typically takes 5–15 minutes; subsequent runs reuse the image.[/]");

            try
            {
                state = await BuildWithProgressAsync(ct);
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]Build failed:[/] {Markup.Escape(ex.Message)}");
                AnsiConsole.MarkupLine(
                    "[yellow]Falling back to system ffmpeg/ffprobe.[/]  " +
                    "Re-run `compressmkv dependency build` to retry, or pass `--no-container` to silence this prompt.");
                FfmpegRunner.ConfigureNative(cfg.Ffmpeg, cfg.Ffprobe);
                return true;
            }
        }

        // Container available: route through it.
        FfmpegRunner.ConfigureContainer(state.ImageTag, new[] { cfg.InputFolder, cfg.OutputFolder });
        AnsiConsole.MarkupLine(
            $"[grey]Using container[/] [bold]{Markup.Escape(state.ImageTag)}[/] " +
            $"[grey](Netflix/vmaf {Markup.Escape(state.UpstreamTag)}, built {state.BuiltUtc:yyyy-MM-dd}).[/]");
        return true;
    }

    private static async Task<BuildState> BuildWithProgressAsync(CancellationToken ct)
    {
        BuildState? result = null;
        await AnsiConsole.Status()
            .AutoRefresh(true)
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Preparing build...", async ctx =>
            {
                result = await VmafBuilder.BuildAsync(
                    tag: null,
                    onProgress: line =>
                    {
                        // Truncate so the spinner line stays readable.
                        string trimmed = line.Length > 100 ? line[..97] + "..." : line;
                        ctx.Status(Markup.Escape(trimmed));
                    },
                    ct: ct);
            });
        return result!;
    }

    // ----------------------------------------------------------------
    //  Per-file work loop (extracted from old Program.cs)
    // ----------------------------------------------------------------

    private static async Task ProcessAllFilesAsync(
        Config cfg, GpuGate gpu, CpuGate cpu, List<DiscoveredVideo> files,
        FfmpegCapabilitiesResult caps, bool forceRedo,
        OverallSummary overall, StageReporter reporter, CancellationToken ct)
    {
        await Parallel.ForEachAsync(
            files.Select((dv, i) => (dv, idx: i + 1)),
            new ParallelOptions
            {
                CancellationToken = ct,
                MaxDegreeOfParallelism = cfg.MaxConcurrentFiles,
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
                    var summary = await ProcessOneAsync(cfg, gpu, cpu, file, discovered.Probe, caps, fileCt, logger);
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
        Ffmpeg = "ffmpeg",
        Ffprobe = "ffprobe",

        NvencSlots = 2,
        NvdecSlots = 2,
        CudaVmafSlots = 2,

        CandidateCq = [16, 18, 20, 22, 24, 26, 28, 30, 32, 34],
        SampleCount = 16,
        SampleWindowSeconds = 12,
        RandomSeed = 12345,

        TargetMeanVmaf = 97.0,
        TargetP05Vmaf = 95.0,
        TargetP01Vmaf = 90.0,

        HdrApplyCqLadderShift = true,
        HdrCqLadderDelta = 2,
        MinCq = 0,

        NvencPreset = "p7",
        RcLookahead = 48,
        UseNvdecForEncode = true,

        UseHwaccelForDetection = true,

        PreviewMaxConfidenceToGenerate = 0.60,
        PreviewCount = 3,
        PreviewDurationSeconds = 10.0,

        OutputExtension = ".mkv",
    };

    private static async Task<VideoSummary> ProcessOneAsync(
        Config cfg, GpuGate gpu, CpuGate cpu, string input, FfprobeRoot probe,
        FfmpegCapabilitiesResult caps, CancellationToken ct, IPipelineLogger logger)
    {
        await Task.CompletedTask;
        var totalSw = Stopwatch.StartNew();
        var timings = new PhaseTimings();
        var id = SafeId(Path.GetFileNameWithoutExtension(input));
        var outDir = Path.Combine(cfg.OutputFolder, id);
        Directory.CreateDirectory(outDir);

        logger.LogInfo($"Input: {input}");
        logger.LogInfo($"Output dir: {outDir}");
        logger.LogInfo($"ffmpeg routing: {(FfmpegRunner.UsingContainer ? $"container ({FfmpegRunner.ImageTag})" : "native (system binary)")}");

        var vstream = probe.Streams?.FirstOrDefault(s => s.IsActualVideo())
                     ?? throw new InvalidOperationException("No usable video stream found.");

        bool isHdr = SourceClassifier.IsHdr(vstream);
        logger.LogInfo($"HDR: {isHdr}");

        if (isHdr && !caps.HasZscale)
            throw new InvalidOperationException(
                "HDR content detected but ffmpeg lacks zscale (libzimg).  " +
                "VMAF measurement for HDR requires tone-mapping via zscale.");

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
        using (await gpu.AcquireAsync(nvenc: 0, nvdec: cfg.UseHwaccelForDetection ? 1 : 0, cuda: 0, ct))
        using (await cpu.AcquireAsync(ct))
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

            foreach (var t in previewTs)
            {
                ct.ThrowIfCancellationRequested();
                string baseTag = $"t{t.ToString("F1", CultureInfo.InvariantCulture)}";
                string ivtcPrev = Path.Combine(previewDir, $"preview_ivtc_{baseTag}.mkv");
                string deintPrev = Path.Combine(previewDir, $"preview_deint_{baseTag}.mkv");

                using (await cpu.AcquireAsync(ct))
                    await PreviewGenerator.MakeLosslessPreviewAsync(cfg, input, ivtcPrev, RestoreMode.Ivtc, detection.DetectedParity, t, ct);
                using (await cpu.AcquireAsync(ct))
                    await PreviewGenerator.MakeLosslessPreviewAsync(cfg, input, deintPrev, RestoreMode.Deinterlace, detection.DetectedParity, t, ct);

                restore.Previews.Add(new PreviewArtifact { TimestampSeconds = t, IvtcPath = ivtcPrev, DeintPath = deintPrev });
            }
            timings.Previews = sw.Elapsed;
        }

        // Resolve per-file VMAF execution: GPU when libvmaf_cuda is available
        // AND (source is SDR OR the HDR-on-GPU toggle is enabled).  Logged so
        // it shows up in decisions.log alongside the other per-file decisions.
        bool useCudaForThisFile = caps.HasLibvmafCuda && (!isHdr || cfg.UseCudaVmafForHdr);
        logger.LogInfo(
            $"VMAF execution: {(useCudaForThisFile ? "GPU (libvmaf_cuda)" : "CPU (libvmaf)")} " +
            $"[caps.libvmaf_cuda={caps.HasLibvmafCuda}, isHdr={isHdr}, " +
            $"UseCudaVmafForHdr={cfg.UseCudaVmafForHdr}]");

        var tuning = await VmafTuner.TuneAsync(cfg, gpu, cpu, input, outDir, restore,
            isHdr, hdrMetadata, pipelineFormat, vmafModelVersion, useCudaForThisFile, ct, logger);
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
        using (await cpu.AcquireAsync(ct))
        {
            await FinalEncoder.EncodeAsync(cfg, gpu, input, finalOut, restore, finalCq, pipelineFormat, ct, logger);
        }
        timings.FinalEncode = sw.Elapsed;
        logger.LogInfo($"Final encode written: {finalOut}");

        SizeGuardOutcome sizeGuard;
        using (await cpu.AcquireAsync(ct))
        {
            sizeGuard = await SizeGuard.MaybeFallbackAsync(cfg, input, finalOut, restore, logger, ct);
        }
        finalOut = sizeGuard.OutputPath;

        sw.Restart();
        OutputVerificationResult verification;
        using (await gpu.AcquireAsync(nvenc: 0, nvdec: cfg.UseHwaccelForDetection ? 1 : 0, cuda: 0, ct))
        using (await cpu.AcquireAsync(ct))
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
