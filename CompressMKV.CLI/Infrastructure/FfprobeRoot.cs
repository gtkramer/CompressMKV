using System.Text.Json.Serialization;

namespace CompressMkv;

public sealed class FfprobeRoot
{
    [JsonPropertyName("format")] public FfprobeFormat? Format { get; set; }
    [JsonPropertyName("streams")] public List<FfprobeStream>? Streams { get; set; }

    /// <summary>
    /// True when this file has at least one real video stream (not cover art)
    /// AND a non-trivial duration (&gt; 1 second) — i.e. it's worth feeding
    /// into the compression pipeline.  Filters out:
    ///   • pure audio files (no video stream)
    ///   • audio with embedded cover art (video stream is attached_pic)
    ///   • single-image files (.jpg/.png — duration ≈ 0)
    ///   • subtitle/data-only containers (no video stream)
    /// </summary>
    public bool HasUsableVideoContent() =>
        (Format?.DurationSeconds ?? 0) > 1.0
        && (Streams?.Any(s => s.IsActualVideo()) ?? false);
}
