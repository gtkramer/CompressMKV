namespace CompressMkv;

public sealed class CqAggregate
{
    public int Cq { get; set; }
    public double MeanVmaf { get; set; }
    public double HarmonicMeanVmaf { get; set; }
    public double P05Vmaf { get; set; }
    public double P01Vmaf { get; set; }
    public double MinVmaf { get; set; }
    public int TotalFrameCount { get; set; }
    public List<SampleMetric> Samples { get; set; } = new();

    /// <summary>
    /// Aggregates from ALL per-frame VMAF scores across all samples.  With 16
    /// windows × 12s × 24fps ≈ 4,608 frames per CQ.
    ///
    /// A note on effective sample size: <see cref="TotalFrameCount"/> reports
    /// the raw frame count, which is the right denominator for percentile
    /// computation but NOT a measure of statistical independence.  Frames
    /// within a 12-second window are highly correlated (same scene, same
    /// motion characteristics), so for confidence-interval purposes the
    /// effective sample size is closer to the WINDOW count (16 typically) —
    /// each window is one approximately-independent observation.
    ///
    /// In practice this means: percentile estimates have a wider confidence
    /// interval than the raw frame count would suggest.  Mean VMAF, P05,
    /// and P01 reported here are point estimates with sampling-derived noise
    /// of ~±0.5–1.0 VMAF points across runs (vs. the ~±0.05 you'd naively
    /// expect from 4,608 truly-independent samples).  This is fundamental to
    /// VMAF tuning at any scale, not a bug — Selector.MarginalThresholdPoints
    /// (0.5) accounts for it.
    /// </summary>
    public static CqAggregate From(int cq, List<SampleMetric> samples)
    {
        // Collect ALL per-frame scores across every sample for robust percentile computation.
        var allFrameScores = samples
            .SelectMany(s => s.FrameVmafScores)
            .OrderBy(x => x)
            .ToList();

        double mean = allFrameScores.Count > 0 ? allFrameScores.Average() : 0;
        double min = allFrameScores.Count > 0 ? allFrameScores[0] : 0;
        double p05 = Percentile(allFrameScores, 0.05);
        double p01 = Percentile(allFrameScores, 0.01);

        // Harmonic mean — more sensitive to quality drops than arithmetic mean.
        var positive = allFrameScores.Where(x => x > 0).ToList();
        double harmonicMean = positive.Count > 0
            ? positive.Count / positive.Sum(x => 1.0 / x)
            : 0;

        return new CqAggregate
        {
            Cq = cq,
            MeanVmaf = mean,
            HarmonicMeanVmaf = harmonicMean,
            P05Vmaf = p05,
            P01Vmaf = p01,
            MinVmaf = min,
            TotalFrameCount = allFrameScores.Count,
            Samples = samples,
        };
    }

    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return double.NaN;
        if (p <= 0) return sorted[0];
        if (p >= 1) return sorted[^1];
        double idx = (sorted.Count - 1) * p;
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        double frac = idx - lo;
        return sorted[lo] * (1 - frac) + sorted[hi] * frac;
    }
}
