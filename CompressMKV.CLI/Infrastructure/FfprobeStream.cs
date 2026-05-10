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
    /// Inspects <see cref="PixFmt"/> to derive the source's component bit depth.
    /// 8 for yuv420p / yuv422p / yuv444p, 10 for *p10le / *p10be, 12 for *p12*,
    /// and 8 as a safe fallback when pix_fmt is missing or unrecognised.
    /// Used to pick a matched-bit-depth comparison format for VMAF so an 8-bit
    /// reference isn't artificially zero-padded to 10 bits before comparison
    /// against a genuine 10-bit encode.
    /// </summary>
    public int GetBitDepth()
    {
        if (string.IsNullOrEmpty(PixFmt)) return 8;
        if (PixFmt.Contains("12", StringComparison.Ordinal)) return 12;
        if (PixFmt.Contains("10", StringComparison.Ordinal)) return 10;
        return 8;
    }

    /// <summary>
    /// Returns the ffmpeg pix_fmt string to use as the matched comparison format
    /// for the VMAF SDR branch.  10/12-bit sources compare at <c>yuv420p10le</c>
    /// (libvmaf supports up to 10-bit; 12-bit gets dithered down).  8-bit sources
    /// compare at <c>yuv420p</c> — a 10-bit comparison would zero-pad the
    /// reference's LSBs and bias VMAF scores low.
    /// </summary>
    public string GetVmafCompareFormat() =>
        GetBitDepth() >= 10 ? "yuv420p10le" : "yuv420p";

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
