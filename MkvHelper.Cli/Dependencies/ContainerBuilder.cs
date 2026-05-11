using System.Reflection;
using Spectre.Console;

namespace MkvHelper;

/// <summary>
/// Builds the mkvhelper dependency container from our embedded
/// <c>Containerfile</c>.  The Containerfile is the single source of truth
/// for all the external tooling the app needs (libvmaf+CUDA, FFmpeg with
/// the full codec set, MKVToolNix); building it produces one image that
/// every other subcommand routes through via <see cref="ContainerTools"/>.
///
/// The build context is empty — the Containerfile clones VMAF and FFmpeg
/// from inside RUN steps — so we don't need a source tree on disk.
/// </summary>
public static class ContainerBuilder
{
    private const string ContainerfileResource = "MkvHelper.Dependencies.Containerfile";

    public static async Task<BuildState> BuildAsync(
        string? tag, Action<string> onProgress, CancellationToken ct)
    {
        ArtifactPaths.EnsureDirectories();

        if (!await Podman.IsAvailableAsync(ct))
            throw new InvalidOperationException("podman was not found on PATH.");
        if (!Podman.HasNvidiaCdi())
            onProgress(
                "warning: /etc/cdi/nvidia.yaml not found.  Run " +
                "`sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml` " +
                "before using the container, or container GPU access will fail.");

        // Resolve VMAF tag — used both as a build arg (so the container
        // builds VMAF at that tag) and as the image's own tag (so multiple
        // VMAF versions can coexist in podman storage).
        string resolvedTag;
        if (string.IsNullOrWhiteSpace(tag))
        {
            onProgress("Querying Netflix/vmaf for the latest release tag...");
            var latest = await ReleaseFetcher.GetLatestAsync(ct);
            resolvedTag = latest.Tag;
            onProgress($"Latest release: {latest.Tag} ({latest.Name}), published {latest.PublishedUtc:yyyy-MM-dd}.");
        }
        else
        {
            resolvedTag = tag;
        }

        string imageTag = ArtifactPaths.ImageTagFor(resolvedTag);
        string buildLog = ArtifactPaths.BuildLogFor(resolvedTag);

        // Materialise the embedded Containerfile to a temp path; podman
        // reads it via -f.  Build context is the empty parent dir.
        string contextDir = Directory.CreateTempSubdirectory("mkvhelper-build-").FullName;
        string containerfilePath = Path.Combine(contextDir, "Containerfile");
        await File.WriteAllTextAsync(containerfilePath, LoadContainerfile(), ct);

        try
        {
            onProgress($"Building container image {imageTag} from embedded Containerfile (this can take 10–20 min on first run)...");
            await Podman.BuildAsync(
                contextDir: contextDir,
                dockerfile: containerfilePath,
                imageTag: imageTag,
                buildLogPath: buildLog,
                onLine: onProgress,
                ct: ct,
                buildArgs: new Dictionary<string, string>
                {
                    ["VMAF_TAG"] = resolvedTag,
                });
        }
        finally
        {
            try { Directory.Delete(contextDir, recursive: true); }
            catch { /* best-effort cleanup */ }
        }

        onProgress($"Build succeeded.  Image: {imageTag}");

        var prior = await BuildState.LoadAsync(ct);
        var newState = new BuildState
        {
            UpstreamTag = resolvedTag,
            ImageTag = imageTag,
            BuiltUtc = DateTime.UtcNow,
            History = prior?.History ?? new List<HistoricBuild>(),
        };
        if (prior is not null && !string.IsNullOrEmpty(prior.ImageTag) && prior.ImageTag != imageTag)
        {
            newState.History.Add(new HistoricBuild
            {
                UpstreamTag = prior.UpstreamTag,
                ImageTag = prior.ImageTag,
                BuiltUtc = prior.BuiltUtc,
            });
        }
        await newState.SaveAsync(ct);

        return newState;
    }

    /// <summary>
    /// Shared startup hook for any command that needs to invoke tools
    /// from the container.  Resolves the build (auto-building on first
    /// use), configures <see cref="ContainerTools"/> with the requested
    /// host-directory mounts, and returns the resolved <see cref="BuildState"/>
    /// so the caller can log it.  Throws if podman isn't available or
    /// the auto-build fails.
    /// </summary>
    public static async Task<BuildState> EnsureReadyAsync(
        IEnumerable<string> mounts, CancellationToken ct)
    {
        if (!await Podman.IsAvailableAsync(ct))
            throw new InvalidOperationException(
                "podman is not available on PATH.  Install podman + the NVIDIA " +
                "Container Toolkit; see the README's first-run setup section.");

        var state = await BuildState.LoadAsync(ct);

        // If state references an image that's gone (e.g. the user pruned
        // podman storage), force a rebuild.
        if (state is not null && !await Podman.ImageExistsAsync(state.ImageTag, ct))
        {
            AnsiConsole.MarkupLine(
                $"[yellow]Warning:[/] state references image " +
                $"[bold]{Markup.Escape(state.ImageTag)}[/] " +
                "but it's gone from podman storage.  Rebuilding...");
            state = null;
        }

        if (state is null)
            state = await AutoBuildWithExplanationAsync(ct);

        ContainerTools.Configure(state.ImageTag, mounts);
        return state;
    }

    /// <summary>
    /// First-run / orphaned-state recovery: print a one-paragraph
    /// explanation of what's about to happen, then run the build with a
    /// live spinner showing the latest podman line.  Used by
    /// <see cref="EnsureReadyAsync"/>.
    /// </summary>
    private static async Task<BuildState> AutoBuildWithExplanationAsync(CancellationToken ct)
    {
        AnsiConsole.MarkupLine("[bold]First-run dependency build[/]");
        AnsiConsole.MarkupLine(
            "[grey]No mkvhelper container was found.  mkvhelper bundles every " +
            "external tool it needs (CUDA-enabled FFmpeg, libvmaf_cuda, " +
            "MKVToolNix) into a single podman image so the host doesn't have " +
            "to install them separately.  First-time build typically takes " +
            "10–20 minutes; subsequent runs reuse the image.[/]");

        BuildState? result = null;
        await AnsiConsole.Status()
            .AutoRefresh(true)
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Preparing build...", async ctx =>
            {
                result = await BuildAsync(
                    tag: null,
                    onProgress: line =>
                    {
                        // Truncate so the spinner line stays readable.
                        string trimmed = line.Length > 100 ? line[..97] + "..." : line;
                        ctx.Status(Markup.Escape(trimmed));
                    },
                    ct: ct);
            });
        return result!;
    }

    /// <summary>
    /// Removes everything ever produced by `dependency build`: every
    /// container image we ever tagged, build logs, and the state file.
    /// </summary>
    public static async Task RemoveAllAsync(Action<string> onProgress, CancellationToken ct)
    {
        var state = await BuildState.LoadAsync(ct);
        if (state is null)
        {
            onProgress("No build state found — nothing to remove.");
            return;
        }

        var images = new List<string>();
        if (!string.IsNullOrEmpty(state.ImageTag)) images.Add(state.ImageTag);
        foreach (var h in state.History)
            if (!string.IsNullOrEmpty(h.ImageTag)) images.Add(h.ImageTag);

        if (await Podman.IsAvailableAsync(ct))
        {
            foreach (var img in images.Distinct())
            {
                if (await Podman.ImageExistsAsync(img, ct))
                {
                    onProgress($"Removing podman image {img}...");
                    await Podman.RemoveImageAsync(img, ct);
                }
            }
        }
        else
        {
            onProgress("podman not on PATH — skipping image removal (state file will still be cleared).");
        }

        if (Directory.Exists(ArtifactPaths.BuildLogsDir))
        {
            onProgress($"Removing build logs {ArtifactPaths.BuildLogsDir}...");
            Directory.Delete(ArtifactPaths.BuildLogsDir, recursive: true);
        }

        BuildState.Delete();
        onProgress(
            "All mkvhelper build artifacts removed.  Untagged intermediate " +
            "layers from the build (~10 GB) can be reclaimed with " +
            "`podman image prune -f`.");
    }

    /// <summary>
    /// Read the Containerfile out of the assembly's embedded resources.
    /// Throws if missing — that's a build-system bug, not a runtime concern.
    /// </summary>
    private static string LoadContainerfile()
    {
        var asm = typeof(ContainerBuilder).Assembly;
        using var stream = asm.GetManifestResourceStream(ContainerfileResource)
            ?? throw new InvalidOperationException(
                $"Embedded Containerfile resource `{ContainerfileResource}` was not found.  " +
                "Check the <EmbeddedResource> entry in MkvHelper.Cli.csproj.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
