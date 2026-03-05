using System.Text.Json.Serialization;

namespace CompressMkv;

public sealed class FfprobeFormat
{
    [JsonPropertyName("duration")] public string? Duration { get; set; }
    [JsonIgnore] public double? DurationSeconds { get; set; }
}
