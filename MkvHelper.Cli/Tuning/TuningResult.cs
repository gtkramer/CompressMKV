namespace MkvHelper;

public sealed class TuningResult
{
    public List<SampleWindow> SampleWindows { get; set; } = new();

    /// <summary>
    /// Per-CQ aggregates for every CQ the binary search actually probed,
    /// in the order they were probed.  Useful for after-the-fact analysis
    /// of how the search converged.
    /// </summary>
    public List<CqAggregate> CqResults { get; set; } = new();

    public Selection Selection { get; set; } = new();

    /// <summary>The [MinCq, MaxCq] integer range the search was run over.</summary>
    public int SearchMinCq { get; set; }
    public int SearchMaxCq { get; set; }

    public TimeSpan Phase1Elapsed { get; set; }
    public TimeSpan Phase2Elapsed { get; set; }
}
