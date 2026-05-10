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

    // --- Concurrency / scheduling ---
    //
    // The pipeline has three resource pools that need to be balanced so neither
    // the CPU nor the GPU sits idle and neither ends up thrashing:
    //
    //   1. Cross-file parallelism (Parallel.ForEachAsync over input files)
    //   2. CPU-heavy ffmpeg ops (Phase 1 ref extraction + Phase 2 VMAF +
    //      detection + verification + final-encode restoration)
    //   3. GPU NVENC sample encodes (gated separately via GpuGate)
    //
    // Defaults are computed from Environment.ProcessorCount so the same config
    // is reasonable on different machines.  All three values can be overridden
    // explicitly if you want to tune for a specific workload.

    /// <summary>
    /// Maximum number of input files in flight at once.  Matches the GPU's
    /// NVENC engine count by default — going higher just queues files at the
    /// GpuGate while burning CPU on Phase 1 / VMAF for files that can't yet
    /// encode.  The RTX 5080 has 2 NVENC engines.
    /// </summary>
    public int MaxConcurrentFiles { get; set; } = 2;

    /// <summary>
    /// Maximum CPU-heavy ffmpeg processes running concurrently across ALL files.
    /// Single global semaphore — when one file is in Phase 1 and another is
    /// in Phase 2, the pool self-balances.  Defaults to <c>cores / 5</c>
    /// (clamped to 2..8) so a 20-core CPU runs 4 concurrent CPU ffmpeg ops.
    /// </summary>
    public int MaxConcurrentCpuFfmpegOps { get; set; } =
        Math.Clamp(Environment.ProcessorCount / 5, 2, 8);

    /// <summary>
    /// `-threads N` setting for CPU-heavy ffmpeg invocations (Phase 1 ref
    /// extraction, detection, verification, final-encode restore).  Sized so
    /// <c>MaxConcurrentCpuFfmpegOps × FfmpegCpuThreads ≈ ProcessorCount</c>.
    /// </summary>
    public int FfmpegCpuThreads { get; set; } =
        Math.Clamp(Environment.ProcessorCount / 5, 2, 6);

    /// <summary>
    /// `-threads N` setting for GPU-bound ffmpeg invocations (Phase 2 sample
    /// encodes from FFV1, where NVENC does the heavy work and ffmpeg just
    /// handles I/O).
    /// </summary>
    public int FfmpegGpuThreads { get; set; } = 2;

    /// <summary>
    /// `n_threads=N` setting for the libvmaf filter inside the VMAF ffmpeg
    /// invocation.  This is the hottest CPU phase, so libvmaf should get most
    /// of a process's CPU budget.  Total threads per VMAF process ≈
    /// <c>FfmpegCpuThreads + LibvmafThreads</c>.
    /// </summary>
    public int LibvmafThreads { get; set; } =
        Math.Clamp(Environment.ProcessorCount / 5, 2, 6);

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
