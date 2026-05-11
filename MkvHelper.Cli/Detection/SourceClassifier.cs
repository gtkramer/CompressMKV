using System.Globalization;
using System.Text.Json;

namespace MkvHelper;

public static class SourceClassifier
{
    public static bool IsHdr(FfprobeStream v)
    {
        if (v.ColorTransfer == null) return false;
        return v.ColorTransfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) ||
               v.ColorTransfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reads HDR side-data (Content-Light-Level + Mastering-Display) from the
    /// first video frame of the source.  ffprobe's stream-level output doesn't
    /// surface this metadata — it lives on individual frames — so we issue a
    /// second ffprobe call with `-read_intervals "%+#1" -show_frames` which
    /// reads exactly one frame's metadata at the start of the file.
    ///
    /// Returns null when the file has no HDR side-data, or when ffprobe fails.
    /// Callers should treat null as "no metadata available, use HDR10 default".
    /// </summary>
    public static async Task<HdrMetadata?> ExtractHdrMetadataAsync(
        Config cfg, string input, CancellationToken ct)
    {
        var args = new[]
        {
            "-v", "error",
            "-select_streams", "v:0",
            "-read_intervals", "%+#1",
            "-show_frames",
            "-print_format", "json",
            input
        };

        var (code, stdout, _) = await FfmpegRunner.RunFfprobeAsync(args, ct);
        if (code != 0 || string.IsNullOrEmpty(stdout)) return null;

        try
        {
            using var doc = JsonDocument.Parse(stdout);
            if (!doc.RootElement.TryGetProperty("frames", out var frames) ||
                frames.GetArrayLength() == 0)
                return null;

            var hdr = new HdrMetadata();
            bool any = false;

            foreach (var frame in frames.EnumerateArray())
            {
                if (!frame.TryGetProperty("side_data_list", out var list)) continue;

                foreach (var sd in list.EnumerateArray())
                {
                    if (!sd.TryGetProperty("side_data_type", out var typeEl)) continue;
                    var sdType = typeEl.GetString();

                    if (sdType == "Content light level metadata")
                    {
                        if (TryGetInt(sd, "max_content", out var mc))   { hdr.MaxCll  = mc;  any = true; }
                        if (TryGetInt(sd, "max_average", out var ma))   { hdr.MaxFall = ma;  any = true; }
                    }
                    else if (sdType == "Mastering display metadata")
                    {
                        // max_luminance in ffprobe is typically a rational like
                        // "10000000/10000" (= 1000 nits) — handle both rational
                        // strings and bare numerics.
                        if (sd.TryGetProperty("max_luminance", out var ml) &&
                            TryParseLuminance(ml, out var lumNits))
                        {
                            hdr.MasteringDisplayMaxLuminance = (int)Math.Round(lumNits);
                            any = true;
                        }
                    }
                }
            }

            return any ? hdr : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetInt(JsonElement parent, string name, out int value)
    {
        value = 0;
        if (!parent.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind == JsonValueKind.Number) return el.TryGetInt32(out value);
        if (el.ValueKind == JsonValueKind.String)
            return int.TryParse(el.GetString(), NumberStyles.Integer,
                                CultureInfo.InvariantCulture, out value);
        return false;
    }

    private static bool TryParseLuminance(JsonElement el, out double nits)
    {
        nits = 0;

        if (el.ValueKind == JsonValueKind.Number)
            return el.TryGetDouble(out nits);

        if (el.ValueKind == JsonValueKind.String)
        {
            var s = el.GetString();
            if (string.IsNullOrEmpty(s)) return false;

            // Rational form "num/den" — common for mastering display fields.
            var parts = s.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                d != 0)
            {
                nits = n / d;
                return true;
            }

            // Plain decimal form.
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out nits))
                return true;
        }

        return false;
    }
}
