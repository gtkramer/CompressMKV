using Serilog;
using Serilog.Context;
using Serilog.Core;

namespace MkvHelper;

/// <summary>
/// Per-file pipeline logger.  Each input file gets its own instance for the
/// lifetime of its processing.  Three responsibilities:
///
///   1. Forward every event to that file's <c>decisions.log</c> (plain text)
///      via a dedicated Serilog instance — same crash-safe non-buffered async
///      sink as the global logger.
///
///   2. Forward the same event to <see cref="Log.Logger"/> (the global logger)
///      so it lands in <c>events.jsonl</c> + <c>events.log</c> for cross-file
///      analysis.  The <c>File</c> property is pushed once via
///      <see cref="LogContext"/> in the constructor and pops on
///      <see cref="Dispose"/>, so global-sink lines carry the input identity
///      without callers having to thread it manually.
///
///   3. Forward stage updates to the run-wide <see cref="StageReporter"/> for
///      the live terminal UI.
///
/// Thread-safety: Serilog's loggers are thread-safe; the StageReporter is
/// thread-safe.  LogContext.PushProperty uses AsyncLocal&lt;T&gt;, which flows
/// across awaits inside the same task so global-logger calls fired from any
/// continuation in the per-file pipeline still carry the right File property.
/// </summary>
public sealed class PerFileLogger : IPipelineLogger, IDisposable
{
    private readonly Logger _file;
    private readonly IDisposable _fileScope;
    private readonly StageReporter? _reporter;
    private readonly int _fileSlot;
    private bool _disposed;

    public string VideoId { get; }

    /// <summary>Warnings collected during the run, surfaced in the final summary.</summary>
    public List<string> Warnings { get; } = new();

    public PerFileLogger(string logPath, string videoId, StageReporter? reporter, int fileSlot)
    {
        _file = LoggerSetup.BuildPerFileLogger(logPath);
        _fileScope = LogContext.PushProperty("File", videoId);
        _reporter = reporter;
        _fileSlot = fileSlot;
        VideoId = videoId;

        _file.Information("Run started at {StartedUtc:O}", DateTime.UtcNow);
        Log.Logger.Information("Per-file run started at {StartedUtc:O}", DateTime.UtcNow);
    }

    public void LogInfo(string message)
    {
        _file.Information("{Message:l}", message);
        Log.Logger.Information("{Message:l}", message);
    }

    public void LogWarning(string message)
    {
        _file.Warning("{Message:l}", message);
        Log.Logger.Warning("{Message:l}", message);
        lock (Warnings) Warnings.Add(message);
    }

    public void LogError(string message)
    {
        _file.Error("{Message:l}", message);
        Log.Logger.Error("{Message:l}", message);
    }

    public void SetStage(string stage, string? detail = null)
    {
        // Mirror to both the per-file log and the global log so post-hoc readers
        // can see when each stage started.  Structured Stage/Detail properties
        // make stage transitions filterable in events.jsonl.
        if (detail is null)
        {
            _file.Information("STAGE: {Stage}", stage);
            Log.Logger.Information("Stage: {Stage}", stage);
        }
        else
        {
            _file.Information("STAGE: {Stage} — {Detail}", stage, detail);
            Log.Logger.Information("Stage: {Stage} — {Detail}", stage, detail);
        }
        _reporter?.SetStage(_fileSlot, stage, detail);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _file.Information("Run finished at {FinishedUtc:O}", DateTime.UtcNow);
        Log.Logger.Information("Per-file run finished at {FinishedUtc:O}", DateTime.UtcNow);
        _fileScope.Dispose();
        _file.Dispose();    // flushes the async file sink
    }
}
