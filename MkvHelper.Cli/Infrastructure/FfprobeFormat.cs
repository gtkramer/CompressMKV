using System.Text.Json.Serialization;

namespace MkvHelper;

public sealed class FfprobeFormat
{
    [JsonPropertyName("duration")] public string? Duration { get; set; }
    [JsonIgnore] public double? DurationSeconds { get; set; }

    /// <summary>Overall bit rate of the container, in bits/sec, as reported
    /// by ffprobe.  Null when ffprobe can't determine it.</summary>
    [JsonPropertyName("bit_rate")] public string? BitRate { get; set; }

    /// <summary>Total file size in bytes as reported by ffprobe.</summary>
    [JsonPropertyName("size")] public string? Size { get; set; }
}
