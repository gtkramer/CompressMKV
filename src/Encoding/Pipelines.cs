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
            "-ss", w.StartSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", input,
            "-t", w.LengthSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-map", "0:v:0", "-an",
        };

        if (!string.IsNullOrWhiteSpace(restore.FilterGraph))
            args.AddRange(["-vf", restore.FilterGraph]);

        if (!string.IsNullOrWhiteSpace(restore.OutputFps) && restore.Mode != RestoreMode.None)
            args.AddRange(["-r", restore.OutputFps!]);

        args.AddRange([
            "-fps_mode", "cfr",
            "-c:v", "ffv1",
            "-level", "3",
            "-slicecrc", "1",
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
        Config cfg, string refInput, string output, int cq, CancellationToken ct)
    {
        if (File.Exists(output)) File.Delete(output);

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
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
            "-pix_fmt", "p010le",
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
        string vmafLog, string vmafModelPath, CancellationToken ct)
    {
        if (File.Exists(vmafLog)) File.Delete(vmafLog);

        string esc(string p) => p.Replace(@"\", @"\\").Replace(":", @"\:");

        string filter;
        if (isHdr)
        {
            // Tonemap both to SDR for VMAF. npl=100 is a reasonable default;
            // ideally this would come from MaxCLL/MaxFALL HDR metadata.
            filter = string.Concat(
                "[0:v]zscale=t=linear:npl=100,tonemap=tonemap=hable,",
                "zscale=t=bt709:m=bt709:r=tv,format=yuv420p[ref_sdr];",
                "[1:v]zscale=t=linear:npl=100,tonemap=tonemap=hable,",
                "zscale=t=bt709:m=bt709:r=tv,format=yuv420p[enc_sdr];",
                $"[enc_sdr][ref_sdr]libvmaf=log_fmt=json:log_path={esc(vmafLog)}:",
                $"model_path={esc(vmafModelPath)}:n_subsample=1");
        }
        else
        {
            // SDR: use yuv420p10le to preserve the 10-bit encode's full precision.
            filter = string.Concat(
                "[0:v]format=yuv420p10le[ref];",
                "[1:v]format=yuv420p10le[enc];",
                $"[enc][ref]libvmaf=log_fmt=json:log_path={esc(vmafLog)}:",
                $"model_path={esc(vmafModelPath)}:n_subsample=1");
        }

        var args = new[]
        {
            "-hide_banner", "-loglevel", "error",
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
    // ----------------------------------------------------------------
    public static async Task EncodeFullNvencAsync(
        Config cfg, string input, string output, RestoreDecision restore,
        int cq, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
        };

        if (cfg.UseNvdecForEncode)
            args.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "cuda"]);

        args.AddRange(["-i", input]);

        if (!string.IsNullOrWhiteSpace(restore.FilterGraph))
            args.AddRange(["-vf", restore.FilterGraph]);

        if (!string.IsNullOrWhiteSpace(restore.OutputFps) && restore.Mode != RestoreMode.None)
            args.AddRange(["-r", restore.OutputFps!]);

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
            "-pix_fmt", "p010le",
            "-fps_mode:v", "cfr",
            "-c:a", "copy",
            "-c:s", "copy",
            output
        ]);

        var (code, _, err) = await Proc.RunAsync(cfg.Ffmpeg, args.ToArray(), ct);
        if (code != 0) throw new InvalidOperationException($"final encode failed: {err}");
    }
}
