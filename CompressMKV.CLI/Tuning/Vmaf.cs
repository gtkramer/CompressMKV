using System.Text.Json;

namespace CompressMkv;

/// <summary>
/// Parses VMAF JSON log files including per-frame scores for
/// robust percentile computation across all sample windows.
/// </summary>
public static class Vmaf
{
    /// <summary>
    /// Parses pooled metrics and every per-frame VMAF score from the JSON log.
    /// </summary>
    public static async Task<VmafResult> ParseAsync(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);

        // --- Per-frame scores (the frames[] array) ---
        var frameScores = new List<double>();
        if (doc.RootElement.TryGetProperty("frames", out var frames))
        {
            foreach (var frame in frames.EnumerateArray())
            {
                if (frame.TryGetProperty("metrics", out var metrics) &&
                    metrics.TryGetProperty("vmaf", out var vmafEl) &&
                    vmafEl.TryGetDouble(out var score))
                {
                    frameScores.Add(score);
                }
            }
        }

        // --- Pooled metrics ---
        double mean = 0, harmonicMean = 0, min = 0;
        if (doc.RootElement.TryGetProperty("pooled_metrics", out var pooled) &&
            pooled.TryGetProperty("vmaf", out var vmaf))
        {
            if (vmaf.TryGetProperty("mean", out var meanEl))
                meanEl.TryGetDouble(out mean);
            if (vmaf.TryGetProperty("harmonic_mean", out var hmEl))
                hmEl.TryGetDouble(out harmonicMean);
            if (vmaf.TryGetProperty("min", out var minEl))
                minEl.TryGetDouble(out min);
        }

        // Fallback: compute from frame scores if pooled metrics are incomplete.
        if (mean == 0 && frameScores.Count > 0)
            mean = frameScores.Average();
        if (min == 0 && frameScores.Count > 0)
            min = frameScores.Min();

        return new VmafResult
        {
            Mean = mean,
            HarmonicMean = harmonicMean,
            Min = min,
            FrameScores = frameScores,
        };
    }
}
