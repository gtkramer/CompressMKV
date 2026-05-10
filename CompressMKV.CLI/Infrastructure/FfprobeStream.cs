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

    // Frame rate strings.  r_frame_rate is the lowest common multiple of frame
    // rates seen (the "nominal" rate for CFR sources, the highest seen for VFR);
    // avg_frame_rate is the mean across the stream.  For CFR content the two
    // values agree closely; for VFR they diverge.
    [JsonPropertyName("r_frame_rate")] public string? RFrameRate { get; set; }
    [JsonPropertyName("avg_frame_rate")] public string? AvgFrameRate { get; set; }

    /// <summary>
    /// Returns the stream's frame rate as an Fps fraction, parsing r_frame_rate
    /// (the LCD/nominal rate) and falling back to avg_frame_rate.  Null when
    /// neither parses to a positive rate.
    /// </summary>
    public Fps? ResolveFps()
    {
        if (Fps.TryParse(RFrameRate, out var r)) return r;
        if (Fps.TryParse(AvgFrameRate, out var a)) return a;
        return null;
    }

    /// <summary>
    /// Maximum r_frame_rate / avg_frame_rate ratio we treat as CFR.  Above this,
    /// the stream is variable-frame-rate (VFR): r_frame_rate (LCD of timestamps)
    /// has inflated toward the highest rate seen while avg_frame_rate stays near
    /// the typical rate.  Real-world VFR sources (screen recordings, mobile
    /// captures) run at 1.5–2.0+; real CFR sources sit at 1.000–1.001, so 1.05
    /// is comfortably between them.
    /// </summary>
    private const double VfrDetectionRatio = 1.05;

    /// <summary>
    /// Heuristic CFR check: compares r_frame_rate (the LCD across all timestamps
    /// seen) to avg_frame_rate (the mean).  For genuine CFR content the two
    /// agree within rounding noise; for VFR sources r_frame_rate inflates toward
    /// the highest rate seen while avg_frame_rate stays near the typical rate.
    ///
    /// Returns true when the stream looks CFR or when either rate is missing
    /// (assume CFR rather than block on uncertain metadata).
    /// </summary>
    public bool IsLikelyCfr()
    {
        if (!Fps.TryParse(RFrameRate, out var r)) return true;
        if (!Fps.TryParse(AvgFrameRate, out var a)) return true;
        if (a.AsDouble <= 0) return true;
        return r.AsDouble / a.AsDouble < VfrDetectionRatio;
    }
}
