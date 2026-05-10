// Program.cs
// Build: dotnet build
// Run:   dotnet run --project CompressMKV.CLI -- --input /path/to/mkvs --output /path/to/out
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

using System.Diagnostics;
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
            Console.WriteLine("Usage: compress-mkv --input <folder> [--output <folder>]");
            return 2;
        }

        var cfg = new Config
        {
            InputFolder = input!,
            OutputFolder = output ?? "./out",
            Ffmpeg = "ffmpeg",
            Ffprobe = "ffprobe",

            // VMAF models (vmaf_v0.6.1 / vmaf_4k_v0.6.1) come from libvmaf's
            // built-in versions — no /usr/share/model/ dependency required.

            // RTX 5080: 2 NVENC + 2 NVDEC sessions.
            // GpuGate semaphores are the sole concurrency control for GPU work —
            // files flow freely through CPU phases and block only on encoder/decoder slots.
            NvencSlots = 2,
            NvdecSlots = 2,

            // VMAF tuning — stratified random sampling + per-frame score aggregation
            CandidateCq = [16, 18, 20, 22, 24, 26, 28, 30, 32, 34],
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
        Console.WriteLine(
            $"Concurrency: {cfg.MaxConcurrentFiles} files in flight, " +
            $"{cfg.MaxConcurrentCpuFfmpegOps} CPU ffmpeg ops, " +
            $"{cfg.NvencSlots} NVENC + {cfg.NvdecSlots} NVDEC, " +
            $"{cfg.FfmpegCpuThreads} threads/CPU op, " +
            $"{cfg.LibvmafThreads} threads/libvmaf.");

        var gpu = new GpuGate(cfg.NvencSlots, cfg.NvdecSlots);
        var cpu = new CpuGate(cfg.MaxConcurrentCpuFfmpegOps);
        var overall = new OverallSummary { StartedUtc = DateTime.UtcNow, Config = cfg };

        await Parallel.ForEachAsync(
            files.Select((file, i) => (file, idx: i + 1)),
            new ParallelOptions
            {
                CancellationToken = cts.Token,
                MaxDegreeOfParallelism = cfg.MaxConcurrentFiles,
            },
            async (item, ct) =>
            {
                var (file, idx) = item;
                var id = SafeId(Path.GetFileNameWithoutExtension(file));
                var outDir = Path.Combine(cfg.OutputFolder, id);
                var logPath = Path.Combine(outDir, "log.json");

                // Resume: if a prior run completed successfully, load its result and skip.
                if (File.Exists(logPath))
                {
                    try
                    {
                        var prior = await JsonIO.ReadAsync<VideoSummary>(logPath, ct);
                        if (prior != null)
                        {
                            lock (overall) overall.Videos.Add(prior);
                            Console.WriteLine($"  [{idx}/{files.Count}] Skipped (already completed): {file}");
                            return;
                        }
                    }
                    catch
                    {
                        // Corrupt log — treat as incomplete and reprocess.
                    }
                }

                // Incomplete prior run: wipe intermediate artifacts and start fresh.
                CleanIntermediates(outDir);

                Console.WriteLine($"\n[{idx}/{files.Count}] {file}");
                try
                {
                    var summary = await ProcessOneAsync(cfg, gpu, cpu, file, hasZscale, ct);
                    lock (overall) overall.Videos.Add(summary);

                    Console.WriteLine($"  [{idx}/{files.Count}] HDR: {summary.IsHdr}");

                    if (summary.ContentDetection != null)
                    {
                        var det = summary.ContentDetection;
                        Console.WriteLine($"  [{idx}/{files.Count}] ContentType: {det.ContentType} (confidence={det.Confidence:P0})");
                        Console.WriteLine($"    Frames: {det.TotalFramesAnalyzed:N0} (P={det.ProgressiveFrameCount:N0}, I={det.InterlacedFrameCount:N0}, U={det.UndeterminedFrameCount:N0})");
                        Console.WriteLine($"    Progressive fraction: {det.GlobalProgressiveFraction:P2}");
                        Console.WriteLine($"    Telecine cadence match rate: {det.TelecineCadenceMatchRate:P2}");
                        Console.WriteLine($"    I-frames in 3:2 cycles: {det.InterlacedFramesInCadenceRatio:P2}");
                        Console.WriteLine($"    Source fps: {det.SourceFps?.ToString() ?? "?"}" +
                            $" ({(det.IsNtscFamilyFps ? "NTSC family" : "non-NTSC")}" +
                            $", {(det.SourceIsLikelyCfr ? "CFR" : "VFR")})");
                        Console.WriteLine($"    Parity: {det.DetectedParity}" +
                            (det.ParityFromNtscFallback ? " (NTSC fallback)" : "") +
                            $" (raw TFF={det.RawIdetTffCount:N0}, BFF={det.RawIdetBffCount:N0})");
                        if (det.ParityMismatch)
                            Console.WriteLine($"    PARITY MISMATCH: idet={det.DetectedParity} vs ffprobe={det.FfprobeMappedParity} (ffprobe field_order={det.FfprobeFieldOrder})");
                        if (!det.IdetAggregateAgrees)
                            Console.WriteLine($"    PARSER WARNING: idet aggregate disagrees with per-frame stream (see prior log line).");
                        Console.WriteLine($"    Reason: {det.Reason}");
                    }

                    if (summary.Restore != null)
                    {
                        Console.WriteLine($"  [{idx}/{files.Count}] Restore: {summary.Restore.Mode} → filter=\"{summary.Restore.FilterGraph}\", fps={summary.Restore.OutputFps?.ToString() ?? "native"}");
                        Console.WriteLine(summary.Restore.Previews is { Count: > 0 }
                            ? $"  Previews: generated ({summary.Restore.Previews.Count})"
                            : "  Previews: not generated");
                    }

                    if (summary.Tuning?.Selection != null)
                    {
                        var sel = summary.Tuning.Selection;
                        Console.WriteLine($"  [{idx}/{files.Count}] CQ selected (final): {summary.FinalCq}");
                        if (summary.Tuning.HdrCqShiftApplied)
                            Console.WriteLine($"    HDR CQ ladder shift applied: -{summary.Tuning.HdrCqShiftDelta}  Base=[{string.Join(",", summary.Tuning.BaseCqList)}] Effective=[{string.Join(",", summary.Tuning.EffectiveCqList)}]");
                        Console.WriteLine($"    VMAF: mean={sel.SelectedMeanVmaf:F2}, harmonic={sel.SelectedHarmonicMeanVmaf:F2}, p05={sel.SelectedP05Vmaf:F2}, p01={sel.SelectedP01Vmaf:F2}, min={sel.SelectedMinVmaf:F2}, frames={sel.TotalFrameCount}");
                    }

                    Console.WriteLine($"  [{idx}/{files.Count}] Output: {summary.FinalOutputPath}");

                    if (summary.OutputVerification is { } v)
                    {
                        string status = v.Skipped ? "SKIPPED" : (v.Passed ? "PASSED" : "FAILED");
                        Console.WriteLine($"  [{idx}/{files.Count}] Verification: {status} — {v.Notes}");
                        foreach (var w in v.Warnings)
                            Console.WriteLine($"    ! {w}");
                    }

                    if (summary.Timings is { } tm)
                    {
                        Console.WriteLine(
                            $"  [{idx}/{files.Count}] Timings (total {tm.Total.TotalSeconds:F1}s): " +
                            $"detect={tm.Detection.TotalSeconds:F1}s, " +
                            $"prev={tm.Previews.TotalSeconds:F1}s, " +
                            $"tune.p1={tm.TuningPhase1.TotalSeconds:F1}s, " +
                            $"tune.p2={tm.TuningPhase2.TotalSeconds:F1}s, " +
                            $"final={tm.FinalEncode.TotalSeconds:F1}s, " +
                            $"verify={tm.Verification.TotalSeconds:F1}s.");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  [{idx}/{files.Count}] ERROR: {ex.Message}");
                    lock (overall) overall.Errors.Add(new RunError { File = file, Error = ex.ToString() });
                }
            });

        overall.FinishedUtc = DateTime.UtcNow;
        var overallPath = Path.Combine(cfg.OutputFolder, $"overall_{DateTime.UtcNow:yyyyMMdd_HHmmss}.json");
        await JsonIO.WriteAsync(overallPath, overall, cts.Token);
        Console.WriteLine($"\nWrote {overallPath}");
        return 0;
    }

    private static async Task<VideoSummary> ProcessOneAsync(
        Config cfg, GpuGate gpu, CpuGate cpu, string input, bool hasZscale, CancellationToken ct)
    {
        var totalSw = Stopwatch.StartNew();
        var timings = new PhaseTimings();
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

        // Extract HDR mastering / Content-Light-Level metadata so the VMAF tonemap
        // chain uses the correct nominal peak luminance (npl).  Only meaningful
        // for HDR sources; SDR files skip the second ffprobe call entirely.
        HdrMetadata? hdrMetadata = isHdr
            ? await SourceClassifier.ExtractHdrMetadataAsync(cfg, input, ct)
            : null;
        if (isHdr)
        {
            int npl = (hdrMetadata ?? new HdrMetadata()).ResolveNpl();
            string source = hdrMetadata?.MaxCll is { } ? "MaxCLL"
                          : hdrMetadata?.MasteringDisplayMaxLuminance is { } ? "mastering display"
                          : "HDR10 default (no metadata found)";
            Console.WriteLine($"  HDR npl: {npl} nits (from {source})");
        }

        // Derive the source's pipeline format once.  Flows through every ffmpeg
        // call so encode pix_fmt, hwaccel output format, and VMAF compare format
        // all stay at the source's native bit depth — 8-bit sources end up in
        // 8-bit encodes (saving ~10-15% file size vs forcing p010le), 10-bit
        // sources stay 10-bit end-to-end.
        var pipelineFormat = PipelineFormat.FromStream(vstream);
        Console.WriteLine($"  Pipeline: {pipelineFormat}");

        // Select VMAF model version based on source resolution: 4K model for
        // ≥3840×2160, standard (1080p-tuned) otherwise.  These are libvmaf's
        // built-in version names — no model file lookup needed.
        string vmafModelVersion = cfg.ResolveVmafModelVersion(vstream.Width, vstream.Height);
        Console.WriteLine($"  VMAF model: {vmafModelVersion} " +
            $"(source {vstream.Width}x{vstream.Height})");

        ContentDetectionResult? detection = null;
        RestoreDecision restore;

        // Source-agnostic content detection per MPlayer guide §7.2.2.
        // Runs on all content regardless of source type (DVD, Blu-ray, etc.)
        // Gate NVDEC slot when hardware-accelerated detection is enabled, plus
        // a CPU slot for the idet filter and any software decode work.
        var sw = Stopwatch.StartNew();
        using (await gpu.AcquireAsync(nvenc: 0, nvdec: cfg.UseHwaccelForDetection ? 1 : 0, ct))
        using (await cpu.AcquireAsync(ct))
        {
            detection = await ContentDetector.DetectAsync(cfg, input, vstream, ct);
        }
        timings.Detection = sw.Elapsed;

        // Map ContentType enum → restore filter chain per MPlayer guide §7.2.3
        restore = RestoreStrategyMapper.MapToRestore(detection);

        // Preview gating: generate when confidence is low or parity is mismatched
        bool needPreviews =
            restore.Confidence < cfg.PreviewMaxConfidenceToGenerate ||
            detection.ParityMismatch;

        if (needPreviews)
        {
            sw.Restart();
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

                using (await cpu.AcquireAsync(ct))
                    await PreviewGenerator.MakeLosslessPreviewAsync(cfg, input, ivtcPrev, RestoreMode.Ivtc, detection.DetectedParity, t, ct);
                using (await cpu.AcquireAsync(ct))
                    await PreviewGenerator.MakeLosslessPreviewAsync(cfg, input, deintPrev, RestoreMode.Deinterlace, detection.DetectedParity, t, ct);

                restore.Previews.Add(new PreviewArtifact { TimestampSeconds = t, IvtcPath = ivtcPrev, DeintPath = deintPrev });
            }
            timings.Previews = sw.Elapsed;
        }

        // VMAF tuning: two-phase pipeline with pre-extracted reference clips.
        var tuning = await VmafTuner.TuneAsync(cfg, gpu, cpu, input, outDir, restore,
            isHdr, hdrMetadata, pipelineFormat, vmafModelVersion, ct);
        timings.TuningPhase1 = tuning.Phase1Elapsed;
        timings.TuningPhase2 = tuning.Phase2Elapsed;

        // Final encode uses selected CQ directly (HDR ladder already shifted during tuning).
        // The restore filter runs on CPU even though NVENC handles encoding, so a CPU slot
        // is acquired for the duration in addition to the NVENC slot inside FinalEncoder.
        int finalCq = tuning.Selection.SelectedCq;
        var finalOut = Path.Combine(outDir, $"{id}_av1_cq{finalCq}{cfg.OutputExtension}");
        sw.Restart();
        using (await cpu.AcquireAsync(ct))
        {
            await FinalEncoder.EncodeAsync(cfg, gpu, input, finalOut, restore, finalCq, pipelineFormat, ct);
        }
        timings.FinalEncode = sw.Elapsed;

        // Trust-but-verify: confirm the final output reflects the chosen restoration.
        // For pass-through runs this is a no-op.  Acquires NVDEC for the AV1 decode pass
        // plus a CPU slot for the idet filter.
        sw.Restart();
        OutputVerificationResult verification;
        using (await gpu.AcquireAsync(nvenc: 0, nvdec: cfg.UseHwaccelForDetection ? 1 : 0, ct))
        using (await cpu.AcquireAsync(ct))
        {
            verification = await OutputVerifier.VerifyAsync(cfg, finalOut, restore, ct);
        }
        timings.Verification = sw.Elapsed;
        timings.Total = totalSw.Elapsed;

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
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        return sb.ToString();
    }
}
