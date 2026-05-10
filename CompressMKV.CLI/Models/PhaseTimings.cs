namespace CompressMkv;

/// <summary>
/// Wall-clock breakdown for a single file's pipeline.  Lets you see directly
/// where time is being spent and whether the resource gates produced the
/// expected utilization profile.  Total ≠ sum of phases — phases overlap
/// across files via the global <see cref="CpuGate"/> and <see cref="GpuGate"/>,
/// so per-file totals reflect wall-clock with that file's gate-waits included.
/// </summary>
public sealed class PhaseTimings
{
    public TimeSpan Detection { get; set; }
    public TimeSpan Previews { get; set; }
    public TimeSpan TuningPhase1 { get; set; }
    public TimeSpan TuningPhase2 { get; set; }
    public TimeSpan FinalEncode { get; set; }
    public TimeSpan Verification { get; set; }
    public TimeSpan Total { get; set; }
}
