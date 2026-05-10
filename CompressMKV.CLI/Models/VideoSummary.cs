namespace CompressMkv;

public sealed class VideoSummary
{
    public string VideoId { get; set; } = "";
    public string InputPath { get; set; } = "";
    public string FinalOutputPath { get; set; } = "";
    public DateTime GeneratedUtc { get; set; }

    public FfprobeRoot? Probe { get; set; }
    public bool IsHdr { get; set; }

    public ContentDetectionResult? ContentDetection { get; set; }
    public RestoreDecision? Restore { get; set; }
    public TuningResult? Tuning { get; set; }

    public int FinalCq { get; set; }

    /// <summary>
    /// Trust-but-verify result of running idet on the final encoded output.
    /// Confirms the chosen restoration produced structurally-correct output
    /// (clean progressive, no residual cadence, expected fps).  Null when
    /// verification was skipped or failed to run.
    /// </summary>
    public OutputVerificationResult? OutputVerification { get; set; }

    /// <summary>
    /// Wall-clock time spent in each pipeline phase.  Used to verify the
    /// concurrency model produced the expected utilization profile.
    /// </summary>
    public PhaseTimings? Timings { get; set; }
}
