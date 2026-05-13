namespace MkvHelper;

public sealed class OverallSummary
{
    public DateTime StartedUtc { get; set; }
    public DateTime? FinishedUtc { get; set; }
    public Config? Config { get; set; }
    public List<VideoSummary> Videos { get; set; } = [];
    public List<RunError> Errors { get; set; } = [];

    /// <summary>
    /// Run-end aggregate from <see cref="SystemSampler"/>: average pool-busy %
    /// per resource alongside the real CPU/GPU/NVENC numbers from /proc and
    /// nvidia-smi.  Useful for spotting pool slot caps that are too tight
    /// (pool 90% busy / hardware idle = pool is the bottleneck, not silicon).
    /// Null when sampling was disabled (<see cref="Config.SystemSamplerIntervalSeconds"/> = 0).
    /// </summary>
    public SystemSamplerSummary? SamplerSummary { get; set; }
}
