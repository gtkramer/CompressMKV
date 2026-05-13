namespace MkvHelper;

/// <summary>
/// Wall-clock breakdown for a single file's pipeline.  Lets you see directly
/// where time is being spent and whether the <see cref="ResourcePool"/>
/// produced the expected utilization profile.  Total ≠ sum of phases —
/// phases overlap across files via the pool, so per-file totals reflect
/// wall-clock with that file's pool-waits included.
///
/// Each phase records three numbers:
///   • Wall      — clock between entering and leaving the phase.
///   • QueueSum  — sum of pool waits across every op in the phase.
///   • RunSum    — sum of pool holds across every op in the phase.
///
/// For sequential phases (Detection, FinalEncode, Verification) Wall ≈
/// QueueSum + RunSum.  For parallel phases (Phase 1, Phase 2) RunSum may
/// exceed Wall — that excess is the parallelism the pool achieved.
/// </summary>
public sealed class PhaseTimings
{
    public PhaseTiming Detection { get; set; } = PhaseTiming.Zero;
    public PhaseTiming TuningPhase1 { get; set; } = PhaseTiming.Zero;
    public PhaseTiming TuningPhase2 { get; set; } = PhaseTiming.Zero;
    public PhaseTiming FinalEncode { get; set; } = PhaseTiming.Zero;
    public PhaseTiming Verification { get; set; } = PhaseTiming.Zero;
    public TimeSpan Total { get; set; }
}

/// <summary>Wall + queue-sum + run-sum for one phase.  OpCount is the number
/// of pool acquires the phase issued — useful to know whether a "32 s of run"
/// number came from one heavy op or twenty light ones.</summary>
public sealed record class PhaseTiming(TimeSpan Wall, TimeSpan QueueSum, TimeSpan RunSum, int OpCount)
{
    public static PhaseTiming Zero { get; } = new(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 0);
}
