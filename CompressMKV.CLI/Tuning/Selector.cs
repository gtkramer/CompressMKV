namespace CompressMkv;

// =========================================================================
// CQ selection from a list of per-CQ VMAF aggregates.
//
// Selection criterion: highest CQ where mean VMAF ≥ TargetMeanVmaf AND
// P05 VMAF ≥ TargetP05Vmaf.  P05 is the 5th percentile across every per-frame
// score from every sample window — it bounds the worst-quality frames
// directly.  Mean catches systematic quality drops.
//
// Note on harmonic mean: CqAggregate computes HarmonicMeanVmaf for diagnostic
// reporting only.  It is intentionally NOT used in selection — the P05 gate
// already constrains the low-quality tail more directly.  If P05 ≥ 95 then
// the bottom 5% of frames are bounded at 95+, which keeps harmonic mean
// from dropping more than ~0.5 below arithmetic mean.  Adding a third gate
// on harmonic mean would be redundant with P05 in practice.
// =========================================================================
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
