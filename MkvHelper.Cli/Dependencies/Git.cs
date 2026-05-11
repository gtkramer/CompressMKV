namespace MkvHelper;

/// <summary>
/// Minimal git wrapper for the dependency-management subcommands.  We only
/// need shallow clone-at-tag here; nothing more.
/// </summary>
public static class Git
{
    public const string Exe = "git";

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
    /// Shallow-clone <paramref name="repoUrl"/> at <paramref name="tag"/> into
    /// <paramref name="destDir"/>.  --depth=1 keeps disk usage tiny — we never
    /// need history, only the worktree at that one tag.
    /// </summary>
    public static async Task ShallowCloneAtTagAsync(
        string repoUrl, string tag, string destDir, CancellationToken ct)
    {
        if (Directory.Exists(destDir))
            Directory.Delete(destDir, recursive: true);

        var args = new[]
        {
            "clone",
            "--depth", "1",
            "--branch", tag,
            "--single-branch",
            repoUrl,
            destDir
        };
        var (code, _, err) = await Proc.RunAsync(Exe, args, ct);
        if (code != 0)
            throw new InvalidOperationException(
                $"git clone {repoUrl} at {tag} failed: {err.Trim()}");
    }
}
