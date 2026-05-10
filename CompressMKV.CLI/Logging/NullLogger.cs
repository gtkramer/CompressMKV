namespace CompressMkv;

/// <summary>
/// No-op logger.  Used as a default when callers don't supply one (tests,
/// programmatic use, development scripts) so pipeline methods don't have to
/// null-check at every call site.
/// </summary>
public sealed class NullLogger : IPipelineLogger
{
    public static readonly NullLogger Instance = new();
    private NullLogger() { }

    public void LogInfo(string message) { }
    public void LogWarning(string message) { }
    public void LogError(string message) { }
    public void SetStage(string stage, string? detail = null) { }
}
