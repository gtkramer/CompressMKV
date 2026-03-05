using System.Text.Json;

namespace CompressMkv;

public static class Vmaf
{
    public static async Task<double> ParseMeanAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);

        if (doc.RootElement.TryGetProperty("pooled_metrics", out var pooled) &&
            pooled.TryGetProperty("vmaf", out var vmaf) &&
            vmaf.TryGetProperty("mean", out var meanEl) &&
            meanEl.TryGetDouble(out var mean))
            return mean;

        throw new InvalidOperationException("VMAF JSON missing pooled_metrics.vmaf.mean");
    }
}
