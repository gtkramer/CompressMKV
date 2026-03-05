// Program.cs
// Build: dotnet build
// Run:   dotnet run -- --input /path/to/mkvs --output /path/to/out [--vmaf-model-dir /usr/share/model]
//
// VMAF models:
//   On Arch Linux: sudo pacman -S vmaf
//   Models installed to /usr/share/model/ (vmaf_v0.6.1.json, vmaf_4k_v0.6.1.json).
//   Standard model used for ≤1080p; 4K model for ≥3840×2160 — selected automatically.
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
//   Thresholds: mean ≥ 97, p05 ≥ 95 (virtually imperceptible at close viewing).
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

using System.Globalization;
using System.Text;

namespace CompressMkv;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        string? input = GetArg(args, "--input");
        string? output = GetArg(args, "--output");
        if (string.IsNullOrWhiteSpace(input))
        {
            Console.WriteLine("Usage: compress-mkv --input <folder> [--output <folder>] [--vmaf-model-dir <path>]");
            return 2;
        }

        var cfg = new Config
        {
            InputFolder = input!,
            OutputFolder = output ?? "./out",
            Ffmpeg = "ffmpeg",
            Ffprobe = "ffprobe",

            // VMAF model directory — auto-selects standard vs 4K based on source resolution.
            // On Arch Linux: sudo pacman -S vmaf → models at /usr/share/model/
            VmafModelDir = GetArg(args, "--vmaf-model-dir") ?? "/usr/share/model",

            // RTX 5080 scheduling model
            NvencSlots = 2,
            NvdecSlots = 2,

            // VMAF tuning — stratified random sampling + per-frame score aggregation
            CandidateCq = [16, 18, 20, 22, 24, 26],
            SampleCount = 16,
            SampleWindowSeconds = 12,
            RandomSeed = 12345,

            // Frame-level VMAF thresholds for imperceptible quality loss.
            // Calibrated for 27" 16:9 at 2-3 ft and 65" 16:9 at 8-12 ft.
            TargetMeanVmaf = 97.0,
            TargetP05Vmaf = 95.0,

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

        Directory.CreateDirectory(cfg.OutputFolder);

        // ---- Validate ffmpeg capabilities at startup ----
        Console.WriteLine("Validating ffmpeg capabilities...");
        bool hasZscale = await FfmpegCapabilities.ValidateAsync(cfg, cts.Token);
        if (!hasZscale)
            Console.WriteLine("  Warning: zscale (libzimg) not available — HDR content will fail.");
        else
            Console.WriteLine("  OK: libvmaf and zscale available.");

        var files = Directory.EnumerateFiles(cfg.InputFolder, "*.mkv", SearchOption.AllDirectories)
                             .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                             .ToList();

        Console.WriteLine($"Found {files.Count} mkv files under {cfg.InputFolder}");
        var gpu = new GpuGate(cfg.NvencSlots, cfg.NvdecSlots);
        var overall = new OverallSummary { StartedUtc = DateTime.UtcNow, Config = cfg };

        int idx = 0;
        foreach (var file in files)
        {
            idx++;
            if (cts.IsCancellationRequested) break;

            Console.WriteLine($"\n[{idx}/{files.Count}] {file}");
            try
            {
                var summary = await ProcessOneAsync(cfg, gpu, file, hasZscale, cts.Token);
                overall.Videos.Add(summary);

                Console.WriteLine($"  HDR: {summary.IsHdr}");

                if (summary.ContentDetection != null)
                {
                    var det = summary.ContentDetection;
                    Console.WriteLine($"  ContentType: {det.ContentType} (confidence={det.Confidence:P0})");
                    Console.WriteLine($"    Frames: {det.TotalFramesAnalyzed:N0} (P={det.ProgressiveFrameCount:N0}, I={det.InterlacedFrameCount:N0}, U={det.UndeterminedFrameCount:N0})");
                    Console.WriteLine($"    Progressive fraction: {det.GlobalProgressiveFraction:P2}");
                    Console.WriteLine($"    Telecine cadence match rate: {det.TelecineCadenceMatchRate:P2}");
                    Console.WriteLine($"    Parity: {det.DetectedParity} (raw TFF={det.RawIdetTffCount:N0}, BFF={det.RawIdetBffCount:N0})");
                    if (det.ParityMismatch)
                        Console.WriteLine($"    PARITY MISMATCH: idet={det.DetectedParity} vs ffprobe={det.FfprobeMappedParity} (ffprobe field_order={det.FfprobeFieldOrder})");
                    Console.WriteLine($"    Reason: {det.Reason}");
                }

                if (summary.Restore != null)
                {
                    Console.WriteLine($"  Restore: {summary.Restore.Mode} → filter=\"{summary.Restore.FilterGraph}\", fps={summary.Restore.OutputFps ?? "native"}");
                    Console.WriteLine(summary.Restore.Previews is { Count: > 0 }
                        ? $"  Previews: generated ({summary.Restore.Previews.Count})"
                        : "  Previews: not generated");
                }

                if (summary.Tuning?.Selection != null)
                {
                    var sel = summary.Tuning.Selection;
                    Console.WriteLine($"  CQ selected (final): {summary.FinalCq}");
                    if (summary.Tuning.HdrCqShiftApplied)
                        Console.WriteLine($"    HDR CQ ladder shift applied: -{summary.Tuning.HdrCqShiftDelta}  Base=[{string.Join(",", summary.Tuning.BaseCqList)}] Effective=[{string.Join(",", summary.Tuning.EffectiveCqList)}]");
                    Console.WriteLine($"    VMAF: mean={sel.SelectedMeanVmaf:F2}, harmonic={sel.SelectedHarmonicMeanVmaf:F2}, p05={sel.SelectedP05Vmaf:F2}, p01={sel.SelectedP01Vmaf:F2}, min={sel.SelectedMinVmaf:F2}, frames={sel.TotalFrameCount}");
                }

                Console.WriteLine($"  Output: {summary.FinalOutputPath}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ERROR: {ex.Message}");
                overall.Errors.Add(new RunError { File = file, Error = ex.ToString() });
            }
        }

        overall.FinishedUtc = DateTime.UtcNow;
        var overallPath = Path.Combine(cfg.OutputFolder, $"overall_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        await JsonIO.WriteAsync(overallPath, overall, cts.Token);
        Console.WriteLine($"\nWrote {overallPath}");
        return 0;
    }

    private static async Task<VideoSummary> ProcessOneAsync(
        Config cfg, GpuGate gpu, string input, bool hasZscale, CancellationToken ct)
    {
        var id = SafeId(Path.GetFileNameWithoutExtension(input));
        var outDir = Path.Combine(cfg.OutputFolder, id);
        Directory.CreateDirectory(outDir);

        var probe = await Ffprobe.RunAsync(cfg, input, ct);
        var vstream = probe.Streams?.FirstOrDefault(s => s.CodecType == "video")
                     ?? throw new InvalidOperationException("No video stream found.");

        bool isHdr = SourceClassifier.IsHdr(vstream);

        // Fail early if HDR content needs zscale and it's unavailable.
        if (isHdr && !hasZscale)
            throw new InvalidOperationException(
                "HDR content detected but ffmpeg lacks zscale (libzimg). " +
                "VMAF measurement for HDR requires tone-mapping via zscale. " +
                "On Arch Linux: install 'zimg' and rebuild ffmpeg with --enable-libzimg.");

        // Select VMAF model based on source resolution: 4K model for ≥3840×2160, standard otherwise.
        string vmafModelPath = cfg.ResolveVmafModelPath(vstream.Width, vstream.Height);
        if (!File.Exists(vmafModelPath))
            throw new InvalidOperationException(
                $"VMAF model not found: {vmafModelPath}. " +
                $"On Arch Linux: sudo pacman -S vmaf (models install to /usr/share/model/).");

        Console.WriteLine($"  VMAF model: {Path.GetFileName(vmafModelPath)} " +
            $"(source {vstream.Width}x{vstream.Height})");

        ContentDetectionResult? detection = null;
        RestoreDecision restore;

        // Source-agnostic content detection per MPlayer guide §7.2.2.
        // Runs on all content regardless of source type (DVD, Blu-ray, etc.)
        detection = await ContentDetector.DetectAsync(cfg, input, vstream, ct);

        // Map ContentType enum → restore filter chain per MPlayer guide §7.2.3
        restore = RestoreStrategyMapper.MapToRestore(detection);

        // Preview gating: generate when confidence is low or parity is mismatched
        bool needPreviews =
            restore.Confidence < cfg.PreviewMaxConfidenceToGenerate ||
            detection.ParityMismatch;

        if (needPreviews)
        {
            var previewDir = Path.Combine(outDir, "previews");
            Directory.CreateDirectory(previewDir);
            restore.Previews = new List<PreviewArtifact>();

            // Pick evenly-spaced timestamps across the video for previews.
            var dur = probe.Format?.DurationSeconds ?? 0;
            var previewTs = new List<double>();
            if (dur > 0)
            {
                double step = dur / (cfg.PreviewCount + 1);
                for (int pi = 1; pi <= cfg.PreviewCount; pi++)
                    previewTs.Add(pi * step);
            }

            foreach (var t in previewTs)
            {
                ct.ThrowIfCancellationRequested();

                string baseTag = $"t{t.ToString("F1", CultureInfo.InvariantCulture)}";
                string ivtcPrev = Path.Combine(previewDir, $"preview_ivtc_{baseTag}.mkv");
                string deintPrev = Path.Combine(previewDir, $"preview_deint_{baseTag}.mkv");

                await PreviewGenerator.MakeLosslessPreviewAsync(cfg, input, ivtcPrev, RestoreMode.Ivtc, detection.DetectedParity, t, ct);
                await PreviewGenerator.MakeLosslessPreviewAsync(cfg, input, deintPrev, RestoreMode.Deinterlace, detection.DetectedParity, t, ct);

                restore.Previews.Add(new PreviewArtifact { TimestampSeconds = t, IvtcPath = ivtcPrev, DeintPath = deintPrev });
            }
        }

        // VMAF tuning: two-phase pipeline with pre-extracted reference clips.
        var tuning = await VmafTuner.TuneAsync(cfg, gpu, input, outDir, restore, isHdr, vmafModelPath, ct);

        // Final encode uses selected CQ directly (HDR ladder already shifted during tuning)
        int finalCq = tuning.Selection.SelectedCq;
        var finalOut = Path.Combine(outDir, $"{id}_av1_cq{finalCq}{cfg.OutputExtension}");
        await FinalEncoder.EncodeAsync(cfg, gpu, input, finalOut, restore, finalCq, ct);

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
        };

        await JsonIO.WriteAsync(Path.Combine(outDir, "log.json"), summary, ct);
        return summary;
    }

    private static string? GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == key) return args[i + 1];
        return null;
    }

    private static string SafeId(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        return sb.ToString();
    }
}
