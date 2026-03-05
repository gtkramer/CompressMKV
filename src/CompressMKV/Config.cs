namespace CompressMkv;

public sealed class Config
{
    public string InputFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string Ffmpeg { get; set; } = "ffmpeg";
    public string Ffprobe { get; set; } = "ffprobe";
    public string VmafModelPath { get; set; } = "";

    public int NvencSlots { get; set; } = 2;
    public int NvdecSlots { get; set; } = 2;

    public List<int> CandidateCq { get; set; } = new();
    public int SampleCount { get; set; } = 12;
    public int SampleWindowSeconds { get; set; } = 8;
    public int RandomSeed { get; set; } = 12345;

    public double TargetMeanVmaf { get; set; } = 95.0;
    public double TargetP05Vmaf { get; set; } = 92.0;

    public bool HdrApplyCqLadderShift { get; set; } = true;
    public int HdrCqLadderDelta { get; set; } = 2;
    public int MinCq { get; set; } = 0;

    public string NvencPreset { get; set; } = "p7";
    public int RcLookahead { get; set; } = 48;

    public bool UseNvdecForEncode { get; set; } = true;
    public bool UseNvdecForVmaf { get; set; } = false;

    // --- Content detection (single-pass full-file idet) ---

    /// <summary>
    /// Use hardware-accelerated decoding for the detection pass.
    /// Requires a supported GPU (e.g. NVIDIA with NVDEC).
    /// </summary>
    public bool UseHwaccelForDetection { get; set; } = true;

    // Detection thresholds are internal constants in ContentDetector.
    // They are derived from 3:2 pulldown signal physics and are not user-tunable.

    // --- Preview gating ---
    public double PreviewMaxConfidenceToGenerate { get; set; } = 0.60;
    public int PreviewCount { get; set; } = 3;
    public double PreviewDurationSeconds { get; set; } = 10.0;

    // --- Output ---
    public string OutputExtension { get; set; } = ".mkv";
}
