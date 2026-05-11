namespace MkvHelper;

/// <summary>
/// Single chokepoint for invoking any external tool that lives inside our
/// dependency container — currently <c>ffmpeg</c>, <c>ffprobe</c>,
/// <c>mkvextract</c>, and <c>mkvmerge</c>.  Every site that wants to run
/// one of those tools goes through here so we have one source of truth
/// for "how do we execute things in the container."
///
/// Wiring: <see cref="Configure"/> is called once per command at startup
/// after <see cref="BuildState"/> has been resolved.  It stamps in the
/// image tag and the host-directory mounts.  After that, every Run* call
/// builds a <c>podman run</c> invocation around the requested tool.
///
/// Mounts use bind-source = bind-target so absolute paths resolve
/// identically inside and outside the container — no argument
/// translation needed.  <c>--userns=keep-id</c> keeps host file
/// ownership intact on writes.
/// </summary>
public static class ContainerTools
{
    private static string s_imageTag = "";
    private static readonly List<string> s_mounts = new();

    public static string ImageTag => s_imageTag;

    /// <summary>
    /// Configure for the current command: which container image to run, and
    /// which host directories to bind-mount into it.  Mounts are
    /// canonicalised to absolute paths and de-duplicated.
    /// </summary>
    public static void Configure(string imageTag, IEnumerable<string> hostMounts)
    {
        if (string.IsNullOrWhiteSpace(imageTag))
            throw new ArgumentException("imageTag is required.", nameof(imageTag));

        s_useContainer = true;
        s_imageTag = imageTag;
        s_mounts.Clear();
        foreach (var m in hostMounts)
        {
            if (string.IsNullOrWhiteSpace(m)) continue;
            string abs = Path.GetFullPath(m);
            if (!s_mounts.Contains(abs))
                s_mounts.Add(abs);
        }
    }

    /// <summary>
    /// Test-only escape hatch: route Run* calls to the system binary
    /// instead of <c>podman run</c>.  Lets the integration tests exercise
    /// detection / restoration / VMAF code paths against a system ffmpeg
    /// without requiring the container to be built.  Internal because
    /// production code should always go through <see cref="Configure"/>.
    /// </summary>
    internal static void ConfigureForTesting()
    {
        s_useContainer = false;
        s_imageTag = "";
        s_mounts.Clear();
    }

    private static bool s_useContainer;

    // ---- Tool invocations.  Each one builds the podman command, then
    // delegates to Proc for the actual subprocess work. ----

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunFfmpegAsync(
        string[] args, CancellationToken ct)
        => RunAsync("ffmpeg", args, ct);

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunFfprobeAsync(
        string[] args, CancellationToken ct)
        => RunAsync("ffprobe", args, ct);

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunMkvextractAsync(
        string[] args, CancellationToken ct)
        => RunAsync("mkvextract", args, ct);

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunMkvmergeAsync(
        string[] args, CancellationToken ct)
        => RunAsync("mkvmerge", args, ct);

    public static Task<(int ExitCode, string StdErr)> RunFfmpegStreamingAsync(
        string[] args, Action<string> onStdoutLine, CancellationToken ct)
        => RunStreamingAsync("ffmpeg", args, onStdoutLine, ct);

    // ---- Implementation ----

    private static Task<(int, string, string)> RunAsync(string tool, string[] args, CancellationToken ct)
    {
        var (exe, finalArgs) = BuildPodmanInvocation(tool, args);
        return Proc.RunAsync(exe, finalArgs, ct);
    }

    private static Task<(int, string)> RunStreamingAsync(
        string tool, string[] args, Action<string> onLine, CancellationToken ct)
    {
        var (exe, finalArgs) = BuildPodmanInvocation(tool, args);
        return Proc.RunStreamingAsync(exe, finalArgs, onLine, ct);
    }

    private static (string Exe, string[] Args) BuildPodmanInvocation(string tool, string[] toolArgs)
    {
        // Test-mode bypass: invoke the system binary directly with no
        // podman wrapping at all.  See ConfigureForTesting().
        if (!s_useContainer)
            return (tool, toolArgs);

        if (string.IsNullOrEmpty(s_imageTag))
            throw new InvalidOperationException(
                "ContainerTools.Configure must be called before any Run* invocation.  " +
                "This is a wiring bug — the command's startup forgot to set the mounts.");

        var podArgs = new List<string>
        {
            "run", "--rm",
            "--network=none",            // ffmpeg/mkvtoolnix don't need network
            "--device", "nvidia.com/gpu=all",
            "--userns=keep-id",
            "--security-opt=label=disable",
        };
        foreach (var mount in s_mounts)
        {
            podArgs.Add("-v");
            podArgs.Add($"{mount}:{mount}");
        }
        podArgs.Add(s_imageTag);
        podArgs.Add(tool);
        podArgs.AddRange(toolArgs);

        return (Podman.Exe, podArgs.ToArray());
    }
}
