namespace MkvHelper;

public sealed class TuningResult
{
    public List<SampleWindow> SampleWindows { get; set; } = [];

    /// <summary>
    /// Per-CQ aggregates for every CQ the binary search actually probed,
    /// in the order they were probed.  Useful for after-the-fact analysis
    /// of how the search converged.
    /// </summary>
    public List<CqAggregate> CqResults { get; set; } = [];

    public Selection Selection { get; set; } = new();

    /// <summary>The [MinCq, MaxCq] integer range the search was run over.</summary>
    public int SearchMinCq { get; set; }
    public int SearchMaxCq { get; set; }

    /// <summary>
    /// Wall/queue/run breakdown for the two tuning phases.  Phase 1 fires N
    /// parallel ref-extracts; Phase 2 fires N samples × M probes of two ops
    /// each (sample-encode + vmaf).  Aggregated so CompressCommand can build
    /// the per-file time card without re-walking the metrics collector.
    /// </summary>
    public PhaseTiming Phase1Timing { get; set; } = PhaseTiming.Zero;
    public PhaseTiming Phase2Timing { get; set; } = PhaseTiming.Zero;
}
