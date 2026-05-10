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

    // ---- Source frame rate (used to identify NTSC family) ----

    public double? SourceFps { get; set; }
    public bool IsNtscFamilyFps { get; set; }
}
