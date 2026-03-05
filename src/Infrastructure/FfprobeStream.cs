using System.Text.Json.Serialization;

namespace CompressMkv;

public sealed class FfprobeStream
{
    [JsonPropertyName("codec_type")] public string? CodecType { get; set; }
    [JsonPropertyName("codec_name")] public string? CodecName { get; set; }
    [JsonPropertyName("width")] public int? Width { get; set; }
    [JsonPropertyName("height")] public int? Height { get; set; }
    [JsonPropertyName("pix_fmt")] public string? PixFmt { get; set; }

    [JsonPropertyName("color_transfer")] public string? ColorTransfer { get; set; }
    [JsonPropertyName("color_primaries")] public string? ColorPrimaries { get; set; }
    [JsonPropertyName("color_space")] public string? ColorSpace { get; set; }

    // ffprobe may provide this; sometimes "unknown" even when interlaced.
    [JsonPropertyName("field_order")] public string? FieldOrder { get; set; }
}
