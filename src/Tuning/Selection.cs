namespace CompressMkv;

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
}
