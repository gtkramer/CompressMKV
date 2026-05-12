using System.Globalization;
using System.Text.Json;

namespace MkvHelper;

/// <summary>
/// ffprobe wrapper — runs ffprobe and deserializes the JSON output.
/// </summary>
public static class Ffprobe
{
    public static async Task<FfprobeRoot> RunAsync(Config cfg, string input, CancellationToken ct)
    {
        string[] args = ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", input];
        (int code, string stdout, string stderr) = await ContainerTools.RunFfprobeAsync(args, ct);
        if (code != 0) throw new InvalidOperationException($"ffprobe failed: {stderr}");

        JsonSerializerOptions opt = new() { PropertyNameCaseInsensitive = true };
        FfprobeRoot root = JsonSerializer.Deserialize<FfprobeRoot>(stdout, opt) ?? new FfprobeRoot();

        if (root.Format?.Duration is { } d && double.TryParse(d, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs))
            root.Format.DurationSeconds = secs;

        return root;
    }
}
