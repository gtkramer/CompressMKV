namespace CompressMkv;

public sealed class Config
{
    public string InputFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";
    public string Ffmpeg { get; set; } = "ffmpeg";
    public string Ffprobe { get; set; } = "ffprobe";

    // --- VMAF model selection ---
    // libvmaf has the standard models compiled in; no file-on-disk required.
    // The version names below are passed to libvmaf as `model=version=NAME`.
    // ResolveVmafModelVersion picks between them by source resolution.
    public string VmafStandardModelVersion { get; set; } = "vmaf_v0.6.1";
    public string Vmaf4kModelVersion { get; set; } = "vmaf_4k_v0.6.1";

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

    /// <summary>
    /// `n_subsample=N` setting for libvmaf.  N=1 measures every frame (most
    /// accurate); N=2 measures every other frame (~2× faster, ~0.1 VMAF point
    /// accuracy loss); N=4 measures every 4th frame (~4× faster).  For the
    /// "find a CQ that hits the threshold" use case, 1 is correct — the cost
    /// of an over-conservative pick (slightly larger files for the same
    /// imperceptible quality) outweighs the speed gain from subsampling.
    /// </summary>
    public int LibvmafSubsample { get; set; } = 1;

    /// <summary>
    /// CQ ladder for the tuning search.  Range 16–34 in steps of 2:
    ///   - Lower bound 16: near-lossless quality safety floor.  Below 16
    ///     the encoder rarely improves on already-perceptually-transparent
    ///     output and we'd just be wasting bits.
    ///   - Upper bound 34: practical maximum compression for AV1 NVENC; CQs
    ///     above this consistently fail the 97/95 quality gates on real content.
    ///   - Step 2: ten ladder rungs gives reasonable granularity (~0.5 VMAF
    ///     points per step on typical content); step 1 would double the search
    ///     work for sub-perceptual differences between adjacent rungs.
    /// </summary>
    public List<int> CandidateCq { get; set; } = new();

    /// <summary>
    /// Target number of stratified random sample windows.  16 × 12s = 192s
    /// of measured content gives ~4,608 frames per CQ for a 24p source —
    /// statistically robust for mean and P05.  See <see cref="Sampler"/> for
    /// how this scales down on short sources.
    /// </summary>
    public int SampleCount { get; set; } = 16;

    /// <summary>
    /// Target length of each sample window in seconds.  12s is long enough for
    /// NVENC's <c>rc-lookahead</c> (~2s) and any temporal AQ / scene-change
    /// detection to settle into steady-state behavior, so the VMAF score for
    /// the window reflects the encoder's normal output rather than warm-up.
    /// Below ~5s, encoder warm-up dominates and measurements get noisy.
    /// </summary>
    public int SampleWindowSeconds { get; set; } = 12;

    /// <summary>RNG seed for reproducible window selection.  Same input always
    /// gets the same windows; runs with the same seed are byte-deterministic.</summary>
    public int RandomSeed { get; set; } = 12345;

    // -- VMAF quality thresholds --
    //
    // Computed from per-frame scores pooled across all sample windows.
    // Three gates: mean catches systematic quality drops, P05 bounds the
    // bottom-5% tail, P01 catches rare really-bad frames the P05 gate misses.

    /// <summary>
    /// Required mean VMAF.  Industry convention for the standard <c>vmaf_v0.6.1</c>
    /// model: 97 = "perceptually transparent at typical viewing distances."
    /// Lower (e.g. 95) for phone-screen viewing; higher (99+) for archive-grade.
    /// </summary>
    public double TargetMeanVmaf { get; set; } = 97.0;

    /// <summary>
    /// Required 5th-percentile VMAF.  At most 5% of frames may score below this.
    /// 95 = "very high quality" for the bottom-of-distribution frames.
    /// Catches scenes that the mean would average over.
    /// </summary>
    public double TargetP05Vmaf { get; set; } = 95.0;

    /// <summary>
    /// Required 1st-percentile VMAF.  Bounds the worst ~1% of frames.  Catches
    /// rare bad scenes (single-second high-motion sequences, hard cuts) that
    /// don't move the P05 needle but are perceptually visible if they drop too
    /// low.  90 corresponds to "good quality" per the standard interpretation
    /// — drops below this are starting to be visible to attentive viewers.
    /// </summary>
    public double TargetP01Vmaf { get; set; } = 90.0;

    /// <summary>
    /// Enables a uniform CQ-ladder shift for HDR content.  HDR's wider dynamic
    /// range makes quantization errors more visible — the same CQ on HDR yields
    /// lower VMAF scores than on SDR.  Shifting the ladder toward higher quality
    /// compensates.
    /// </summary>
    public bool HdrApplyCqLadderShift { get; set; } = true;

    /// <summary>
    /// HDR CQ ladder shift in CQ units.  Default 2 = "shift the whole search
    /// 2 CQ steps toward higher quality" for HDR.  Empirical heuristic;
    /// industry recommendations span 1–5 and modern AV1 may need less.
    /// </summary>
    public int HdrCqLadderDelta { get; set; } = 2;

    /// <summary>
    /// Floor for the shifted CQ ladder so the HDR shift can't drive CQ negative.
    /// </summary>
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
    /// Selects the appropriate libvmaf built-in model version based on source
    /// resolution.  4K model for ≥ 3840×2160; standard (1080p-tuned) model for
    /// everything else.  Returned string is the libvmaf version identifier
    /// (e.g. "vmaf_v0.6.1") suitable for `model=version=NAME` in the filter.
    /// </summary>
    public string ResolveVmafModelVersion(int? width, int? height)
    {
        bool is4k = (width ?? 0) >= 3840 || (height ?? 0) >= 2160;
        return is4k ? Vmaf4kModelVersion : VmafStandardModelVersion;
    }
}
