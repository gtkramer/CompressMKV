using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Spectre.Console;

namespace MkvHelper;

/// <summary>
/// Builds the mkvhelper dependency container from our embedded
/// <c>Containerfile</c>.  The Containerfile is the single source of truth
/// for all the external tooling the app needs (libvmaf+CUDA, FFmpeg with
/// the full codec set, MKVToolNix); building it produces one image that
/// every other subcommand routes through via <see cref="ContainerTools"/>.
///
/// Build context is empty — the Containerfile clones VMAF and FFmpeg from
/// inside RUN steps — so we don't need a source tree on disk.  The image
/// always uses the fixed tag <see cref="ImageTag"/>; rebuilding replaces
/// the previous image (the old layers become orphaned and can be reclaimed
/// with <c>podman image prune</c>).
/// </summary>
public static class ContainerBuilder
{
    /// <summary>Fixed image tag.  Same name across builds — each build replaces
    /// the previous image, mirroring the "Containerfile is source of truth"
    /// model.</summary>
    public const string ImageTag = "localhost/mkvhelper:current";

    private const string ContainerfileResource = "MkvHelper.Dependencies.Containerfile";

    public static async Task<BuildState> BuildAsync(
        bool noCache, Action<string> onProgress, CancellationToken ct)
    {
        ArtifactPaths.EnsureDirectories();

        if (!await Podman.IsAvailableAsync(ct))
            throw new InvalidOperationException("podman was not found on PATH.");
        if (!Podman.HasNvidiaCdi())
            onProgress(
                "warning: /etc/cdi/nvidia.yaml not found.  Run " +
                "`sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml` " +
                "before using the container, or container GPU access will fail.");

        // Materialise the embedded Containerfile to a fresh temp dir so
        // the build context is guaranteed empty — no stale files leak in
        // from a prior run.
        string containerfileContent = LoadContainerfile();
        string contextDir = Directory.CreateTempSubdirectory("mkvhelper-build-").FullName;
        string containerfilePath = Path.Combine(contextDir, "Containerfile");
        await File.WriteAllTextAsync(containerfilePath, containerfileContent, ct);

        try
        {
            string cacheNote = noCache ? " (--no-cache: ignoring layer cache)" : "";
            onProgress($"Building {ImageTag} from embedded Containerfile{cacheNote}.  This can take 10–20 min on the first build.");

            await Podman.BuildAsync(
                contextDir: contextDir,
                containerfile: containerfilePath,
                imageTag: ImageTag,
                buildLogPath: ArtifactPaths.BuildLog,
                onLine: onProgress,
                ct: ct,
                noCache: noCache);
        }
        finally
        {
            try { Directory.Delete(contextDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        onProgress($"Build succeeded.  Image: {ImageTag}");

        BuildState state = new()
        {
            ImageTag = ImageTag,
            ContainerfileSha256 = Sha256(containerfileContent),
            BuiltUtc = DateTime.UtcNow,
        };
        await state.SaveAsync(ct);
        return state;
    }

    /// <summary>
    /// Shared startup hook for any command that needs to invoke tools
    /// from the container.  Runs the build automatically on first use,
    /// or when the embedded Containerfile has changed since the last
    /// build (sha256 mismatch), or when the cached image is gone from
    /// podman storage.  Configures <see cref="ContainerTools"/> with the
    /// given host-directory mounts and returns the resolved
    /// <see cref="BuildState"/>.
    /// </summary>
    public static async Task<BuildState> EnsureReadyAsync(
        IEnumerable<string> mounts, CancellationToken ct)
    {
        if (!await Podman.IsAvailableAsync(ct))
            throw new InvalidOperationException(
                "podman is not available on PATH.  Install podman + the NVIDIA " +
                "Container Toolkit; see the README's first-run setup section.");

        BuildState? state = await BuildState.LoadAsync(ct);
        string currentHash = Sha256(LoadContainerfile());

        // Decide whether to rebuild.  Three triggers, all surfaced to the
        // user before a long auto-build kicks off so they understand what's
        // about to happen.
        string? rebuildReason = null;
        if (state is null)
            rebuildReason = "no prior build was found";
        else if (!await Podman.ImageExistsAsync(state.ImageTag, ct))
            rebuildReason = $"image {state.ImageTag} is missing from podman storage";
        else if (!string.IsNullOrEmpty(state.ContainerfileSha256)
                 && state.ContainerfileSha256 != currentHash)
            rebuildReason = "the embedded Containerfile has changed since the last build";

        if (rebuildReason is not null)
            state = await AutoBuildAsync(rebuildReason, ct);

        if (state is null)
            throw new InvalidOperationException(
                "ContainerBuilder: build state was null after readiness resolution — internal bug.");

        ContainerTools.Configure(state.ImageTag, mounts);
        return state;
    }

    /// <summary>
    /// First-run / stale-image recovery: print a one-paragraph
    /// explanation of what's about to happen, then run the build with a
    /// live spinner showing the latest podman line.
    /// </summary>
    private static async Task<BuildState> AutoBuildAsync(string reason, CancellationToken ct)
    {
        AnsiConsole.MarkupLine($"[bold]Auto-building mkvhelper container[/] — {Markup.Escape(reason)}.");
        AnsiConsole.MarkupLine(
            "[grey]The container bundles every external tool mkvhelper shells out to " +
            "(CUDA-enabled FFmpeg, libvmaf_cuda, MKVToolNix) so the host doesn't have " +
            "to install them separately.  First-time build typically takes 10–20 minutes; " +
            "subsequent runs reuse the image until the Containerfile changes.[/]");

        BuildState? result = null;
        await AnsiConsole.Status()
            .AutoRefresh(true)
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Preparing build...", async ctx =>
            {
                result = await BuildAsync(
                    noCache: false,
                    onProgress: line =>
                    {
                        string trimmed = line.Length > 100 ? line[..97] + "..." : line;
                        ctx.Status(Markup.Escape(trimmed));
                    },
                    ct: ct);
            });
        return result
            ?? throw new InvalidOperationException(
                "Auto-build completed but produced no BuildState — internal bug.");
    }

    /// <summary>
    /// Snapshot of the container's readiness, exactly as
    /// <see cref="EnsureReadyAsync"/> would judge it on next invocation.
    /// Computed without modifying anything — safe to call repeatedly.
    /// </summary>
    public sealed record ContainerStatus(
        string ImageTag,
        bool ImageExists,
        BuildState? State,
        string CurrentContainerfileSha,
        ContainerReadiness Readiness)
    {
        public bool ShaMatches =>
            State is not null
            && !string.IsNullOrEmpty(State.ContainerfileSha256)
            && string.Equals(State.ContainerfileSha256, CurrentContainerfileSha, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolution of <see cref="ContainerStatus"/> into a single bucket —
    /// the four mutually-exclusive answers to "what would happen on the
    /// next subcommand that needs the container?"
    /// </summary>
    public enum ContainerReadiness
    {
        /// <summary>Image exists, SHA matches: next subcommand uses it as-is.</summary>
        Ready,
        /// <summary>Image exists but the embedded Containerfile has changed: next subcommand auto-rebuilds.</summary>
        Stale,
        /// <summary>state.json exists but the image is gone from podman storage: next subcommand auto-rebuilds.</summary>
        ImageMissing,
        /// <summary>No prior build at all: next subcommand auto-builds from scratch.</summary>
        NotBuilt,
    }

    /// <summary>
    /// Read-only assessment of the container's current state versus the
    /// embedded Containerfile.  Doesn't touch podman storage beyond the
    /// existence check, doesn't trigger any builds.
    /// </summary>
    public static async Task<ContainerStatus> GetStatusAsync(CancellationToken ct)
    {
        string currentSha = Sha256(LoadContainerfile());
        BuildState? state = await BuildState.LoadAsync(ct);

        bool imageExists = false;
        if (await Podman.IsAvailableAsync(ct))
            imageExists = await Podman.ImageExistsAsync(ImageTag, ct);

        ContainerReadiness readiness = (state, imageExists) switch
        {
            (null, _) => ContainerReadiness.NotBuilt,
            (_, false) => ContainerReadiness.ImageMissing,
            (not null, true) when string.Equals(state.ContainerfileSha256, currentSha, StringComparison.OrdinalIgnoreCase)
                => ContainerReadiness.Ready,
            _ => ContainerReadiness.Stale,
        };

        return new ContainerStatus(
            ImageTag: ImageTag,
            ImageExists: imageExists,
            State: state,
            CurrentContainerfileSha: currentSha,
            Readiness: readiness);
    }

    /// <summary>
    /// Removes the built container image, the build log, and the state
    /// file.  Untagged intermediate layers from the build (~10 GB worth)
    /// are NOT removed automatically — point the user at
    /// <c>podman image prune -f</c> for that.
    /// </summary>
    public static async Task RemoveAllAsync(Action<string> onProgress, CancellationToken ct)
    {
        if (await Podman.IsAvailableAsync(ct))
        {
            if (await Podman.ImageExistsAsync(ImageTag, ct))
            {
                onProgress($"Removing podman image {ImageTag}...");
                await Podman.RemoveImageAsync(ImageTag, ct);
            }
            else
            {
                onProgress($"Image {ImageTag} not present in podman storage — nothing to remove there.");
            }
        }
        else
        {
            onProgress("podman not on PATH — skipping image removal (state file will still be cleared).");
        }

        if (File.Exists(ArtifactPaths.BuildLog))
        {
            File.Delete(ArtifactPaths.BuildLog);
            onProgress($"Removed build log {ArtifactPaths.BuildLog}.");
        }

        BuildState.Delete();
        onProgress(
            "All mkvhelper container artifacts removed.  Untagged intermediate " +
            "layers (~10 GB) can be reclaimed with `podman image prune -f`.");
    }

    /// <summary>
    /// Read the Containerfile out of the assembly's embedded resources.
    /// Throws if missing — that's a build-system bug, not a runtime
    /// concern.
    /// </summary>
    private static string LoadContainerfile()
    {
        Assembly asm = typeof(ContainerBuilder).Assembly;
        using Stream stream = asm.GetManifestResourceStream(ContainerfileResource)
            ?? throw new InvalidOperationException(
                $"Embedded Containerfile resource `{ContainerfileResource}` was not found.  " +
                "Check the <EmbeddedResource> entry in MkvHelper.Cli.csproj.");
        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string Sha256(string content)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
