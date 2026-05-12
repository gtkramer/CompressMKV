using System.Text.Json;

namespace MkvHelper;

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
        await using FileStream fs = File.OpenRead(path);
        JsonDocument doc = await JsonDocument.ParseAsync(fs, cancellationToken: ct);

        // --- Per-frame scores (the frames[] array) ---
        List<double> frameScores = [];
        if (doc.RootElement.TryGetProperty("frames", out JsonElement frames))
        {
            foreach (JsonElement frame in frames.EnumerateArray())
            {
                if (frame.TryGetProperty("metrics", out JsonElement metrics) &&
                    metrics.TryGetProperty("vmaf", out JsonElement vmafEl) &&
                    vmafEl.TryGetDouble(out double score))
                {
                    frameScores.Add(score);
                }
            }
        }

        // --- Pooled metrics ---
        double mean = 0, harmonicMean = 0, min = 0;
        if (doc.RootElement.TryGetProperty("pooled_metrics", out JsonElement pooled) &&
            pooled.TryGetProperty("vmaf", out JsonElement vmaf))
        {
            if (vmaf.TryGetProperty("mean", out JsonElement meanEl))
                meanEl.TryGetDouble(out mean);
            if (vmaf.TryGetProperty("harmonic_mean", out JsonElement hmEl))
                hmEl.TryGetDouble(out harmonicMean);
            if (vmaf.TryGetProperty("min", out JsonElement minEl))
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
