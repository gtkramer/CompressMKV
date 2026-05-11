using System.Globalization;

namespace MkvHelper;

/// <summary>
/// ffmpeg pipelines: lossless reference extraction, AV1 NVENC encoding,
/// VMAF measurement, and full-file final encode.
/// </summary>
public static class Pipelines
{
    // ----------------------------------------------------------------
    //  Phase 1: Extract a lossless reference clip with restored cadence.
    //  One per sample window — reused across all CQ levels.
    //  FFV1 lossless preserves full source fidelity at any bit depth.
    // ----------------------------------------------------------------
    public static async Task ExtractReferenceClipAsync(
        Config cfg, string input, string output, SampleWindow w,
        RestoreDecision restore, CancellationToken ct)
    {
        if (File.Exists(output)) File.Delete(output);

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-threads", cfg.FfmpegCpuThreads.ToString(CultureInfo.InvariantCulture),
            "-ss", w.StartSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", input,
            "-t", w.LengthSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-map", "0:v:0", "-an",
        };

        if (!string.IsNullOrWhiteSpace(restore.FilterGraph))
            args.AddRange(["-vf", restore.FilterGraph]);

        if (restore.OutputFps.HasValue && restore.Mode != RestoreMode.None)
            args.AddRange(["-r", restore.OutputFps.Value.ToString()]);

        args.AddRange([
            "-fps_mode", "cfr",
            "-c:v", "ffv1",
            "-level", "3",
            "-slicecrc", "1",
            "-threads", cfg.FfmpegCpuThreads.ToString(CultureInfo.InvariantCulture),
            output
        ]);

        var (code, _, err) = await ContainerTools.RunFfmpegAsync(args.ToArray(), ct);
        if (code != 0) throw new InvalidOperationException($"reference clip extraction failed: {err}");
    }

    // ----------------------------------------------------------------
    //  Phase 2: Encode a sample from a pre-extracted reference clip.
    //  No restore filters needed — already applied during extraction.
    //  Includes multipass, spatial/temporal AQ, CFR.
    // ----------------------------------------------------------------
    public static async Task EncodeSampleFromRefAsync(
        Config cfg, string refInput, string output, int cq,
        PipelineFormat format, CancellationToken ct)
    {
        if (File.Exists(output)) File.Delete(output);

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-threads", cfg.FfmpegGpuThreads.ToString(CultureInfo.InvariantCulture),
            "-i", refInput,
            "-map", "0:v:0", "-an",
            "-c:v", "av1_nvenc",
            "-preset", cfg.NvencPreset,
            "-multipass", "fullres",
            "-rc:v", "vbr",
            "-cq:v", cq.ToString(CultureInfo.InvariantCulture),
            "-b:v", "0",
            "-spatial-aq", "1",
            "-temporal-aq", "1",
            "-rc-lookahead", cfg.RcLookahead.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt", format.EncodePixFmt,
            "-fps_mode", "cfr",
            output
        };

        var (code, _, err) = await ContainerTools.RunFfmpegAsync(args.ToArray(), ct);
        if (code != 0) throw new InvalidOperationException($"sample encode failed: {err}");
    }

    // ----------------------------------------------------------------
    //  VMAF: compare reference clip vs encoded clip directly.
    //  Both are already progressive with matching cadence/framecount.
    //
    //  Frames are software-decoded (FFV1 has no NVDEC path; AV1 sample
    //  is small enough that NVDEC bookkeeping isn't worth a slot),
    //  uploaded to GPU via hwupload_cuda, then libvmaf_cuda runs the
    //  perceptual math on CUDA cores.
    //
    //  HDR caveat: zscale + tonemap stay on CPU.  GPU tonemap routes
    //  (tonemap_cuda, tonemap_opencl, libplacebo) all failed to compose
    //  with libvmaf_cuda in this version of FFmpeg — see the Containerfile
    //  header.  Only libvmaf itself moves to GPU on HDR runs; the colour-
    //  space conversion cost stays on CPU.  Gating on the CUDA slot rather
    //  than the CpuGate keeps CpuGate slots free for the FFV1 decode that
    //  feeds NVENC — the actual hot path for end-to-end throughput.
    //
    //  Bit depth: for SDR, compare at matching bit depth (yuv420p for
    //  8-bit, yuv420p10le for 10-bit) so an 8-bit reference's zero-padded
    //  LSBs don't bias the score against a 10-bit encode's real LSB
    //  content.  libvmaf caps at 10-bit.
    // ----------------------------------------------------------------
    public static async Task RunVmafDirectAsync(
        Config cfg, string refInput, string encInput, bool isHdr,
        HdrMetadata? hdrMetadata, PipelineFormat format,
        string vmafLog, string vmafModelVersion,
        CancellationToken ct)
    {
        if (File.Exists(vmafLog)) File.Delete(vmafLog);

        // Escape `:` and `\` inside filter-arg values (path goes into
        // libvmaf_cuda=log_path=...).  Filter argument syntax treats `:`
        // as a separator and `\` as an escape; both need to be doubled.
        string esc(string p) => p.Replace(@"\", @"\\").Replace(":", @"\:");

        // libvmaf_cuda runs the perceptual math on CUDA cores, not CPU
        // threads — passing n_threads to it spawns a thread pool that
        // races with vmaf_read_pictures and silently fails after the
        // first frame (the filter just emits "problem during
        // vmaf_read_pictures" and EINVAL).  n_subsample is fine.
        string vmafOpts =
            $"log_fmt=json:log_path={esc(vmafLog)}:" +
            $"model=version={vmafModelVersion}:" +
            $"n_subsample={cfg.LibvmafSubsample.ToString(CultureInfo.InvariantCulture)}";

        // For HDR, both ref and encode get a CPU-side tonemap chain
        // before being uploaded to GPU.  npl ("nominal peak luminance")
        // tells the tonemapper what brightness counts as the input peak
        // — get it wrong and highlights are crushed identically in both
        // streams, but their differences in those crushed regions become
        // invisible to VMAF.  Resolved from HDR side-data
        // (MaxCLL → mastering display max → 1000 nits HDR10 default).
        string hdrTonemap(string srcLabel, int npl) =>
            $"[{srcLabel}]zscale=t=linear:npl={npl.ToString(CultureInfo.InvariantCulture)}," +
            "tonemap=tonemap=hable,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

        string compareFmt = format.VmafCompareFormat;
        string refChain, encChain;
        if (isHdr)
        {
            int npl = (hdrMetadata ?? new HdrMetadata()).ResolveNpl();
            refChain = hdrTonemap("0:v", npl) + ",hwupload_cuda[ref_cuda];";
            encChain = hdrTonemap("1:v", npl) + ",hwupload_cuda[enc_cuda];";
        }
        else
        {
            refChain = $"[0:v]format={compareFmt},hwupload_cuda[ref_cuda];";
            encChain = $"[1:v]format={compareFmt},hwupload_cuda[enc_cuda];";
        }

        string filter = string.Concat(
            refChain, encChain,
            $"[enc_cuda][ref_cuda]libvmaf_cuda={vmafOpts}");

        var args = new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-threads", cfg.FfmpegCpuThreads.ToString(CultureInfo.InvariantCulture),
            "-i", refInput,
            "-i", encInput,
            "-lavfi", filter,
            "-f", "null", "-"
        };

        var (code, _, err) = await ContainerTools.RunFfmpegAsync(args, ct);
        if (code != 0) throw new InvalidOperationException($"vmaf failed: {err}");
    }

    // ----------------------------------------------------------------
    //  Final full-file encode: source → restore → AV1 NVENC.
    //  Includes multipass, spatial/temporal AQ, CFR.
    //  Audio and subtitles are copied through.
    //
    //  Loglevel is "warning" (not the usual "error") on IVTC-using runs so
    //  that fieldmatch's parity/cadence warnings reach our stderr capture.
    //  Stripped via SurfaceFieldmatchWarnings below — we only forward
    //  fieldmatch lines to the user, not every ffmpeg "info"-level chatter.
    // ----------------------------------------------------------------
    public static async Task EncodeFullNvencAsync(
        Config cfg, string input, string output, RestoreDecision restore,
        int cq, PipelineFormat format, CancellationToken ct,
        IPipelineLogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        bool isIvtc = restore.Mode == RestoreMode.Ivtc;
        string loglevel = isIvtc ? "warning" : "error";
        bool hasCpuFilter = !string.IsNullOrWhiteSpace(restore.FilterGraph);

        // Two NVDEC+NVENC pipeline shapes, depending on whether a CPU filter
        // sits between decode and encode:
        //
        //   No CPU filter (Progressive) — pure-GPU path.  Frames stay on the
        //     GPU end-to-end (-hwaccel_output_format cuda).  NVENC consumes
        //     cuda hwframes directly and matches the decoder's bit depth
        //     automatically — so we MUST NOT also request -pix_fmt nv12 here,
        //     which would otherwise force an unsupported cuda→nv12 auto-scale.
        //
        //   CPU filter (IVTC / Deinterlace) — download immediately at the
        //     matched bit depth.  The CPU filter operates in system memory,
        //     and NVENC re-uploads at the requested -pix_fmt.

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", loglevel,
            "-threads", cfg.FfmpegCpuThreads.ToString(CultureInfo.InvariantCulture),
        };

        if (cfg.UseNvdecForEncode)
        {
            string hwOutFormat = hasCpuFilter ? format.HwaccelOutputFormat : "cuda";
            args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", hwOutFormat]);
        }

        args.AddRange(["-i", input]);

        if (hasCpuFilter)
            args.AddRange(["-vf", restore.FilterGraph]);

        if (restore.OutputFps.HasValue && restore.Mode != RestoreMode.None)
            args.AddRange(["-r", restore.OutputFps.Value.ToString()]);

        args.AddRange([
            "-map", "0",
            "-c:v", "av1_nvenc",
            "-preset", cfg.NvencPreset,
            "-multipass", "fullres",
            "-rc:v", "vbr",
            "-cq:v", cq.ToString(CultureInfo.InvariantCulture),
            "-b:v", "0",
            "-spatial-aq", "1",
            "-temporal-aq", "1",
            "-rc-lookahead", cfg.RcLookahead.ToString(CultureInfo.InvariantCulture),
        ]);

        // Only set -pix_fmt when frames will be in CPU memory (CPU filter case
        // or no NVDEC).  In the pure-GPU pipeline NVENC selects the matched
        // bit depth from the cuda hwframes; setting -pix_fmt would force a
        // cuda→sw conversion that the auto-scaler can't perform.
        bool framesOnGpu = cfg.UseNvdecForEncode && !hasCpuFilter;
        if (!framesOnGpu)
            args.AddRange(["-pix_fmt", format.EncodePixFmt]);

        args.AddRange([
            "-fps_mode:v", "cfr",
            "-c:a", "copy",
            "-c:s", "copy",
            output
        ]);

        var (code, _, err) = await ContainerTools.RunFfmpegAsync(args.ToArray(), ct);
        if (code != 0) throw new InvalidOperationException($"final encode failed: {err}");

        if (isIvtc) SurfaceFieldmatchWarnings(err, logger);
    }

    /// <summary>
    /// Scans captured ffmpeg stderr for fieldmatch's runtime warnings (typically
    /// about declared-vs-detected field order mismatches) and forwards them to
    /// the per-file logger.  Other ffmpeg log lines are ignored to keep output
    /// focused.  Warnings persist in the per-file decisions.log for post-hoc
    /// inspection — they used to scroll past on the console only.
    /// </summary>
    internal static void SurfaceFieldmatchWarnings(string stderr, IPipelineLogger logger)
    {
        if (string.IsNullOrEmpty(stderr)) return;

        int count = 0;
        foreach (var raw in stderr.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.Contains("fieldmatch", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning($"fieldmatch: {line}");
                count++;
            }
        }

        if (count > 0)
            logger.LogWarning($"fieldmatch emitted {count} warning line(s) — review for parity/cadence issues.");
    }
}
