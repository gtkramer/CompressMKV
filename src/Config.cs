namespace CompressMkv;

public sealed class Config
{
    public string InputFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string Ffmpeg { get; set; } = "ffmpeg";
    public string Ffprobe { get; set; } = "ffprobe";

    // --- VMAF model selection ---
    // On Arch Linux: install 'vmaf' package → models at /usr/share/model/
    public string VmafModelDir { get; set; } = "/usr/share/model";
    public string VmafStandardModelName { get; set; } = "vmaf_v0.6.1.json";
    public string Vmaf4kModelName { get; set; } = "vmaf_4k_v0.6.1.json";

    public int NvencSlots { get; set; } = 2;
    public int NvdecSlots { get; set; } = 2;

    public List<int> CandidateCq { get; set; } = new();
    public int SampleCount { get; set; } = 16;
    public int SampleWindowSeconds { get; set; } = 12;
    public int RandomSeed { get; set; } = 12345;

    // Frame-level VMAF thresholds (computed from per-frame scores across all samples).
    // mean ≥ 97 + p05 ≥ 95 → quality loss virtually imperceptible at close viewing distances.
    public double TargetMeanVmaf { get; set; } = 97.0;
    public double TargetP05Vmaf { get; set; } = 95.0;

    public bool HdrApplyCqLadderShift { get; set; } = true;
    public int HdrCqLadderDelta { get; set; } = 2;
    public int MinCq { get; set; } = 0;

    public string NvencPreset { get; set; } = "p7";
    public int RcLookahead { get; set; } = 48;

    public bool UseNvdecForEncode { get; set; } = true;

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

    // --- Model resolution helper ---

    /// <summary>
    /// Selects the appropriate VMAF model based on source resolution.
    /// 4K model for ≥ 3840×2160; standard model for everything else.
    /// </summary>
    public string ResolveVmafModelPath(int? width, int? height)
    {
        bool is4k = (width ?? 0) >= 3840 || (height ?? 0) >= 2160;
        string modelName = is4k ? Vmaf4kModelName : VmafStandardModelName;
        return Path.Combine(VmafModelDir, modelName);
    }
}
