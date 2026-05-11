namespace CompressMkv;

/// <summary>
/// Thin wrapper around the `podman` binary.  All container operations
/// in this app go through here so the invocation surface stays small.
/// </summary>
public static class Podman
{
    public const string Exe = "podman";

    public static async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        try
        {
            var (code, _, _) = await Proc.RunAsync(Exe, new[] { "--version" }, ct);
            return code == 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// Quick best-effort check that the NVIDIA CDI device is set up.  Returns
    /// false if CDI generation hasn't been run on this machine.  We only use
    /// this for a friendlier error message — the actual GPU access is
    /// validated when the container is launched.
    /// </summary>
    public static bool HasNvidiaCdi() =>
        File.Exists("/etc/cdi/nvidia.yaml") || File.Exists("/var/run/cdi/nvidia.yaml");

    public static async Task<bool> ImageExistsAsync(string imageTag, CancellationToken ct)
    {
        var (code, _, _) = await Proc.RunAsync(Exe, new[] { "image", "exists", imageTag }, ct);
        return code == 0;
    }

    public static async Task RemoveImageAsync(string imageTag, CancellationToken ct)
    {
        // -f: force-remove even if there are stopped containers referencing it.
        await Proc.RunAsync(Exe, new[] { "rmi", "-f", imageTag }, ct);
    }

    /// <summary>
    /// Add an alias tag to an existing image.  Used to satisfy the
    /// hard-coded `FROM vmaf` in Netflix's Dockerfile.ffmpeg without
    /// permanently squatting on the global `vmaf` tag.
    /// </summary>
    public static async Task TagAsync(string source, string alias, CancellationToken ct)
    {
        var (code, _, err) = await Proc.RunAsync(Exe, new[] { "tag", source, alias }, ct);
        if (code != 0)
            throw new InvalidOperationException($"podman tag {source} {alias} failed: {err.Trim()}");
    }

    /// <summary>
    /// Remove an alias tag from an image.  Best-effort — if the tag is
    /// already gone (e.g. the image was deleted), don't throw.
    /// </summary>
    public static async Task UntagAsync(string imageTag, CancellationToken ct)
    {
        await Proc.RunAsync(Exe, new[] { "untag", imageTag }, ct);
    }

    /// <summary>
    /// Build an image from a Dockerfile in <paramref name="contextDir"/>.
    /// Streams build output to <paramref name="onLine"/> so callers can show
    /// progress.  Build logs are simultaneously appended to a file.
    /// </summary>
    public static async Task BuildAsync(
        string contextDir, string dockerfile, string imageTag,
        string buildLogPath, Action<string> onLine, CancellationToken ct)
    {
        await using var logFile = new StreamWriter(buildLogPath, append: false);

        var args = new[]
        {
            "build",
            "-f", dockerfile,
            "-t", imageTag,
            contextDir
        };

        var (code, stderr) = await Proc.RunStreamingAsync(Exe, args, line =>
        {
            logFile.WriteLine(line);
            onLine(line);
        }, ct);

        if (!string.IsNullOrEmpty(stderr))
            await logFile.WriteAsync(stderr);

        await logFile.FlushAsync(ct);

        if (code != 0)
            throw new InvalidOperationException(
                $"podman build failed (exit {code}). Build log: {buildLogPath}");
    }
}
