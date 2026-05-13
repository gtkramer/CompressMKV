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
    //
    //  Decode side can run on CPU or NVDEC; FFV1 encode is always CPU
    //  (no NVENC FFV1 encoder exists).  ResourcePool picks the path
    //  based on which resources are free — see Config.RefExtractAlternativesFor.
    //
    //  Thread pinning: ffmpeg's -threads is per-stream, so a single -threads
    //  value before -i applies to the decoder and a separate -threads before
    //  the output applies to the FFV1 encoder; the two pools run concurrently.
    //  We therefore split the granted CPU budget into decode + encode halves
    //  so the combined peak matches what the pool reserved.  On the NVDEC
    //  path decode is hardware and its -threads is meaningless, so the bulk
    //  of the budget goes to the FFV1 encoder.
    // ----------------------------------------------------------------
    public static async Task ExtractReferenceClipAsync(
        Config cfg, string input, string output, SampleWindow w,
        RestoreDecision restore, FfprobeStream vstream, bool useHwaccel, int cpuBudget,
        CancellationToken ct)
    {
        if (File.Exists(output)) File.Delete(output);

        int decodeThreads, encodeThreads;
        if (useHwaccel)
        {
            // NVDEC handles decode in hardware; decode-side -threads only
            // governs the parser/bsf and barely consumes CPU.  Give the
            // rest of the budget to the FFV1 encoder.
            decodeThreads = 1;
            encodeThreads = Math.Max(1, cpuBudget - 1);
        }
        else
        {
            // Split roughly evenly between sw decode and FFV1 encode.  Encode
            // gets the larger half on odd budgets (FFV1 slice parallelism
            // scales better than HEVC decode threading past ~2 cores).
            decodeThreads = Math.Max(1, cpuBudget / 2);
            encodeThreads = Math.Max(1, cpuBudget - decodeThreads);
        }

        List<string> args =
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-threads", decodeThreads.ToString(CultureInfo.InvariantCulture),
        ];

        if (useHwaccel)
        {
            // NVDEC decode → auto-download in source's native bit depth.
            // CPU filters (IVTC/Deint when present) and the FFV1 encoder
            // run in system memory; the GPU just handles decode.
            PipelineFormat format = PipelineFormat.FromStream(vstream);
            args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", format.HwaccelOutputFormat]);
        }

        args.AddRange([
            "-ss", w.StartSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", input,
            "-t", w.LengthSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-map", "0:v:0", "-an",
        ]);

        if (!string.IsNullOrWhiteSpace(restore.FilterGraph))
            args.AddRange(["-vf", restore.FilterGraph]);

        if (restore.OutputFps.HasValue && restore.Mode != RestoreMode.None)
            args.AddRange(["-r", restore.OutputFps.Value.ToString()]);

        args.AddRange([
            "-fps_mode", "cfr",
            "-c:v", "ffv1",
            "-level", "3",
            "-slicecrc", "1",
            "-threads", encodeThreads.ToString(CultureInfo.InvariantCulture),
            output
        ]);

        (int code, string _, string err) = await ContainerTools.RunFfmpegAsync(args.ToArray(), ct);
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

        List<string> args =
        [
            "-y", "-hide_banner", "-loglevel", "error",
            "-threads", cfg.SampleEncodeThreads.ToString(CultureInfo.InvariantCulture),
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
        ];

        (int code, string _, string err) = await ContainerTools.RunFfmpegAsync(args.ToArray(), ct);
        if (code != 0) throw new InvalidOperationException($"sample encode failed: {err}");
    }

    // ----------------------------------------------------------------
    //  VMAF: compare reference clip vs encoded clip directly.
    //  Both are already progressive with matching cadence/framecount.
    //
    //  Decode split:
    //    - FFV1 reference  → CPU.  FFV1 has no NVDEC path (and never will;
    //                        it's a CPU-archival codec).
    //    - AV1 sample      → NVDEC.  Auto-downloads to system memory in
    //                        the source's native bit depth so the CPU-side
    //                        tonemap chain consumes it unchanged.  Costs one
    //                        extra GPU→CPU DMA per frame (~0.3 ms at 4K on
    //                        PCIe 5.0) and frees one CPU decode thread.
    //
    //  After decode both streams meet on CPU, hit the tonemap chain (HDR
    //  only), then hwupload_cuda lifts them to the GPU where libvmaf_cuda
    //  runs the perceptual math on CUDA cores.
    //
    //  HDR caveat: zscale + tonemap stay on CPU.  GPU tonemap routes
    //  (tonemap_cuda, tonemap_opencl, libplacebo) all failed to compose
    //  with libvmaf_cuda in this version of FFmpeg — see the Containerfile
    //  header.  Only libvmaf itself moves to GPU on HDR runs; the colour-
    //  space conversion cost stays on CPU and is accounted for via
    //  cfg.VmafFilterThreads.
    //
    //  CPU accounting: -threads applies per-input decoder.  The FFV1 input
    //  consumes VmafFfmpegThreads of real CPU; the AV1/NVDEC input's
    //  -threads only sizes the parser/bsf and is effectively free.
    //  -filter_threads sizes the filtergraph pool.  Total real CPU =
    //  1 × VmafFfmpegThreads + VmafFilterThreads = cfg.VmafThreads,
    //  matching what the ResourcePool reserved.
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

        // -threads sets the per-input decoder thread count.  For the FFV1
        // ref it's a real CPU thread count; for the AV1 sample (NVDEC) it
        // only sizes the parser/bsf and barely consumes CPU.  -hwaccel cuda
        // -hwaccel_output_format <native> on the AV1 input triggers NVDEC
        // decode with auto-download to system memory in the source's native
        // bit depth, so the tonemap chain consumes it unchanged.
        // -filter_threads sizes the filtergraph engine running zscale /
        // tonemap / format / hwupload.  Total CPU = 1 × VmafFfmpegThreads
        // + VmafFilterThreads = cfg.VmafThreads, matching the ResourcePool
        // reservation.
        List<string> args =
        [
            "-hide_banner", "-loglevel", "error",
            "-threads", cfg.VmafFfmpegThreads.ToString(CultureInfo.InvariantCulture),
            "-filter_threads", cfg.VmafFilterThreads.ToString(CultureInfo.InvariantCulture),
            "-i", refInput,
            "-hwaccel", "cuda", "-hwaccel_output_format", format.HwaccelOutputFormat,
            "-i", encInput,
            "-lavfi", filter,
            "-f", "null", "-"
        ];

        (int code, string _, string err) = await ContainerTools.RunFfmpegAsync(args.ToArray(), ct);
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
        int cq, PipelineFormat format, int cpuBudget, CancellationToken ct,
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
        //     CPU work is orchestration only; the caller passes the smaller
        //     FinalEncodeProgressiveThreads budget for the pin.
        //
        //   CPU filter (IVTC / Deinterlace) — download immediately at the
        //     matched bit depth.  The CPU filter operates in system memory,
        //     and NVENC re-uploads at the requested -pix_fmt.  Caller passes
        //     the larger FinalEncodeFilteredThreads budget to cover the filter.

        string hwOutFormat = hasCpuFilter ? format.HwaccelOutputFormat : "cuda";
        List<string> args =
        [
            "-y", "-hide_banner", "-loglevel", loglevel,
            "-threads", cpuBudget.ToString(CultureInfo.InvariantCulture),
            "-hwaccel", "cuda", "-hwaccel_output_format", hwOutFormat,
            "-i", input,
        ];

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

        // -pix_fmt is only valid when frames are already in CPU memory (CPU
        // filter case).  In the pure-GPU path NVENC reads matched bit depth
        // straight from the cuda hwframes; setting -pix_fmt would force a
        // cuda→sw conversion that the auto-scaler can't perform.
        if (hasCpuFilter)
            args.AddRange(["-pix_fmt", format.EncodePixFmt]);

        args.AddRange([
            "-fps_mode:v", "cfr",
            "-c:a", "copy",
            "-c:s", "copy",
            output
        ]);

        (int code, string _, string err) = await ContainerTools.RunFfmpegAsync(args.ToArray(), ct);
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
        foreach (string raw in stderr.Split('\n'))
        {
            string line = raw.Trim();
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
