using System.Text.Json;
using System.Text.Json.Serialization;

namespace MkvHelper;

/// <summary>
/// On-disk record of the current container build.  Lives at
/// <see cref="ArtifactPaths.StateFile"/> and is read on every command
/// startup to find the right image tag.
/// </summary>
public sealed class BuildState
{
    /// <summary>Upstream Netflix/vmaf tag the build was made from (e.g. "v3.1.0").</summary>
    public string UpstreamTag { get; set; } = "";

    /// <summary>Podman image tag (e.g. "localhost/mkvhelper:v3.1.0").</summary>
    public string ImageTag { get; set; } = "";

    public DateTime BuiltUtc { get; set; }

    /// <summary>
    /// Records of any prior builds whose image may still be in podman
    /// storage.  `dependency remove` walks this list when cleaning up.
    /// </summary>
    public List<HistoricBuild> History { get; set; } = new();

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
            await using var fs = File.OpenRead(ArtifactPaths.StateFile);
            return await JsonSerializer.DeserializeAsync<BuildState>(fs, s_jsonOpts, ct);
        }
        catch
        {
            return null;  // corrupt state — treat as unbuilt
        }
    }

    public async Task SaveAsync(CancellationToken ct)
    {
        ArtifactPaths.EnsureDirectories();
        await using var fs = File.Create(ArtifactPaths.StateFile);
        await JsonSerializer.SerializeAsync(fs, this, s_jsonOpts, ct);
    }

    public static void Delete()
    {
        if (File.Exists(ArtifactPaths.StateFile))
            File.Delete(ArtifactPaths.StateFile);
    }
}

public sealed class HistoricBuild
{
    public string UpstreamTag { get; set; } = "";
    public string ImageTag { get; set; } = "";
    public DateTime BuiltUtc { get; set; }
}
