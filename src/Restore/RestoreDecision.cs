namespace CompressMkv;

public sealed class RestoreDecision
{
    public RestoreMode Mode { get; set; }
    public double Confidence { get; set; }
    public string FilterGraph { get; set; } = "";
    public string? OutputFps { get; set; }
    public string Notes { get; set; } = "";
    public string DecisionReason { get; set; } = "";

    public ContentDetectionResult? ContentDetection { get; set; }
    public List<PreviewArtifact>? Previews { get; set; }
}
