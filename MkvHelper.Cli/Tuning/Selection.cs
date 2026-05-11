namespace MkvHelper;

public sealed class Selection
{
    public int SelectedCq { get; set; }
    public double SelectedMeanVmaf { get; set; }
    public double SelectedHarmonicMeanVmaf { get; set; }
    public double SelectedP05Vmaf { get; set; }
    public double SelectedP01Vmaf { get; set; }
    public double SelectedMinVmaf { get; set; }
    public int TotalFrameCount { get; set; }
    public string Reason { get; set; } = "";

    /// <summary>
    /// True when at least one of the selected metrics is within
    /// <see cref="Selector.MarginalThresholdPoints"/> VMAF points of its target.
    /// NVENC encoder noise alone can shift scores by ±0.1–0.3 across runs, so a
    /// "barely passing" CQ may score differently on a re-run.  Surfacing this
    /// lets the user know the choice is borderline and might warrant manual
    /// review or a different threshold setting.
    /// </summary>
    public bool IsMarginal { get; set; }

    /// <summary>
    /// Human-readable list of which metrics were marginal and by how much.
    /// Empty when <see cref="IsMarginal"/> is false.
    /// </summary>
    public List<string> MarginalReasons { get; set; } = new();
}
