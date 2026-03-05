namespace CompressMkv;

public static class Selector
{
    public static Selection Select(Config cfg, List<CqAggregate> results)
    {
        // Pick the highest CQ that passes both frame-level VMAF thresholds.
        var pass = results.Where(r => r.MeanVmaf >= cfg.TargetMeanVmaf && r.P05Vmaf >= cfg.TargetP05Vmaf)
                          .OrderByDescending(r => r.Cq)
                          .FirstOrDefault();

        if (pass != null)
            return new Selection
            {
                SelectedCq = pass.Cq,
                SelectedMeanVmaf = pass.MeanVmaf,
                SelectedHarmonicMeanVmaf = pass.HarmonicMeanVmaf,
                SelectedP05Vmaf = pass.P05Vmaf,
                SelectedP01Vmaf = pass.P01Vmaf,
                SelectedMinVmaf = pass.MinVmaf,
                TotalFrameCount = pass.TotalFrameCount,
                Reason = $"Meets frame-level thresholds mean>={cfg.TargetMeanVmaf:F1}, p05>={cfg.TargetP05Vmaf:F1}; choose highest CQ passing."
            };

        var best = results.OrderByDescending(r => r.MeanVmaf).First();
        return new Selection
        {
            SelectedCq = best.Cq,
            SelectedMeanVmaf = best.MeanVmaf,
            SelectedHarmonicMeanVmaf = best.HarmonicMeanVmaf,
            SelectedP05Vmaf = best.P05Vmaf,
            SelectedP01Vmaf = best.P01Vmaf,
            SelectedMinVmaf = best.MinVmaf,
            TotalFrameCount = best.TotalFrameCount,
            Reason = "No CQ met frame-level thresholds; choose best mean VMAF."
        };
    }
}
