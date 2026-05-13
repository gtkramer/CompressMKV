namespace MkvHelper;

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
// When a marginal pick exists, we also pull the neighboring CQs (selected ± 1)
// from the result set, if probed, so the user can see what the alternatives
// looked like — "we landed on CQ=44 instead of 45 because at 45 p01 was 89.5".
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
        CqAggregate? pass = results
            .Where(r => r.MeanVmaf >= cfg.TargetMeanVmaf
                     && r.P05Vmaf  >= cfg.TargetP05Vmaf
                     && r.P01Vmaf  >= cfg.TargetP01Vmaf)
            .OrderByDescending(r => r.Cq)
            .FirstOrDefault();

        if (pass != null)
        {
            Selection sel = BuildSelection(pass,
                $"Meets thresholds mean≥{cfg.TargetMeanVmaf:F1}, p05≥{cfg.TargetP05Vmaf:F1}, " +
                $"p01≥{cfg.TargetP01Vmaf:F1}; chose highest passing CQ.");
            ApplyMarginalCheck(sel, pass, cfg, results);
            return sel;
        }

        CqAggregate best = results.OrderByDescending(r => r.MeanVmaf).First();
        Selection fallback = BuildSelection(best,
            "No CQ met all thresholds; chose CQ with best mean VMAF.");
        // Fallback selections are inherently below threshold — flag as marginal.
        fallback.IsMarginal = true;
        fallback.MarginalReasons.Add(
            $"no CQ met all three gates — best mean was {best.MeanVmaf:F2} " +
            $"(target ≥ {cfg.TargetMeanVmaf:F1}).");
        return fallback;
    }

    /// <summary>
    /// Names the gate(s) a CQ failed and by how much.  Used to annotate the
    /// trajectory line so a reader can see "CQ=45 failed (p01=89.5 < 90)"
    /// rather than just "CQ=45 FAIL".
    /// </summary>
    public static string FailureReason(CqAggregate r, Config cfg)
    {
        List<string> reasons = [];
        if (r.MeanVmaf < cfg.TargetMeanVmaf)
            reasons.Add($"mean={r.MeanVmaf:F2}<{cfg.TargetMeanVmaf:F1}");
        if (r.P05Vmaf < cfg.TargetP05Vmaf)
            reasons.Add($"p05={r.P05Vmaf:F2}<{cfg.TargetP05Vmaf:F1}");
        if (r.P01Vmaf < cfg.TargetP01Vmaf)
            reasons.Add($"p01={r.P01Vmaf:F2}<{cfg.TargetP01Vmaf:F1}");
        return reasons.Count == 0 ? "passed" : string.Join(", ", reasons);
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

    private static void ApplyMarginalCheck(
        Selection sel, CqAggregate pass, Config cfg, List<CqAggregate> results)
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

        if (!sel.IsMarginal) return;

        // Marginal pass → surface the neighboring probed CQs so a reader can
        // see what they would have gotten one notch tighter or looser.  The
        // binary search may or may not have actually probed selectedCq ± 1
        // (depends on where the search landed); we report whichever neighbors
        // are in the result set.
        CqAggregate? above = results.FirstOrDefault(r => r.Cq == pass.Cq + 1);
        CqAggregate? below = results.FirstOrDefault(r => r.Cq == pass.Cq - 1);
        if (above is not null)
            sel.MarginalReasons.Add(
                $"neighbor CQ={above.Cq}: mean={above.MeanVmaf:F2} p05={above.P05Vmaf:F2} " +
                $"p01={above.P01Vmaf:F2} — {FailureReason(above, cfg)}");
        if (below is not null)
            sel.MarginalReasons.Add(
                $"neighbor CQ={below.Cq}: mean={below.MeanVmaf:F2} p05={below.P05Vmaf:F2} " +
                $"p01={below.P01Vmaf:F2} — {FailureReason(below, cfg)}");
    }
}
