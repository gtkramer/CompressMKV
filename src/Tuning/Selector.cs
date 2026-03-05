namespace CompressMkv;

public static class Selector
{
    public static Selection Select(Config cfg, List<CqAggregate> results)
    {
        var pass = results.Where(r => r.MeanVmaf >= cfg.TargetMeanVmaf && r.P05Vmaf >= cfg.TargetP05Vmaf)
                          .OrderByDescending(r => r.Cq)
                          .FirstOrDefault();

        if (pass != null)
            return new Selection
            {
                SelectedCq = pass.Cq,
                SelectedMeanVmaf = pass.MeanVmaf,
                SelectedP05Vmaf = pass.P05Vmaf,
                SelectedMinVmaf = pass.MinVmaf,
                Reason = $"Meets thresholds mean>={cfg.TargetMeanVmaf:F1}, p05>={cfg.TargetP05Vmaf:F1}; choose highest CQ passing."
            };

        var best = results.OrderByDescending(r => r.MeanVmaf).First();
        return new Selection
        {
            SelectedCq = best.Cq,
            SelectedMeanVmaf = best.MeanVmaf,
            SelectedP05Vmaf = best.P05Vmaf,
            SelectedMinVmaf = best.MinVmaf,
            Reason = "No CQ met thresholds; choose best mean VMAF."
        };
    }
}
