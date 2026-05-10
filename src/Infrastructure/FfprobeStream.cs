using System.Globalization;
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

    // Frame rate strings, expressed as "num/den" fractions.
    // r_frame_rate is the lowest common multiple of frame rates seen; avg is the
    // average over the stream.  Either is sufficient to identify NTSC family.
    [JsonPropertyName("r_frame_rate")] public string? RFrameRate { get; set; }
    [JsonPropertyName("avg_frame_rate")] public string? AvgFrameRate { get; set; }

    /// <summary>
    /// Returns the stream's frame rate in fps, parsing either r_frame_rate or
    /// avg_frame_rate (in that order).  Null when neither parses to a valid fraction.
    /// </summary>
    public double? ResolveFps()
    {
        if (TryParseFraction(RFrameRate, out var r) && r > 0) return r;
        if (TryParseFraction(AvgFrameRate, out var a) && a > 0) return a;
        return null;
    }

    /// <summary>
    /// Returns true when the source frame rate matches an NTSC family rate
    /// (30000/1001, 24000/1001, 60000/1001).  These all share the 1001 denominator
    /// inherited from black-and-white compatibility — see MPlayer guide §7.2.1.
    /// </summary>
    public bool IsNtscFamilyFps()
    {
        var fps = ResolveFps();
        if (fps is null) return false;
        return Near(fps.Value, 30000.0 / 1001.0)
            || Near(fps.Value, 24000.0 / 1001.0)
            || Near(fps.Value, 60000.0 / 1001.0);

        static bool Near(double a, double b) => Math.Abs(a - b) < 0.05;
    }

    private static bool TryParseFraction(string? s, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;

        var parts = s.Split('/');
        if (parts.Length == 1)
            return double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out value);

        if (parts.Length == 2 &&
            double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
            d != 0)
        {
            value = n / d;
            return true;
        }
        return false;
    }
}
