namespace CompressMkv;

public sealed class OverallSummary
{
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public Config? Config { get; set; }
    public List<VideoSummary> Videos { get; set; } = new();
    public List<RunError> Errors { get; set; } = new();
}
