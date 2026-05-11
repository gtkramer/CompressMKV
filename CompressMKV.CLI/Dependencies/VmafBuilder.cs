namespace CompressMkv;

/// <summary>
/// Orchestrates the full Netflix/vmaf → CUDA-enabled FFmpeg container build.
///
/// Netflix ships a two-stage build:
///   1. <c>Dockerfile</c>          → libvmaf + CUDA, tagged <c>vmaf</c>
///   2. <c>Dockerfile.ffmpeg</c>   → ffmpeg with libvmaf_cuda, <c>FROM vmaf</c>
///
/// The second stage is hard-coded to <c>FROM vmaf</c>, so we have to make
/// that tag exist at build time.  Approach:
///   - Build base as <c>localhost/compressmkv-vmaf-base:&lt;tag&gt;</c>
///   - Alias it as <c>vmaf</c> just for the duration of the second build
///   - Build runtime as <c>localhost/compressmkv-ffmpeg-vmaf-cuda:&lt;tag&gt;</c>
///   - Drop the <c>vmaf</c> alias so we don't squat on a generic name
///
/// Both base and runtime images are tracked in <see cref="BuildState"/>
/// so `dependency remove` can clean them up later.
/// </summary>
public static class VmafBuilder
{
    private const string RepoUrl = "https://github.com/Netflix/vmaf.git";
    private const string BaseDockerfile = "Dockerfile";
    private const string RuntimeDockerfile = "Dockerfile.ffmpeg";
    private const string FromAlias = "vmaf";

    public static async Task<BuildState> BuildAsync(
        string? tag, Action<string> onProgress, CancellationToken ct)
    {
        ArtifactPaths.EnsureDirectories();

        if (!await Git.IsAvailableAsync(ct))
            throw new InvalidOperationException("git was not found on PATH.");
        if (!await Podman.IsAvailableAsync(ct))
            throw new InvalidOperationException("podman was not found on PATH.");
        if (!Podman.HasNvidiaCdi())
            onProgress(
                "warning: /etc/cdi/nvidia.yaml not found.  Run " +
                "`sudo nvidia-ctk cdi generate --output=/etc/cdi/nvidia.yaml` " +
                "before using the container, or container GPU access will fail.");

        // Resolve tag.
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

        string runtimeImage = ArtifactPaths.ImageTagFor(resolvedTag);
        string baseImage = ArtifactPaths.BaseImageTagFor(resolvedTag);
        string sourceDir = ArtifactPaths.VmafSourceFor(resolvedTag);
        string buildLog = ArtifactPaths.BuildLogFor(resolvedTag);

        onProgress($"Shallow-cloning Netflix/vmaf at {resolvedTag} into {sourceDir}...");
        await Git.ShallowCloneAtTagAsync(RepoUrl, resolvedTag, sourceDir, ct);

        // Both Dockerfiles must be present at the repo root for the standard
        // Netflix workflow (we follow resource/doc/docker.md).
        string baseDockerfile = Path.Combine(sourceDir, BaseDockerfile);
        string runtimeDockerfile = Path.Combine(sourceDir, RuntimeDockerfile);
        if (!File.Exists(baseDockerfile))
            throw new InvalidOperationException($"Missing {BaseDockerfile} at {baseDockerfile} (Netflix/vmaf layout changed?).");
        if (!File.Exists(runtimeDockerfile))
            throw new InvalidOperationException($"Missing {RuntimeDockerfile} at {runtimeDockerfile} (Netflix/vmaf layout changed?).");

        // Stage 1: base image.
        onProgress($"Stage 1/2: building VMAF base image {baseImage} (this can take 5–10 min)...");
        await Podman.BuildAsync(
            contextDir: sourceDir,
            dockerfile: baseDockerfile,
            imageTag: baseImage,
            buildLogPath: buildLog,
            onLine: onProgress,
            ct: ct);

        // Alias for stage 2's `FROM vmaf`.  Wrapped in try/finally so we always
        // drop the alias, even on build failure — leaving `vmaf` tagged would
        // be confusing if the user has unrelated vmaf images.
        onProgress($"Aliasing {baseImage} as `{FromAlias}` for Dockerfile.ffmpeg's FROM line...");
        await Podman.TagAsync(baseImage, FromAlias, ct);

        try
        {
            onProgress($"Stage 2/2: building runtime image {runtimeImage} (this can take 5–10 min)...");
            await Podman.BuildAsync(
                contextDir: sourceDir,
                dockerfile: runtimeDockerfile,
                imageTag: runtimeImage,
                buildLogPath: buildLog + ".ffmpeg",
                onLine: onProgress,
                ct: ct);
        }
        finally
        {
            try { await Podman.UntagAsync(FromAlias, ct); }
            catch { /* best-effort */ }
        }

        onProgress($"Build succeeded.  Runtime image: {runtimeImage}");

        var prior = await BuildState.LoadAsync(ct);
        var newState = new BuildState
        {
            UpstreamTag = resolvedTag,
            ImageTag = runtimeImage,
            BaseImageTag = baseImage,
            BuiltUtc = DateTime.UtcNow,
            SourcePath = sourceDir,
            History = prior?.History ?? new List<HistoricBuild>(),
        };

        if (prior is not null && !string.IsNullOrEmpty(prior.ImageTag) && prior.ImageTag != runtimeImage)
        {
            newState.History.Add(new HistoricBuild
            {
                UpstreamTag = prior.UpstreamTag,
                ImageTag = prior.ImageTag,
                BaseImageTag = prior.BaseImageTag,
                SourcePath = prior.SourcePath,
                BuiltUtc = prior.BuiltUtc,
            });
        }
        await newState.SaveAsync(ct);

        return newState;
    }

    /// <summary>
    /// Removes everything ever produced by `dependency build`: every base and
    /// runtime image, source clones, build logs, and the state file.
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
        var sources = new List<string>();

        if (!string.IsNullOrEmpty(state.ImageTag)) images.Add(state.ImageTag);
        if (!string.IsNullOrEmpty(state.BaseImageTag)) images.Add(state.BaseImageTag);
        if (!string.IsNullOrEmpty(state.SourcePath)) sources.Add(state.SourcePath);

        foreach (var h in state.History)
        {
            if (!string.IsNullOrEmpty(h.ImageTag)) images.Add(h.ImageTag);
            if (!string.IsNullOrEmpty(h.BaseImageTag)) images.Add(h.BaseImageTag);
            if (!string.IsNullOrEmpty(h.SourcePath)) sources.Add(h.SourcePath);
        }

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

        foreach (var src in sources.Distinct())
        {
            if (Directory.Exists(src))
            {
                onProgress($"Removing source clone {src}...");
                Directory.Delete(src, recursive: true);
            }
        }

        if (Directory.Exists(ArtifactPaths.BuildLogsDir))
        {
            onProgress($"Removing build logs {ArtifactPaths.BuildLogsDir}...");
            Directory.Delete(ArtifactPaths.BuildLogsDir, recursive: true);
        }

        BuildState.Delete();
        onProgress("All compressmkv build artifacts removed.");
    }
}
