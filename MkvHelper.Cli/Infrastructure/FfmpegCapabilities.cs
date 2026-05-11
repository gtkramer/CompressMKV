namespace MkvHelper;

/// <summary>
/// Validates the running ffmpeg installation has the filters we need.
/// Probes whatever <see cref="FfmpegRunner"/> resolves to — system ffmpeg
/// when no container build exists, the pinned CUDA build when it does.
/// </summary>
public sealed class FfmpegCapabilitiesResult
{
    public bool HasLibvmaf { get; init; }
    public bool HasLibvmafCuda { get; init; }
    public bool HasZscale { get; init; }
}

public static class FfmpegCapabilities
{
    /// <summary>
    /// Probes the active ffmpeg's filter set.  Throws if libvmaf is missing
    /// (the app fundamentally needs it).  Reports libvmaf_cuda + zscale as
    /// soft capabilities so the pipeline can route around them.
    /// </summary>
    public static async Task<FfmpegCapabilitiesResult> ValidateAsync(CancellationToken ct)
    {
        var (code, stdout, _) = await FfmpegRunner.RunFfmpegAsync(
            new[] { "-filters", "-hide_banner" }, ct);

        if (code != 0)
            throw new InvalidOperationException($"Failed to query ffmpeg filters (exit code {code}).");

        bool hasLibvmaf = stdout.Contains("libvmaf");
        if (!hasLibvmaf)
            throw new InvalidOperationException(
                "ffmpeg is missing libvmaf support.  Build the bundled container with " +
                "`mkvhelper dependency build` (it ships ffmpeg + libvmaf + libvmaf_cuda), " +
                "or ensure your system ffmpeg was built with --enable-libvmaf.");

        return new FfmpegCapabilitiesResult
        {
            HasLibvmaf = true,
            HasLibvmafCuda = stdout.Contains("libvmaf_cuda"),
            HasZscale = stdout.Contains("zscale"),
        };
    }
}
