namespace MkvHelper;

/// <summary>
/// Filesystem layout for mkvhelper's container build artifacts under
/// XDG_DATA_HOME (~/.local/share/mkvhelper/).
///
///   mkvhelper/
///   ├── state.json              Build metadata (vmaf tag, image tag, built-at).
///   └── build-logs/&lt;tag&gt;.log    Captured stdout+stderr from the container build.
///
/// The container image itself lives in Podman's storage
/// (~/.local/share/containers/storage/ by default) and is referenced by tag.
/// Build context for the image is empty — the Containerfile clones VMAF and
/// FFmpeg from inside RUN steps — so we no longer keep a source clone on disk.
/// </summary>
public static class ArtifactPaths
{
    /// <summary>The mkvhelper data root. Created on demand.</summary>
    public static string Root
    {
        get
        {
            // Honor XDG_DATA_HOME if set, otherwise default to ~/.local/share.
            string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
            string baseDir = !string.IsNullOrWhiteSpace(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(baseDir, "mkvhelper");
        }
    }

    public static string StateFile => Path.Combine(Root, "state.json");
    public static string BuildLogsDir => Path.Combine(Root, "build-logs");

    /// <summary>Per-build log file path.</summary>
    public static string BuildLogFor(string tag) =>
        Path.Combine(BuildLogsDir, $"{SanitizeTag(tag)}.log");

    /// <summary>
    /// Container image tag.  The image bundles libvmaf+CUDA, FFmpeg with
    /// the full codec set, and MKVToolNix — all of mkvhelper's external
    /// dependencies in one place.  Tagged with the VMAF release version
    /// so multiple builds can coexist (and `dependency remove` knows what
    /// to clean up).
    /// </summary>
    public static string ImageTagFor(string tag) =>
        $"localhost/mkvhelper:{SanitizeTag(tag)}";

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(BuildLogsDir);
    }

    // Tags like "v3.1.0" are fine as-is; sanitize defensively in case
    // upstream ever ships a tag with weird characters.
    private static string SanitizeTag(string tag)
    {
        var sb = new System.Text.StringBuilder(tag.Length);
        foreach (var ch in tag)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        return sb.ToString();
    }
}
