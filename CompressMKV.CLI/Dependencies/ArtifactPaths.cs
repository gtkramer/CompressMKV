namespace CompressMkv;

/// <summary>
/// Filesystem layout for the VMAF/FFmpeg build artifacts under
/// XDG_DATA_HOME (~/.local/share/compressmkv/).
///
///   compressmkv/
///   ├── state.json              Build metadata (version, image tag, built-at).
///   ├── vmaf/<tag>/             Cloned Netflix/vmaf source at the built tag.
///   ├── build-logs/<tag>.log    Captured stdout+stderr from the container build.
///   └── tmp/                    Scratch dir for in-progress operations.
///
/// The built container image itself lives in Podman's storage
/// (~/.local/share/containers/storage/ by default) and is referenced by tag.
/// </summary>
public static class ArtifactPaths
{
    /// <summary>The compressmkv data root. Created on demand.</summary>
    public static string Root
    {
        get
        {
            // Honor XDG_DATA_HOME if set, otherwise default to ~/.local/share.
            string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
            string baseDir = !string.IsNullOrWhiteSpace(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(baseDir, "compressmkv");
        }
    }

    public static string StateFile => Path.Combine(Root, "state.json");
    public static string VmafSourceRoot => Path.Combine(Root, "vmaf");
    public static string BuildLogsDir => Path.Combine(Root, "build-logs");
    public static string TempDir => Path.Combine(Root, "tmp");

    /// <summary>Directory where vmaf source for a given tag lives.</summary>
    public static string VmafSourceFor(string tag) => Path.Combine(VmafSourceRoot, SanitizeTag(tag));

    /// <summary>Per-build log file path.</summary>
    public static string BuildLogFor(string tag) =>
        Path.Combine(BuildLogsDir, $"{SanitizeTag(tag)}.log");

    /// <summary>
    /// Container image tag we apply to the runtime ffmpeg image.  Includes
    /// the upstream vmaf tag so multiple builds can coexist.
    /// </summary>
    public static string ImageTagFor(string tag) =>
        $"localhost/compressmkv-ffmpeg-vmaf-cuda:{SanitizeTag(tag)}";

    /// <summary>
    /// Tag for the underlying VMAF base image (Dockerfile).  The runtime
    /// image (Dockerfile.ffmpeg) is built FROM this — without it, the
    /// upstream Dockerfile.ffmpeg won't resolve its FROM line.
    /// </summary>
    public static string BaseImageTagFor(string tag) =>
        $"localhost/compressmkv-vmaf-base:{SanitizeTag(tag)}";

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(VmafSourceRoot);
        Directory.CreateDirectory(BuildLogsDir);
        Directory.CreateDirectory(TempDir);
    }

    // tags like "v3.0.0" are fine as-is; sanitize defensively in case
    // upstream ever ships a tag with weird characters.
    private static string SanitizeTag(string tag)
    {
        var sb = new System.Text.StringBuilder(tag.Length);
        foreach (var ch in tag)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        return sb.ToString();
    }
}
