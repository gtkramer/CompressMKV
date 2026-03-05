namespace CompressMkv;

/// <summary>
/// Result of the single-pass full-file per-frame content type detection pipeline.
/// </summary>
public sealed class ContentDetectionResult
{
    public ContentType ContentType { get; set; }
    public double Confidence { get; set; }
    public string Reason { get; set; } = "";

    // Frame statistics (from every decoded frame in the file)
    public int TotalFramesAnalyzed { get; set; }
    public int ProgressiveFrameCount { get; set; }
    public int InterlacedFrameCount { get; set; }
    public int UndeterminedFrameCount { get; set; }

    /// <summary>
    /// Progressive / (progressive + interlaced).  Excludes undetermined frames.
    /// ~1.0 = progressive, ~0.0 = interlaced, ~0.6 = telecine or mixed.
    /// </summary>
    public double GlobalProgressiveFraction { get; set; }

    /// <summary>
    /// Fraction of 5-frame sliding windows containing exactly 3 progressive + 2 interlaced
    /// frames — the signature of 3:2 pulldown (PPPII).  High (≥80%) = uniform telecine,
    /// moderate (10–80%) = mixed prog+telecine, low (&lt;10%) = no telecine.
    /// </summary>
    public double TelecineCadenceMatchRate { get; set; }

    // Parity detection (from full-file TFF vs BFF frame counts)
    public FieldParity DetectedParity { get; set; } = FieldParity.Auto;
    public long RawIdetTffCount { get; set; }
    public long RawIdetBffCount { get; set; }

    // ffprobe cross-check
    public string? FfprobeFieldOrder { get; set; }
    public FieldParity FfprobeMappedParity { get; set; } = FieldParity.Auto;
    public bool ParityMismatch { get; set; }
}
