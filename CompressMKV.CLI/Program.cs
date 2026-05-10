// Program.cs
// Build: dotnet build
// Run:   dotnet run --project CompressMKV.CLI -- --input /path/to/mkvs --output /path/to/out [--force-redo]
//
// VMAF models come from libvmaf's compiled-in versions — vmaf_v0.6.1 for
// ≤1080p, vmaf_4k_v0.6.1 for ≥3840×2160.  Selected automatically by source
// resolution; no model files required on disk.
//
// Content type detection follows the five NTSC DVD categories from MPlayer guide §7.2:
//   http://www.mplayerhq.hu/DOCS/HTML/en/menc-feat-telecine.html
//
//   1. Progressive        — 24p stored as 24p; no restoration needed.
//   2. Telecined           — 24p hard-telecined to 30fps (3:2 pulldown baked in).
//                            IVTC via fieldmatch+decimate to recover 24p.
//   3. Interlaced          — Native 60i content. Deinterlace via bwdif.
//   4. Mixed prog+telecine — Some sections progressive, some telecined.
//                            fieldmatch+decimate handles both (progressive passes through).
//   5. Mixed prog+interlaced — Some sections progressive, some natively interlaced.
//                            Deinterlace all as a compromise.
//
// VMAF-guided tuning pipeline:
//   Phase 1: Extract lossless FFV1 reference clips with restored cadence (fast seek).
//   Phase 2: Encode each at candidate CQ levels (high→low, early termination).
//   VMAF measured per-frame across all samples for robust percentile computation.
//   Thresholds: mean ≥ 97, p05 ≥ 95, p01 ≥ 90 (virtually imperceptible at close viewing).
//
// Detection approach (single-pass per-frame):
//   One ffmpeg call decodes the entire file with idet + metadata=print,
//   streaming per-frame interlace classification to stdout.  Each frame's
//   FrameFlag (progressive/interlaced/undetermined) is stored in a compact
//   list (~200 KB for a typical 2-hour movie).  Content type is determined
//   from two whole-file metrics: global progressive fraction and telecine
//   cadence match rate (sliding 5-frame window for PPPII pattern).
//   No chunks, no sampling, no IVTC verification, no source-type bias.
//   Detection thresholds are internal constants derived from signal physics.
//   Hardware-accelerated decoding (NVDEC) is used when available.
//
// Logging:
//   - log.json  (per-file structured summary with all decisions)
//   - decisions.log (per-file plain-text chronological narrative)
//   - overall_<timestamp>.json (run-level aggregate)
//
// Resume:
//   - Files with valid log.json are skipped on restart (file-level resume).
//   - --force-redo deletes prior log.json + intermediate state and re-runs.

using System.Diagnostics;
using System.Globalization;
using Spectre.Console;

namespace CompressMkv;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        string? input = GetArg(args, "--input");
        string? output = GetArg(args, "--output");
        bool forceRedo = args.Contains("--force-redo");

        if (string.IsNullOrWhiteSpace(input))
        {
            AnsiConsole.MarkupLine("[red]Usage:[/] compress-mkv --input <folder> [[--output <folder>]] [[--force-redo]]");
            return 2;
        }

        var cfg = BuildConfig(input!, output);
        Directory.CreateDirectory(cfg.OutputFolder);

        // ---- Validate ffmpeg capabilities at startup ----
        AnsiConsole.MarkupLine("[grey]Validating ffmpeg capabilities...[/]");
        bool hasZscale = await FfmpegCapabilities.ValidateAsync(cfg, cts.Token);
        if (!hasZscale)
            AnsiConsole.MarkupLine("[yellow]Warning:[/] zscale (libzimg) not available — HDR content will fail.");
        else
            AnsiConsole.MarkupLine("[green]OK:[/] libvmaf and zscale available.");

        // Discover video files by content, not extension.  Probes every file
        // in the input folder (that's >10 KB and not hidden) with ffprobe and
        // keeps the ones that have real video content — i.e. at least one
        // non-cover-art video stream and >1 second of duration.  Handles any
        // container ffmpeg can read (.mkv/.mp4/.rm/.flv/.vob/.wmv/...) and
        // even files with no extension at all.
        var discovered = await VideoFileDiscovery.DiscoverAsync(cfg, cfg.InputFolder, cts.Token);

        AnsiConsole.MarkupLine($"[bold]Found {discovered.Count} video file(s)[/] under {cfg.InputFolder}");
        AnsiConsole.MarkupLine(
            $"[grey]Concurrency:[/] {cfg.MaxConcurrentFiles} files in flight, " +
            $"{cfg.MaxConcurrentCpuFfmpegOps} CPU ffmpeg ops, " +
            $"{cfg.NvencSlots} NVENC + {cfg.NvdecSlots} NVDEC, " +
            $"{cfg.FfmpegCpuThreads} threads/CPU op, " +
            $"{cfg.LibvmafThreads} threads/libvmaf.");
        if (forceRedo)
            AnsiConsole.MarkupLine("[yellow]--force-redo:[/] existing log.json files will be ignored and files re-processed.");
        AnsiConsole.WriteLine();

        var gpu = new GpuGate(cfg.NvencSlots, cfg.NvdecSlots);
        var cpu = new CpuGate(cfg.MaxConcurrentCpuFfmpegOps);
        var overall = new OverallSummary { StartedUtc = DateTime.UtcNow, Config = cfg };
        var reporter = new StageReporter(discovered.Count);

        // Live UI: re-renders the reporter every 200ms while the workload runs.
        // Spectre.Console handles non-TTY environments gracefully (plain output).
        var workTask = ProcessAllFilesAsync(cfg, gpu, cpu, discovered, hasZscale, forceRedo, overall, reporter, cts.Token);

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

        // End-of-run summary.
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
        AnsiConsole.MarkupLine($"  [grey]Wrote {overallPath}[/]");

        return overall.Errors.Count == 0 ? 0 : 1;
    }

    /// <summary>
    /// Per-file work loop.  Spawns at most <see cref="Config.MaxConcurrentFiles"/>
    /// concurrent file processings, each with its own <see cref="PerFileLogger"/>.
    /// </summary>
    private static async Task ProcessAllFilesAsync(
        Config cfg, GpuGate gpu, CpuGate cpu, List<DiscoveredVideo> files, bool hasZscale,
        bool forceRedo, OverallSummary overall, StageReporter reporter, CancellationToken ct)
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

                // Resume: if a prior run completed successfully, load its result and skip.
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
                    catch
                    {
                        // Corrupt log — treat as incomplete and reprocess.
                    }
                }

                // Incomplete prior run (or --force-redo): wipe intermediates + log.
                if (forceRedo && File.Exists(logPath)) File.Delete(logPath);
                CleanIntermediates(outDir);
                Directory.CreateDirectory(outDir);

                int slot = idx;  // file slot identifier for the reporter
                reporter.BeginFile(slot, idx, fileName);

                using var logger = new PerFileLogger(
                    Path.Combine(outDir, "decisions.log"), reporter, slot);

                try
                {
                    var summary = await ProcessOneAsync(cfg, gpu, cpu, file, discovered.Probe, hasZscale, fileCt, logger);
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
        // Build a compact one-line result for the recent-completions table.
        var parts = new List<string>();
        if (s.Tuning?.Selection?.SelectedCq is { } cq) parts.Add($"CQ={cq}");

        bool marginal = s.Tuning?.Selection?.IsMarginal == true;
        bool verifyFailed = s.OutputVerification?.Passed == false;

        if (verifyFailed)
            return ($"verify FAILED — {string.Join(' ', parts)}", ResultLevel.Failure);
        if (marginal)
            return ($"⚠ marginal — {string.Join(' ', parts)}", ResultLevel.Warning);
        return ($"✓ {string.Join(' ', parts)}", ResultLevel.Success);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static Config BuildConfig(string input, string? output) => new()
    {
        InputFolder = input,
        OutputFolder = output ?? "./out",
        Ffmpeg = "ffmpeg",
        Ffprobe = "ffprobe",

        // RTX 5080: 2 NVENC + 2 NVDEC sessions.
        NvencSlots = 2,
        NvdecSlots = 2,

        // VMAF tuning — stratified random sampling + per-frame score aggregation
        CandidateCq = [16, 18, 20, 22, 24, 26, 28, 30, 32, 34],
        SampleCount = 16,
        SampleWindowSeconds = 12,
        RandomSeed = 12345,

        // Frame-level VMAF thresholds for imperceptible quality loss.
        TargetMeanVmaf = 97.0,
        TargetP05Vmaf = 95.0,
        TargetP01Vmaf = 90.0,

        // HDR CQ ladder shift (effective CQ = base CQ - delta)
        HdrApplyCqLadderShift = true,
        HdrCqLadderDelta = 2,
        MinCq = 0,

        // NVENC AV1 — highest quality preset with multipass + AQ (set in Pipelines)
        NvencPreset = "p7",
        RcLookahead = 48,
        UseNvdecForEncode = true,

        // Content detection (single-pass per-frame idet)
        UseHwaccelForDetection = true,

        // Preview gating
        PreviewMaxConfidenceToGenerate = 0.60,
        PreviewCount = 3,
        PreviewDurationSeconds = 10.0,

        OutputExtension = ".mkv",
    };

    private static async Task<VideoSummary> ProcessOneAsync(
        Config cfg, GpuGate gpu, CpuGate cpu, string input, FfprobeRoot probe,
        bool hasZscale, CancellationToken ct, IPipelineLogger logger)
    {
        await Task.CompletedTask;  // makes async-state-machine cancellation deterministic
        var totalSw = Stopwatch.StartNew();
        var timings = new PhaseTimings();
        var id = SafeId(Path.GetFileNameWithoutExtension(input));
        var outDir = Path.Combine(cfg.OutputFolder, id);
        Directory.CreateDirectory(outDir);

        logger.LogInfo($"Input: {input}");
        logger.LogInfo($"Output dir: {outDir}");

        // Probe was already done during discovery; pick the real video stream.
        // Filter out cover-art streams in case the source contains both.
        var vstream = probe.Streams?.FirstOrDefault(s => s.IsActualVideo())
                     ?? throw new InvalidOperationException("No usable video stream found.");

        bool isHdr = SourceClassifier.IsHdr(vstream);
        logger.LogInfo($"HDR: {isHdr}");

        if (isHdr && !hasZscale)
            throw new InvalidOperationException(
                "HDR content detected but ffmpeg lacks zscale (libzimg). " +
                "VMAF measurement for HDR requires tone-mapping via zscale.");

        // HDR metadata — only meaningful for HDR sources.
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

        // ---- Detection ----
        var sw = Stopwatch.StartNew();
        ContentDetectionResult detection;
        using (await gpu.AcquireAsync(nvenc: 0, nvdec: cfg.UseHwaccelForDetection ? 1 : 0, ct))
        using (await cpu.AcquireAsync(ct))
        {
            detection = await ContentDetector.DetectAsync(cfg, input, vstream, ct, logger);
        }
        timings.Detection = sw.Elapsed;

        var restore = RestoreStrategyMapper.MapToRestore(detection);
        logger.LogInfo($"Restore decision: {restore.Mode} (filter=\"{restore.FilterGraph}\", fps={restore.OutputFps?.ToString() ?? "native"})");
        logger.LogInfo($"  Reason: {restore.Notes}");

        // ---- Previews ----
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

        // ---- Tuning ----
        var tuning = await VmafTuner.TuneAsync(cfg, gpu, cpu, input, outDir, restore,
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

        // ---- Final encode ----
        int finalCq = tuning.Selection.SelectedCq;
        var finalOut = Path.Combine(outDir, $"{id}_av1_cq{finalCq}{cfg.OutputExtension}");
        sw.Restart();
        using (await cpu.AcquireAsync(ct))
        {
            await FinalEncoder.EncodeAsync(cfg, gpu, input, finalOut, restore, finalCq, pipelineFormat, ct, logger);
        }
        timings.FinalEncode = sw.Elapsed;
        logger.LogInfo($"Final encode written: {finalOut}");

        // ---- Verification ----
        sw.Restart();
        OutputVerificationResult verification;
        using (await gpu.AcquireAsync(nvenc: 0, nvdec: cfg.UseHwaccelForDetection ? 1 : 0, ct))
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
            Timings = timings,
            HdrMetadata = hdrMetadata,
        };

        await JsonIO.WriteAsync(Path.Combine(outDir, "log.json"), summary, ct);
        CleanIntermediates(outDir);

        return summary;
    }

    private static string? GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null;
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
