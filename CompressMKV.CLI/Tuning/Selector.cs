namespace CompressMkv;

// =========================================================================
// CQ selection from a list of per-CQ VMAF aggregates.
//
// Three gates (mean / P05 / P01):
//   - Mean catches systematic quality drops across the whole sampled content.
//   - P05 bounds the bottom-5% tail at "very high quality" (95) — keeps the
//     selector from picking a CQ that has a long mediocre tail.
//   - P01 catches rare really-bad scenes (single-second high-motion sequences,
//     hard cuts) that don't move P05 because they're <1% of frames but are
//     visible if quality drops too far.  Threshold 90 = "just barely good".
//
// Selection picks the highest CQ where ALL THREE gates pass — that's the most
// compressed setting that meets every quality bound.  Falls back to the CQ
// with the best mean if no CQ passes.
//
// Marginal-pass detection: if any selected metric is within
// MarginalThresholdPoints of its target, the result is flagged IsMarginal and
// the user is told which metric(s) and by how much.  NVENC's spatial-aq /
// temporal-aq introduce ±0.1–0.3 score noise across runs, so a borderline
// CQ may classify differently next time — IsMarginal makes that visible.
//
// Note on harmonic mean: CqAggregate computes HarmonicMeanVmaf for diagnostic
// reporting only.  It is intentionally NOT used in selection — the P05 and
// P01 gates already constrain the low-quality tail more directly than
// harmonic mean would.
// =========================================================================
public static class Selector
{
    /// <summary>
    /// VMAF-point margin within which a passing metric is flagged "marginal".
    /// Sized to NVENC's per-run score noise (±0.1–0.3) plus a safety factor —
    /// any pass within this margin could classify differently on a re-encode.
    /// </summary>
    public const double MarginalThresholdPoints = 0.5;

    public static Selection Select(Config cfg, List<CqAggregate> results)
    {
        // Pick the highest CQ that passes all three frame-level VMAF thresholds.
        var pass = results
            .Where(r => r.MeanVmaf >= cfg.TargetMeanVmaf
                     && r.P05Vmaf  >= cfg.TargetP05Vmaf
                     && r.P01Vmaf  >= cfg.TargetP01Vmaf)
            .OrderByDescending(r => r.Cq)
            .FirstOrDefault();

        if (pass != null)
        {
            var sel = BuildSelection(pass,
                $"Meets thresholds mean≥{cfg.TargetMeanVmaf:F1}, p05≥{cfg.TargetP05Vmaf:F1}, " +
                $"p01≥{cfg.TargetP01Vmaf:F1}; chose highest passing CQ.");
            ApplyMarginalCheck(sel, pass, cfg);
            return sel;
        }

        var best = results.OrderByDescending(r => r.MeanVmaf).First();
        var fallback = BuildSelection(best,
            "No CQ met all thresholds; chose CQ with best mean VMAF.");
        // Fallback selections are inherently below threshold — flag as marginal.
        fallback.IsMarginal = true;
        fallback.MarginalReasons.Add(
            $"no CQ met all three gates — best mean was {best.MeanVmaf:F2} " +
            $"(target ≥ {cfg.TargetMeanVmaf:F1}).");
        return fallback;
    }

    private static Selection BuildSelection(CqAggregate r, string reason) => new()
    {
        SelectedCq = r.Cq,
        SelectedMeanVmaf = r.MeanVmaf,
        SelectedHarmonicMeanVmaf = r.HarmonicMeanVmaf,
        SelectedP05Vmaf = r.P05Vmaf,
        SelectedP01Vmaf = r.P01Vmaf,
        SelectedMinVmaf = r.MinVmaf,
        TotalFrameCount = r.TotalFrameCount,
        Reason = reason,
    };

    private static void ApplyMarginalCheck(Selection sel, CqAggregate pass, Config cfg)
    {
        if (Math.Abs(pass.MeanVmaf - cfg.TargetMeanVmaf) < MarginalThresholdPoints)
            sel.MarginalReasons.Add(
                $"mean={pass.MeanVmaf:F2} only {pass.MeanVmaf - cfg.TargetMeanVmaf:F2} " +
                $"above target {cfg.TargetMeanVmaf:F1}");

        if (Math.Abs(pass.P05Vmaf - cfg.TargetP05Vmaf) < MarginalThresholdPoints)
            sel.MarginalReasons.Add(
                $"p05={pass.P05Vmaf:F2} only {pass.P05Vmaf - cfg.TargetP05Vmaf:F2} " +
                $"above target {cfg.TargetP05Vmaf:F1}");

        if (Math.Abs(pass.P01Vmaf - cfg.TargetP01Vmaf) < MarginalThresholdPoints)
            sel.MarginalReasons.Add(
                $"p01={pass.P01Vmaf:F2} only {pass.P01Vmaf - cfg.TargetP01Vmaf:F2} " +
                $"above target {cfg.TargetP01Vmaf:F1}");

        sel.IsMarginal = sel.MarginalReasons.Count > 0;
    }
}
