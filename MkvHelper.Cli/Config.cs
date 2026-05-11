namespace MkvHelper;

public sealed class Config
{
    public string InputFolder { get; set; } = "";
    public string OutputFolder { get; set; } = "";

    // --- VMAF model selection ---
    // libvmaf has the standard models compiled in; no file-on-disk required.
    // The version names below are passed to libvmaf as `model=version=NAME`.
    // ResolveVmafModelVersion picks between them by source resolution.
    public string VmafStandardModelVersion { get; set; } = "vmaf_v0.6.1";
    public string Vmaf4kModelVersion { get; set; } = "vmaf_4k_v0.6.1";

    public int NvencSlots { get; set; } = 2;
    public int NvdecSlots { get; set; } = 2;

    /// <summary>
    /// In-flight libvmaf_cuda jobs allowed at once.  CUDA-VMAF runs on the
    /// general-purpose CUDA cores rather than the dedicated NVENC/NVDEC
    /// engines, so it has its own gate.  Two slots lets us keep both NVENC
    /// engines busy with sample encodes while VMAF measures the previous
    /// pair — without flooding the GPU with parallel VMAF processes that
    /// would crowd out NVENC's CUDA-based AQ helpers and consume too much
    /// VRAM.  Falls back to <see cref="MaxConcurrentCpuFfmpegOps"/>'s share
    /// of the CPU budget when the system ffmpeg lacks libvmaf_cuda (i.e.
    /// when the bundled container hasn't been built and we're using native
    /// libvmaf instead).
    /// </summary>
    public int CudaVmafSlots { get; set; } = 2;

    /// <summary>
    /// Whether to route HDR sources through libvmaf_cuda (true) or through
    /// the CPU libvmaf path (false, default).  HDR comparison requires a
    /// zscale tonemap chain, and zscale is CPU-only — so even when this is
    /// true, the tonemap work stays on CPU and only the libvmaf computation
    /// itself moves to the GPU.  Default is false because keeping HDR on
    /// CpuGate naturally separates the two workloads (HDR runs are
    /// uncommon, but when they do happen they shouldn't fight SDR encodes
    /// for the same CUDA-VMAF slots).  Flip to true to consolidate VMAF
    /// gating on the GPU regardless of source.  No effect when libvmaf_cuda
    /// isn't available (system ffmpeg without container) — everything
    /// degrades to CPU libvmaf.
    /// </summary>
    public bool UseCudaVmafForHdr { get; set; } = false;

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

    // -- CQ search range --
    //
    // The tuning search looks for the highest CQ (most compression) that still
    // meets the VMAF gates below.  A binary search over the [MinCq, MaxCq]
    // integer range converges in ~log2(N) probes regardless of where the
    // answer lies — so the bounds are about pruning regions where the answer
    // is determined a priori, not about constraining the search granularity.
    //
    // NVENC AV1 supports CQ 0–63.  We default to a tighter window because:
    //   - Below MinCq=8: encodes are already perceptually transparent at any
    //     quality target we'd realistically set; smaller CQ just wastes bits.
    //   - Above MaxCq=55: real content uniformly fails the 97/95/90 gates;
    //     no point sampling there.
    // Bump MaxCq toward 63 if you're working with content that can survive
    // very aggressive compression; bump MinCq toward 0 if you target archive-
    // grade quality (mean ≥ 99).</summary>

    /// <summary>Lower bound (inclusive) of the binary CQ search.  Must be ≥ 0.</summary>
    public int MinCq { get; set; } = 8;

    /// <summary>Upper bound (inclusive) of the binary CQ search.  Must be ≤ 63 (NVENC AV1 hard ceiling).</summary>
    public int MaxCq { get; set; } = 55;

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

    // No HDR-specific CQ shift.  An HDR source whose perceived quality at a
    // given CQ is worse than its SDR equivalent will fail the VMAF gates and
    // the search will descend to a lower CQ on its own — driving selection
    // from measurement rather than a fixed offset.

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
