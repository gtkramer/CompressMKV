namespace CompressMkv;

public sealed class CqAggregate
{
    public int Cq { get; set; }
    public double MeanVmaf { get; set; }
    public double P05Vmaf { get; set; }
    public double MinVmaf { get; set; }
    public List<SampleMetric> Samples { get; set; } = new();

    public static CqAggregate From(int cq, List<SampleMetric> samples)
    {
        var vals = samples.Select(s => s.VmafMean).OrderBy(x => x).ToList();
        double mean = vals.Average();
        double min = vals.First();
        double p05 = Percentile(vals, 0.05);
        return new CqAggregate { Cq = cq, MeanVmaf = mean, MinVmaf = min, P05Vmaf = p05, Samples = samples };
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
