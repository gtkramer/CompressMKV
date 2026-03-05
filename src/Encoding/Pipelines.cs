using System.Globalization;

namespace CompressMkv;

/// <summary>
/// ffmpeg pipelines (restoration + optional HDR tonemap proxy for VMAF).
/// </summary>
public static class Pipelines
{
    public static async Task EncodeSampleNvencAsync(Config cfg, string input, string output, SampleWindow w, RestoreDecision restore, int cq, CancellationToken ct)
    {
        if (File.Exists(output)) File.Delete(output);

        var args = new List<string>
        {
            "-y","-hide_banner","-loglevel","error",
            "-ss", w.StartSeconds.ToString("F3", CultureInfo.InvariantCulture),
        };

        if (cfg.UseNvdecForEncode)
            args.AddRange(new[] { "-hwaccel","cuda","-hwaccel_output_format","cuda" });

        args.AddRange(new[]
        {
            "-i", input,
            "-t", w.LengthSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-map","0:v:0",
            "-an",
        });

        if (!string.IsNullOrWhiteSpace(restore.FilterGraph))
            args.AddRange(new[] { "-vf", restore.FilterGraph });

        if (!string.IsNullOrWhiteSpace(restore.OutputFps) && restore.Mode != RestoreMode.None)
            args.AddRange(new[] { "-r", restore.OutputFps! });

        args.AddRange(new[]
        {
            "-c:v","av1_nvenc",
            "-preset", cfg.NvencPreset,
            "-rc:v","vbr",
            "-cq:v", cq.ToString(CultureInfo.InvariantCulture),
            "-rc-lookahead", cfg.RcLookahead.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt","p010le",
            output
        });

        var (code, _, err) = await Proc.RunAsync(cfg.Ffmpeg, args.ToArray(), ct);
        if (code != 0) throw new InvalidOperationException($"encode sample failed: {err}");
    }

    public static async Task RunVmafAsync(Config cfg, string referenceInput, string encodedInput, SampleWindow w, RestoreDecision restore, bool isHdr, string vmafLog, CancellationToken ct)
    {
        if (File.Exists(vmafLog)) File.Delete(vmafLog);

        string esc(string p) => p.Replace(@"\", @"\\").Replace(":", @"\:");

        string restoreFilter = string.IsNullOrWhiteSpace(restore.FilterGraph) ? "" : restore.FilterGraph + ",";

        string filter;
        if (isHdr)
        {
            filter = $@"
[0:v]trim=start={w.StartSeconds.ToString("F3", CultureInfo.InvariantCulture)}:duration={w.LengthSeconds.ToString("F3", CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS,{restoreFilter}
zscale=t=linear:npl=100,tonemap=tonemap=hable,zscale=t=bt709:m=bt709:r=tv,format=yuv420p[ref_sdr];
[1:v]setpts=PTS-STARTPTS,
zscale=t=linear:npl=100,tonemap=tonemap=hable,zscale=t=bt709:m=bt709:r=tv,format=yuv420p[enc_sdr];
[enc_sdr][ref_sdr]libvmaf=log_fmt=json:log_path={esc(vmafLog)}:model_path={esc(cfg.VmafModelPath)}:n_subsample=1
".Trim();
        }
        else
        {
            filter = $@"
[0:v]trim=start={w.StartSeconds.ToString("F3", CultureInfo.InvariantCulture)}:duration={w.LengthSeconds.ToString("F3", CultureInfo.InvariantCulture)},setpts=PTS-STARTPTS,{restoreFilter}format=yuv420p[ref];
[1:v]setpts=PTS-STARTPTS,format=yuv420p[enc];
[enc][ref]libvmaf=log_fmt=json:log_path={esc(vmafLog)}:model_path={esc(cfg.VmafModelPath)}:n_subsample=1
".Trim();
        }

        filter = filter.Replace("\r", "").Replace("\n", "");

        var args = new[]
        {
            "-hide_banner","-loglevel","error",
            "-i", referenceInput,
            "-i", encodedInput,
            "-lavfi", filter,
            "-f","null","-"
        };

        var (code, _, err) = await Proc.RunAsync(cfg.Ffmpeg, args, ct);
        if (code != 0) throw new InvalidOperationException($"vmaf failed: {err}");
    }

    public static async Task EncodeFullNvencAsync(Config cfg, string input, string output, RestoreDecision restore, int cq, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-y","-hide_banner","-loglevel","error",
        };

        if (cfg.UseNvdecForEncode)
            args.AddRange(new[] { "-hwaccel","cuda","-hwaccel_output_format","cuda" });

        args.AddRange(new[] { "-i", input });

        if (!string.IsNullOrWhiteSpace(restore.FilterGraph))
            args.AddRange(new[] { "-vf", restore.FilterGraph });

        if (!string.IsNullOrWhiteSpace(restore.OutputFps) && restore.Mode != RestoreMode.None)
            args.AddRange(new[] { "-r", restore.OutputFps! });

        args.AddRange(new[]
        {
            "-map","0",
            "-c:v","av1_nvenc",
            "-preset", cfg.NvencPreset,
            "-rc:v","vbr",
            "-cq:v", cq.ToString(CultureInfo.InvariantCulture),
            "-rc-lookahead", cfg.RcLookahead.ToString(CultureInfo.InvariantCulture),
            "-pix_fmt","p010le",
            "-c:a","copy",
            "-c:s","copy",
            output
        });

        var (code, _, err) = await Proc.RunAsync(cfg.Ffmpeg, args.ToArray(), ct);
        if (code != 0) throw new InvalidOperationException($"final encode failed: {err}");
    }
}
