namespace CompressMkv;

/// <summary>
/// Validates that the ffmpeg installation has required filter support.
/// Call once at startup; returns zscale availability for later HDR checks.
/// </summary>
public static class FfmpegCapabilities
{
    /// <summary>
    /// Ensures libvmaf is available. Returns true if zscale (libzimg) is also available.
    /// Throws <see cref="InvalidOperationException"/> if libvmaf is missing.
    /// </summary>
    public static async Task<bool> ValidateAsync(Config cfg, CancellationToken ct)
    {
        var (code, stdout, _) = await Proc.RunAsync(cfg.Ffmpeg, ["-filters", "-hide_banner"], ct);
        if (code != 0)
            throw new InvalidOperationException($"Failed to query ffmpeg filters (exit code {code}).");

        if (!stdout.Contains("libvmaf"))
            throw new InvalidOperationException(
                "ffmpeg is missing libvmaf support. " +
                "On Arch Linux: ensure the 'vmaf' package is installed " +
                "and ffmpeg was built with --enable-libvmaf.");

        bool hasZscale = stdout.Contains("zscale");
        return hasZscale;
    }
}
