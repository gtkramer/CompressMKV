namespace MkvHelper;

/// <summary>
/// Single chokepoint for every ffmpeg/ffprobe invocation in the app.
/// Decides at call-time whether to shell out to the system binary or to
/// run our pinned, CUDA-enabled build via <c>podman run</c>.
///
/// Wiring: <see cref="ConfigureContainer"/> / <see cref="ConfigureNative"/>
/// is called once at CLI startup.  After that, every site that wants to run
/// ffmpeg calls into the static methods here.
///
/// Container mode binds the host's input + output dirs at the same paths
/// inside the container, so argument lists do not need path translation:
/// <c>/home/x/Videos/in.mp4</c> resolves identically in both modes.
/// <c>--userns=keep-id</c> preserves host file ownership on writes.
/// </summary>
public static class FfmpegRunner
{
    private static bool s_useContainer;
    private static string s_imageTag = "";
    private static readonly List<string> s_mounts = new();
    private static string s_ffmpegExe = "ffmpeg";
    private static string s_ffprobeExe = "ffprobe";

    public static bool UsingContainer => s_useContainer;
    public static string ImageTag => s_imageTag;

    /// <summary>Use the system ffmpeg/ffprobe.  Default before any configuration.</summary>
    public static void ConfigureNative(string ffmpeg = "ffmpeg", string ffprobe = "ffprobe")
    {
        s_useContainer = false;
        s_imageTag = "";
        s_mounts.Clear();
        s_ffmpegExe = ffmpeg;
        s_ffprobeExe = ffprobe;
    }

    /// <summary>
    /// Use ffmpeg/ffprobe from inside the built container image.  Mount the
    /// provided host directories so the same absolute paths work in args.
    /// </summary>
    public static void ConfigureContainer(string imageTag, IEnumerable<string> hostMounts)
    {
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

    // ---- Public ffmpeg/ffprobe invocations (replace direct Proc.RunAsync calls) ----

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunFfmpegAsync(
        string[] args, CancellationToken ct)
        => RunAsync(/*ffmpeg*/ true, args, ct);

    public static Task<(int ExitCode, string StdOut, string StdErr)> RunFfprobeAsync(
        string[] args, CancellationToken ct)
        => RunAsync(/*ffmpeg*/ false, args, ct);

    public static Task<(int ExitCode, string StdErr)> RunFfmpegStreamingAsync(
        string[] args, Action<string> onStdoutLine, CancellationToken ct)
        => RunStreamingAsync(/*ffmpeg*/ true, args, onStdoutLine, ct);

    // ---- Implementation ----

    private static Task<(int, string, string)> RunAsync(bool ffmpeg, string[] args, CancellationToken ct)
    {
        var (exe, finalArgs) = Resolve(ffmpeg, args);
        return Proc.RunAsync(exe, finalArgs, ct);
    }

    private static Task<(int, string)> RunStreamingAsync(
        bool ffmpeg, string[] args, Action<string> onLine, CancellationToken ct)
    {
        var (exe, finalArgs) = Resolve(ffmpeg, args);
        return Proc.RunStreamingAsync(exe, finalArgs, onLine, ct);
    }

    private static (string Exe, string[] Args) Resolve(bool ffmpeg, string[] originalArgs)
    {
        string binaryInsideContainer = ffmpeg ? "ffmpeg" : "ffprobe";
        string nativeExe = ffmpeg ? s_ffmpegExe : s_ffprobeExe;

        if (!s_useContainer)
            return (nativeExe, originalArgs);

        var podArgs = new List<string>
        {
            "run", "--rm",
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
        podArgs.Add(binaryInsideContainer);
        podArgs.AddRange(originalArgs);

        return (Podman.Exe, podArgs.ToArray());
    }
}
