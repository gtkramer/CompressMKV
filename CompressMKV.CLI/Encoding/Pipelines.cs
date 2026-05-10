using System.Globalization;

namespace CompressMkv;

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

        var (code, _, err) = await Proc.RunAsync(cfg.Ffmpeg, args.ToArray(), ct);
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

        var (code, _, err) = await Proc.RunAsync(cfg.Ffmpeg, args.ToArray(), ct);
        if (code != 0) throw new InvalidOperationException($"sample encode failed: {err}");
    }

    // ----------------------------------------------------------------
    //  VMAF: compare reference clip vs encoded clip directly.
    //  Both are already progressive with matching cadence/framecount.
    //  No trim, no restore — just format conversion + libvmaf.
    //  SDR: compare at yuv420p10le to preserve 10-bit encode precision.
    //  HDR: tonemap both to SDR via zscale+hable before VMAF.
    //  CPU-only — does not consume GPU resources.
    // ----------------------------------------------------------------
    public static async Task RunVmafDirectAsync(
        Config cfg, string refInput, string encInput, bool isHdr,
        HdrMetadata? hdrMetadata, PipelineFormat format,
        string vmafLog, string vmafModelVersion, CancellationToken ct)
    {
        if (File.Exists(vmafLog)) File.Delete(vmafLog);

        string esc(string p) => p.Replace(@"\", @"\\").Replace(":", @"\:");

        // Use libvmaf's built-in model versions instead of file paths.  Model
        // names like "vmaf_v0.6.1" and "vmaf_4k_v0.6.1" are compiled into
        // libvmaf, so no /usr/share/model/ dependency is needed and the
        // filter graph stays portable across distros.
        // libvmaf threads: capped via Config so total CPU stays in-budget when
        // multiple VMAF runs are in flight (gated by CpuGate at the caller).
        string vmafOpts =
            $"log_fmt=json:log_path={esc(vmafLog)}:" +
            $"model=version={vmafModelVersion}:" +
            $"n_threads={cfg.LibvmafThreads.ToString(CultureInfo.InvariantCulture)}:" +
            $"n_subsample={cfg.LibvmafSubsample.ToString(CultureInfo.InvariantCulture)}";

        string filter;
        if (isHdr)
        {
            // Tonemap both ref and encode to SDR before VMAF.  npl ("nominal peak
            // luminance") tells the tonemapper what brightness counts as the input
            // peak — get it wrong and highlights are crushed identically in both
            // streams, but their differences in those crushed regions become
            // invisible to VMAF.  Resolve from HDR side-data (MaxCLL → mastering
            // display max → 1000 nits HDR10 default).
            int npl = (hdrMetadata ?? new HdrMetadata()).ResolveNpl();
            string nplStr = npl.ToString(CultureInfo.InvariantCulture);

            filter = string.Concat(
                $"[0:v]zscale=t=linear:npl={nplStr},tonemap=tonemap=hable,",
                "zscale=t=bt709:m=bt709:r=tv,format=yuv420p[ref_sdr];",
                $"[1:v]zscale=t=linear:npl={nplStr},tonemap=tonemap=hable,",
                "zscale=t=bt709:m=bt709:r=tv,format=yuv420p[enc_sdr];",
                $"[enc_sdr][ref_sdr]libvmaf={vmafOpts}");
        }
        else
        {
            // SDR: compare at the source's matched bit depth.  An 8-bit reference
            // converted to yuv420p10le would have zero-padded LSBs while a
            // 10-bit encode has genuine LSB content — the resulting LSB
            // differences are sub-perceptual but contribute a small bias to VMAF.
            // format.VmafCompareFormat resolves to yuv420p for 8-bit sources,
            // yuv420p10le for 10/12-bit sources (libvmaf caps at 10-bit).
            string compareFmt = format.VmafCompareFormat;
            filter = string.Concat(
                $"[0:v]format={compareFmt}[ref];",
                $"[1:v]format={compareFmt}[enc];",
                $"[enc][ref]libvmaf={vmafOpts}");
        }

        var args = new[]
        {
            "-hide_banner", "-loglevel", "error",
            "-threads", cfg.FfmpegCpuThreads.ToString(CultureInfo.InvariantCulture),
            "-i", refInput,
            "-i", encInput,
            "-lavfi", filter,
            "-f", "null", "-"
        };

        var (code, _, err) = await Proc.RunAsync(cfg.Ffmpeg, args, ct);
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

        var (code, _, err) = await Proc.RunAsync(cfg.Ffmpeg, args.ToArray(), ct);
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
