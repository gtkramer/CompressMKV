namespace CompressMkv;

/// <summary>
/// Parsed result from a VMAF JSON log, including per-frame scores.
/// </summary>
public sealed class VmafResult
{
    public double Mean { get; set; }
    public double HarmonicMean { get; set; }
    public double Min { get; set; }

    /// <summary>
    /// Per-frame VMAF scores from the "frames" array in the JSON log.
    /// Used for robust percentile computation across all samples.
    /// </summary>
    public List<double> FrameScores { get; set; } = new();
}
