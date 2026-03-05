namespace CompressMkv;

public sealed class SampleMetric
{
    public int SampleIndex { get; set; }
    public SampleWindow Window { get; set; } = new();
    public string ReferenceClipPath { get; set; } = "";
    public string EncodedPath { get; set; } = "";
    public string VmafLogPath { get; set; } = "";
    public double VmafMean { get; set; }
    public double VmafHarmonicMean { get; set; }

    /// <summary>
    /// Per-frame VMAF scores for this sample. Used for robust
    /// percentile computation across all samples at a given CQ level.
    /// </summary>
    public List<double> FrameVmafScores { get; set; } = new();
}
