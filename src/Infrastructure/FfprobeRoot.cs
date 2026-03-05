using System.Text.Json.Serialization;

namespace CompressMkv;

public sealed class FfprobeRoot
{
    [JsonPropertyName("format")] public FfprobeFormat? Format { get; set; }
    [JsonPropertyName("streams")] public List<FfprobeStream>? Streams { get; set; }
}
