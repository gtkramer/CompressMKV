using System.Globalization;

namespace CompressMkv;

/// <summary>
/// Production logger for a single file's pipeline run.  Two responsibilities:
///
///   1. Append every log line to <c>decisions.log</c> in the file's output
///      directory, with timestamp and level prefix.  This is the durable
///      chronological record — independent of the structured log.json.
///
///   2. Forward stage updates to the run-wide <see cref="StageReporter"/>,
///      which is responsible for repainting the live UI.
///
/// Thread-safe for concurrent writes from a single file's pipeline (the file
/// log is serialised on a per-instance lock; the StageReporter is itself
/// thread-safe for cross-file updates).
/// </summary>
public sealed class PerFileLogger : IPipelineLogger, IDisposable
{
    private readonly StreamWriter _file;
    private readonly object _fileLock = new();
    private readonly StageReporter? _reporter;
    private readonly int _fileSlot;
    private bool _disposed;

    /// <summary>Warnings collected during the run, surfaced in the final summary.</summary>
    public List<string> Warnings { get; } = new();

    public PerFileLogger(string logPath, StageReporter? reporter, int fileSlot)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        _file = new StreamWriter(logPath, append: true)
        {
            AutoFlush = true,    // crash-safe: every line is on disk before the next call returns
        };
        _reporter = reporter;
        _fileSlot = fileSlot;

        WriteLine("INFO", $"Run started at {DateTime.UtcNow:O}");
    }

    public void LogInfo(string message) => WriteLine("INFO", message);
    public void LogWarning(string message)
    {
        WriteLine("WARN", message);
        lock (Warnings) Warnings.Add(message);
    }
    public void LogError(string message) => WriteLine("ERROR", message);

    public void SetStage(string stage, string? detail = null)
    {
        // Mirror to the log so post-hoc readers can see when each stage started.
        WriteLine("STAGE", detail is null ? stage : $"{stage} — {detail}");
        _reporter?.SetStage(_fileSlot, stage, detail);
    }

    private void WriteLine(string level, string message)
    {
        var line = $"{DateTime.UtcNow:HH:mm:ss.fff} [{level}] {message}";
        lock (_fileLock)
        {
            _file.WriteLine(line);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_fileLock)
        {
            _file.WriteLine($"{DateTime.UtcNow:HH:mm:ss.fff} [INFO] Run finished at {DateTime.UtcNow:O}");
            _file.Dispose();
        }
    }
}
