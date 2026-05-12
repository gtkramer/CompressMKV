namespace MkvHelper;

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
            (int code, string _, string _) = await Proc.RunAsync(Exe, ["--version"], ct);
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
        (int code, string _, string _) = await Proc.RunAsync(Exe, ["image", "exists", imageTag], ct);
        return code == 0;
    }

    public static async Task RemoveImageAsync(string imageTag, CancellationToken ct)
    {
        // -f: force-remove even if there are stopped containers referencing it.
        await Proc.RunAsync(Exe, ["rmi", "-f", imageTag], ct);
    }

    /// <summary>
    /// Build an image from a Containerfile in <paramref name="contextDir"/>.
    /// Streams build output to <paramref name="onLine"/> so callers can
    /// show progress.  Build output is simultaneously written to
    /// <paramref name="buildLogPath"/>.  Optional build args are passed
    /// through as <c>--build-arg KEY=VALUE</c>.  When <paramref name="noCache"/>
    /// is true, podman ignores its layer cache and re-runs every step
    /// from scratch — useful when apt repositories have new versions of
    /// pinned-by-name packages.
    /// </summary>
    public static async Task BuildAsync(
        string contextDir, string containerfile, string imageTag,
        string buildLogPath, Action<string> onLine, CancellationToken ct,
        IReadOnlyDictionary<string, string>? buildArgs = null,
        bool noCache = false)
    {
        await using StreamWriter logFile = new(buildLogPath, append: false);

        List<string> args = ["build", "-f", containerfile, "-t", imageTag];
        if (noCache)
            args.Add("--no-cache");
        if (buildArgs is not null)
        {
            foreach ((string k, string v) in buildArgs)
            {
                args.Add("--build-arg");
                args.Add($"{k}={v}");
            }
        }
        args.Add(contextDir);

        (int code, string stderr) = await Proc.RunStreamingAsync(Exe, args.ToArray(), line =>
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
