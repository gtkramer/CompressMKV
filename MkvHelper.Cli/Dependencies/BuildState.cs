using System.Text.Json;
using System.Text.Json.Serialization;

namespace MkvHelper;

/// <summary>
/// On-disk record of the current container build.  Lives at
/// <see cref="ArtifactPaths.StateFile"/> and is checked on every command
/// startup to decide whether to rebuild (Containerfile changed) or reuse
/// (image still present and up-to-date).
/// </summary>
public sealed class BuildState
{
    /// <summary>Podman image tag (always <see cref="ContainerBuilder.ImageTag"/>).</summary>
    public required string ImageTag { get; init; }

    /// <summary>SHA-256 of the embedded Containerfile content at build time.
    /// Used by <see cref="ContainerBuilder.EnsureReadyAsync"/> to detect when
    /// the source has changed and the cached image needs a rebuild.</summary>
    public required string ContainerfileSha256 { get; init; }

    public required DateTime BuiltUtc { get; init; }

    private static readonly JsonSerializerOptions s_jsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static async Task<BuildState?> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(ArtifactPaths.StateFile)) return null;
        try
        {
            await using FileStream fs = File.OpenRead(ArtifactPaths.StateFile);
            return await JsonSerializer.DeserializeAsync<BuildState>(fs, s_jsonOpts, ct);
        }
        catch
        {
            return null;  // corrupt or schema-incompatible — treat as unbuilt
        }
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        ArtifactPaths.EnsureDirectories();
        await using FileStream fs = File.Create(ArtifactPaths.StateFile);
        await JsonSerializer.SerializeAsync(fs, this, s_jsonOpts, ct);
    }

    public static void Delete()
    {
        if (File.Exists(ArtifactPaths.StateFile))
            File.Delete(ArtifactPaths.StateFile);
    }
}
