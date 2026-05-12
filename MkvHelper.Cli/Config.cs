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

    // --- Resource pool capacities ---
    //
    // Every pipeline operation declares its cost (CPU threads + GPU engines/lanes)
    // via a ResourceRequest; ResourcePool gates admission across all files
    // against the totals below.  No separate cap on "files in flight" — the
    // pool admits as many ops in parallel as the resources allow.
    //
    // CPU thread counts on each ffmpeg call are pinned to match the request
    // they were admitted with, so the pool's accounting is exact rather than
    // an estimate.  See the per-op *Threads properties below.

    /// <summary>Total CPU threads available for pipeline ffmpeg ops.  Defaults
    /// to <see cref="Environment.ProcessorCount"/>.</summary>
    public int CpuPool { get; set; } = Environment.ProcessorCount;

    /// <summary>NVENC engine count.  RTX 5080 = 2.  Final encodes hold one for
    /// the duration of the file; Phase 2 sample encodes hold one for ~5s each.</summary>
    public int NvencSlots { get; set; } = 2;

    /// <summary>NVDEC engine count.  RTX 5080 = 2.  Used by the final encode
    /// pipeline; detection and verification sw-decode intentionally.</summary>
    public int NvdecSlots { get; set; } = 2;

    /// <summary>
    /// libvmaf_cuda compute lanes.  CUDA-compute is shared between libvmaf_cuda
    /// (the only generic-CUDA-compute filter we use) and NVENC's AQ helpers.
    /// Two lanes lets us keep both NVENC engines busy with sample encodes while
    /// VMAF measures the previous pair — without flooding the GPU with parallel
    /// libvmaf_cuda processes that would crowd out NVENC's AQ kernels and
    /// consume too much VRAM.
    /// </summary>
    public int CudaSlots { get; set; } = 2;

    // --- Per-operation CPU thread counts ---
    //
    // Each operation declares how many CPU threads it consumes; the same
    // number is pinned on ffmpeg via -threads / -filter_threads so the
    // declared cost matches actual consumption.  Resource pool accounting
    // is therefore exact, not estimated.
    //
    // Phases whose decode side can run on either CPU or NVDEC declare two
    // costs (one per path) and a corresponding *Alternatives list that the
    // ResourcePool picks from at admission time.  This lets work flow to
    // whichever resource is currently free instead of pinning to one.
    //
    // Phases whose CPU cost depends on whether a CPU filter sits in the
    // pipeline (RefExtract, FinalEncode) declare two variants — progressive
    // (no filter) and filtered (IVTC / Deinterlace) — and a *RequestFor /
    // *AlternativesFor helper that picks by restore.FilterGraph presence.

    /// <summary>Threads for the full-file idet pass (Detection + Verification)
    /// when the source is decoded on CPU.  Splits decode + idet across cores.</summary>
    public int DetectionCpuThreads { get; set; } = 4;

    /// <summary>Threads for the full-file idet pass when decode is offloaded
    /// to NVDEC.  idet is single-threaded and memory-bound — 1 CPU thread
    /// for the filter is all that's needed; NVDEC handles the decode.</summary>
    public int DetectionGpuThreads { get; set; } = 1;

    /// <summary>Threads for one lossless x264 preview encode.</summary>
    public int PreviewThreads { get; set; } = 4;

    /// <summary>Total CPU budget for one progressive (no-filter) ref extract on
    /// the sw-decode path.  Split between decode and FFV1 encode in
    /// <see cref="Pipelines.ExtractReferenceClipAsync"/>.</summary>
    public int RefExtractCpuProgressiveThreads { get; set; } = 3;

    /// <summary>Total CPU budget for one filtered (IVTC/Deint) ref extract on
    /// the sw-decode path.  Adds a CPU filter to the decode→FFV1 chain.</summary>
    public int RefExtractCpuFilteredThreads { get; set; } = 4;

    /// <summary>Total CPU budget for one progressive (no-filter) ref extract on
    /// the NVDEC path.  Only the FFV1 encode side consumes CPU; NVDEC handles
    /// decode entirely on the dedicated engine.</summary>
    public int RefExtractGpuProgressiveThreads { get; set; } = 2;

    /// <summary>Total CPU budget for one filtered (IVTC/Deint) ref extract on
    /// the NVDEC path.  CPU filter + FFV1 encode still run on cores even though
    /// the decode side is offloaded.</summary>
    public int RefExtractGpuFilteredThreads { get; set; } = 4;

    /// <summary>Threads for one Phase 2 sample encode from FFV1.  NVENC does
    /// the encode; CPU handles FFV1 decode + I/O.  Low: 2 is enough.</summary>
    public int SampleEncodeThreads { get; set; } = 2;

    /// <summary>
    /// CPU threads for one Phase 2 VMAF measurement.  The process runs two
    /// CPU decoders (FFV1 ref + AV1 sample) plus the filtergraph (zscale /
    /// tonemap on HDR, format conversion, hwupload_cuda); libvmaf_cuda itself
    /// is GPU-bound and consumes no CPU.  4 = 1 thread per input decoder
    /// (×2 inputs) + 2 filter threads, matching the pins below.
    /// </summary>
    public int VmafThreads { get; set; } = 4;

    /// <summary>
    /// `-threads` value passed to ffmpeg for VMAF ops, applied as the default
    /// for each input decoder.  With 2 inputs (ref + encode), total decode
    /// threads = 2 × this value.  Filter threads come from
    /// <see cref="VmafFilterThreads"/>.
    /// </summary>
    public int VmafFfmpegThreads { get; set; } = 1;

    /// <summary>`-filter_threads` value for VMAF ops.  Bounds the filtergraph's
    /// per-frame parallelism (zscale + tonemap + format + hwupload).</summary>
    public int VmafFilterThreads { get; set; } = 2;

    /// <summary>CPU budget for a progressive final encode (no CPU filter).
    /// NVDEC→NVENC runs pure-GPU; the CPU only orchestrates, so reserving more
    /// than ~1 core would hide headroom from the rest of the pipeline.</summary>
    public int FinalEncodeProgressiveThreads { get; set; } = 1;

    /// <summary>CPU budget for a filtered final encode (IVTC fieldmatch+decimate
    /// or bwdif deinterlace).  The CPU filter is real sustained work between
    /// NVDEC and NVENC, so the budget matches the worst case.</summary>
    public int FinalEncodeFilteredThreads { get; set; } = 4;

    /// <summary>
    /// `n_subsample=N` setting for libvmaf.  N=1 measures every frame (most
    /// accurate); N=2 measures every other frame (~2× faster, ~0.1 VMAF point
    /// accuracy loss); N=4 measures every 4th frame (~4× faster).  For the
    /// "find a CQ that hits the threshold" use case, 1 is correct — the cost
    /// of an over-conservative pick (slightly larger files for the same
    /// imperceptible quality) outweighs the speed gain from subsampling.
    /// </summary>
    public int LibvmafSubsample { get; set; } = 1;

    // --- ResourceRequest factories (one per operation) ---
    //
    // Phases listed as *Alternatives expose both a CPU-only and a GPU-decode
    // shape; ResourcePool.AcquireAnyAsync picks the first that fits.  CPU
    // path is listed first so it's preferred when both are free — that keeps
    // NVDEC engines available for the final encode pipeline, which always
    // needs them.  When the CPU pool is saturated (typically at the start
    // of a batch where every file wants Detection at once), the GPU path
    // is granted instead so files don't sit idle waiting for CPU.
    //
    // Alternatives lists are cached after first read so AcquireAnyAsync
    // doesn't re-allocate the 2-element collection on every acquire.

    private IReadOnlyList<ResourceRequest>? _detectionAlternatives;
    public IReadOnlyList<ResourceRequest> DetectionAlternatives =>
        _detectionAlternatives ??=
        [
            new(Cpu: DetectionCpuThreads),
            new(Cpu: DetectionGpuThreads, Nvdec: 1),
        ];

    public IReadOnlyList<ResourceRequest> VerificationAlternatives => DetectionAlternatives;

    private IReadOnlyList<ResourceRequest>? _refExtractProgressiveAlternatives;
    public IReadOnlyList<ResourceRequest> RefExtractProgressiveAlternatives =>
        _refExtractProgressiveAlternatives ??=
        [
            new(Cpu: RefExtractCpuProgressiveThreads),
            new(Cpu: RefExtractGpuProgressiveThreads, Nvdec: 1),
        ];

    private IReadOnlyList<ResourceRequest>? _refExtractFilteredAlternatives;
    public IReadOnlyList<ResourceRequest> RefExtractFilteredAlternatives =>
        _refExtractFilteredAlternatives ??=
        [
            new(Cpu: RefExtractCpuFilteredThreads),
            new(Cpu: RefExtractGpuFilteredThreads, Nvdec: 1),
        ];

    /// <summary>Picks the right ref-extract cost shape for the given restore
    /// decision.  Progressive sources (no filter) get the smaller budget;
    /// IVTC / Deinterlace runs pay for the CPU filter that sits in the chain.</summary>
    public IReadOnlyList<ResourceRequest> RefExtractAlternativesFor(RestoreDecision restore) =>
        string.IsNullOrWhiteSpace(restore.FilterGraph)
            ? RefExtractProgressiveAlternatives
            : RefExtractFilteredAlternatives;

    public ResourceRequest PreviewRequest        => new(Cpu: PreviewThreads);
    public ResourceRequest SampleEncodeRequest   => new(Cpu: SampleEncodeThreads, Nvenc: 1);
    public ResourceRequest VmafRequest           => new(Cpu: VmafThreads, Cuda: 1);

    public ResourceRequest FinalEncodeProgressiveRequest =>
        new(Cpu: FinalEncodeProgressiveThreads, Nvenc: 1, Nvdec: 1);
    public ResourceRequest FinalEncodeFilteredRequest    =>
        new(Cpu: FinalEncodeFilteredThreads, Nvenc: 1, Nvdec: 1);

    /// <summary>Picks the right final-encode cost shape for the given restore
    /// decision.  Pure-progressive encodes are NVDEC→NVENC end-to-end with
    /// near-zero CPU; filtered encodes pay for the CPU filter (fieldmatch+
    /// decimate or bwdif) that sits between decode and encode.</summary>
    public ResourceRequest FinalEncodeRequestFor(RestoreDecision restore) =>
        string.IsNullOrWhiteSpace(restore.FilterGraph)
            ? FinalEncodeProgressiveRequest
            : FinalEncodeFilteredRequest;

    public ResourceRequest SizeGuardRemuxRequest => new(Cpu: 1);

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
    // grade quality (mean ≥ 99).

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
