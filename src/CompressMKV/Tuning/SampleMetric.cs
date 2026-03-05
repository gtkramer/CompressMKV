namespace CompressMkv;

public sealed class SampleMetric
{
    public int SampleIndex { get; set; }
    public SampleWindow Window { get; set; } = new();
    public string EncodedPath { get; set; } = "";
    public string VmafLogPath { get; set; } = "";
    public double VmafMean { get; set; }
}
