namespace CompressMkv;

/// <summary>
/// Result of single-pass full-file per-frame content type detection.
/// Captures everything needed to map onto one of the five MPlayer guide §7.2.2
/// categories and to choose the appropriate restoration filter chain.
/// </summary>
public sealed class ContentDetectionResult
{
    public ContentType ContentType { get; set; }
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";

    // ---- Per-frame counts from idet ----

    public int TotalFramesAnalyzed { get; set; }
    public int ProgressiveFrameCount { get; set; }
    public int InterlacedFrameCount { get; set; }
    public int UndeterminedFrameCount { get; set; }

    /// <summary>
    /// Progressive / (progressive + interlaced).  Excludes undetermined frames so
    /// the value reflects the actual content mix rather than idet's blind spots.
    /// 1.0 = pure progressive, 0.0 = pure interlaced, ~0.6 = telecine or mixed.
    /// </summary>
    public double GlobalProgressiveFraction { get; set; }

    // ---- Cadence metrics (used to disambiguate the four non-progressive categories) ----

    /// <summary>
    /// Fraction of 5-frame sliding windows containing exactly 3P + 2I — the 3:2
    /// pulldown signature (PPPII).  High (≥80%) = uniform telecine throughout
    /// the file (§7.2.2.2); moderate = some telecined sections; low = none.
    /// </summary>
    public double TelecineCadenceMatchRate { get; set; }

    /// <summary>
    /// Of all interlaced frames in the file, the fraction whose centered 5-frame
    /// neighborhood is exactly 3P + 2I.  This is the §7.2.2.4 vs §7.2.2.5
    /// discriminator: telecined I frames sit inside 3:2 cycles (high ratio);
    /// natively interlaced regions are clusters of consecutive I frames (low ratio).
    /// Zero when there are no interlaced frames.
    /// </summary>
    public double InterlacedFramesInCadenceRatio { get; set; }

    // ---- Parity ----

    public FieldParity DetectedParity { get; set; } = FieldParity.Auto;
    public long RawIdetTffCount { get; set; }
    public long RawIdetBffCount { get; set; }

    /// <summary>
    /// True when the parity decision was forced by the NTSC convention (TFF) rather
    /// than by clear idet dominance.  Useful for diagnostics — flips a flag in logs.
    /// </summary>
    public bool ParityFromNtscFallback { get; set; }

    // ---- ffprobe cross-check ----

    public string? FfprobeFieldOrder { get; set; }
    public FieldParity FfprobeMappedParity { get; set; } = FieldParity.Auto;
    public bool ParityMismatch { get; set; }

    // ---- Source frame rate metadata (used by §7.2.3 action gating) ----

    /// <summary>Source frame rate as an exact fraction.  Null when ffprobe
    /// reported no parseable rate.</summary>
    public Fps? SourceFps { get; set; }

    /// <summary>True when the source rate matches an NTSC family rate
    /// (30000/1001, 24000/1001, 60000/1001).  Used by the parity NTSC tiebreaker.</summary>
    public bool IsNtscFamilyFps { get; set; }

    /// <summary>True when r_frame_rate and avg_frame_rate agree closely.  False
    /// indicates a variable-frame-rate (VFR) source, where idet's cadence
    /// metrics and any fixed-rate output are unreliable — the IVTC chain is
    /// suppressed for VFR sources to prevent rate damage.</summary>
    public bool SourceIsLikelyCfr { get; set; } = true;

    // ---- idet aggregate cross-check ----
    //
    // idet's "Multi frame detection" line at end-of-stream gives ffmpeg's own
    // aggregate prog/int/TFF/BFF counts using the same multi-frame detector
    // mode we sample per-frame.  Cross-checking these against our streaming
    // counts validates the parser end-to-end — if ffmpeg's claim and our claim
    // disagree by more than rounding noise, our regex or stream handling has
    // a bug.

    /// <summary>True when idet emitted an aggregate line and the parsed counts
    /// matched our per-frame counts within tolerance.  False on disagreement
    /// (logged as a warning) or when no aggregate was emitted.</summary>
    public bool IdetAggregateAgrees { get; set; } = true;

    /// <summary>idet's aggregate progressive count from "Multi frame detection",
    /// or null if not parsed.</summary>
    public long? IdetAggregateProgressive { get; set; }

    /// <summary>idet's aggregate TFF count from "Multi frame detection",
    /// or null if not parsed.</summary>
    public long? IdetAggregateTff { get; set; }

    /// <summary>idet's aggregate BFF count from "Multi frame detection",
    /// or null if not parsed.</summary>
    public long? IdetAggregateBff { get; set; }

    /// <summary>idet's aggregate Undetermined count from "Multi frame detection",
    /// or null if not parsed.</summary>
    public long? IdetAggregateUndetermined { get; set; }
}
