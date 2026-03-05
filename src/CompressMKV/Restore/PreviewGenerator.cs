using System.Globalization;

namespace CompressMkv;

/// <summary>
/// Previews: CPU lossless (x264 CRF 0), original colorspace preserved (parity-aware).
/// </summary>
public static class PreviewGenerator
{
    public static async Task MakeLosslessPreviewAsync(
        Config cfg, string input, string output, RestoreMode mode, FieldParity parity, double startSeconds, CancellationToken ct)
    {
        if (File.Exists(output)) File.Delete(output);

        var (restoreFilter, outFps) = RestoreFilters.For(mode, parity);

        var args = new List<string>
        {
            "-y","-hide_banner","-loglevel","error",
            "-ss", startSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-i", input,
            "-t", cfg.PreviewDurationSeconds.ToString("F3", CultureInfo.InvariantCulture),
            "-map","0:v:0",
            "-an",
        };

        if (!string.IsNullOrWhiteSpace(restoreFilter))
            args.AddRange(new[] { "-vf", restoreFilter });

        if (!string.IsNullOrWhiteSpace(outFps))
            args.AddRange(new[] { "-r", outFps! });

        args.AddRange(new[]
        {
            "-c:v","libx264",
            "-preset","ultrafast",
            "-crf","0",
            "-x264-params","keyint=1",
            output
        });

        var (code, _, err) = await Proc.RunAsync(cfg.Ffmpeg, args.ToArray(), ct);
        if (code != 0) throw new InvalidOperationException($"preview encode failed: {err}");
    }
}
