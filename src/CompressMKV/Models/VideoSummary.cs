namespace CompressMkv;

public sealed class VideoSummary
{
    public string VideoId { get; set; } = "";
    public string InputPath { get; set; } = "";
    public string FinalOutputPath { get; set; } = "";
    public DateTime GeneratedUtc { get; set; }

    public FfprobeRoot? Probe { get; set; }
    public SourceType SourceType { get; set; }
    public bool IsHdr { get; set; }

    public ContentDetectionResult? ContentDetection { get; set; }
    public RestoreDecision? Restore { get; set; }
    public TuningResult? Tuning { get; set; }

    public int FinalCq { get; set; }
}
