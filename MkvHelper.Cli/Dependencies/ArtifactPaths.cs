namespace MkvHelper;

/// <summary>
/// Filesystem layout for mkvhelper's container build artifacts under
/// XDG_DATA_HOME (~/.local/share/mkvhelper/).
///
///   mkvhelper/
///   ├── state.json   Build metadata (image tag, Containerfile hash, built-at).
///   └── build.log    Captured stdout+stderr from the most recent podman build.
///
/// The container image itself lives in Podman's storage
/// (~/.local/share/containers/storage/ by default) and is referenced by
/// the fixed tag <see cref="ContainerBuilder.ImageTag"/>.
/// </summary>
public static class ArtifactPaths
{
    /// <summary>The mkvhelper data root. Created on demand.</summary>
    public static string Root
    {
        get
        {
            string xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME") ?? "";
            string baseDir = !string.IsNullOrWhiteSpace(xdg)
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(baseDir, "mkvhelper");
        }
    }

    public static string StateFile => Path.Combine(Root, "state.json");

    /// <summary>Captured podman build output for the most recent build.
    /// Single rolling file rather than per-tag history because we now keep
    /// exactly one image at any time.</summary>
    public static string BuildLog => Path.Combine(Root, "build.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(Root);
    }
}
