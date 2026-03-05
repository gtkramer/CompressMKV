using System.Globalization;
using System.Text.Json;

namespace CompressMkv;

/// <summary>
/// ffprobe wrapper — runs ffprobe and deserializes the JSON output.
/// </summary>
public static class Ffprobe
{
    public static async Task<FfprobeRoot> RunAsync(Config cfg, string input, CancellationToken ct)
    {
        var args = new[] { "-v","error","-print_format","json","-show_format","-show_streams", input };
        var (code, stdout, stderr) = await Proc.RunAsync(cfg.Ffprobe, args, ct);
        if (code != 0) throw new InvalidOperationException($"ffprobe failed: {stderr}");

        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var root = JsonSerializer.Deserialize<FfprobeRoot>(stdout, opt) ?? new FfprobeRoot();

        if (root.Format?.Duration is { } d && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
            root.Format.DurationSeconds = secs;

        return root;
    }
}
