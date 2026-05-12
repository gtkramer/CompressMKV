using Serilog;
using Serilog.Core;
using Serilog.Formatting.Compact;

namespace MkvHelper;

/// <summary>
/// Builds the Serilog loggers used by the pipeline.  Two scopes:
///
///   <see cref="BuildGlobalLogger"/>: assigned to <see cref="Log.Logger"/> at
///   run start.  Receives every event from every file plus all run-wide events
///   (pool acquires/releases, system-utilization samples).  Two sinks share it:
///
///     • <c>events.jsonl</c> — JSON-lines via <see cref="CompactJsonFormatter"/>.
///       Canonical machine-readable source for after-the-fact AI analysis:
///       every event is one self-contained JSON object with structured
///       properties (file, op, level, message template, captured fields).
///
///     • <c>events.log</c> — human-readable plain text mirror.  Same events,
///       same chronological order, rendered with the message template
///       interpolated into the line.
///
///   <see cref="BuildPerFileLogger"/>: created once per input file.  Writes
///   only to that file's <c>decisions.log</c> (plain text).  Same events that
///   flow through here also flow to the global logger via
///   <see cref="PerFileLogger"/>'s dual-forward, but the per-file file makes
///   it cheap to read the chronological record for one input without grepping
///   the run-wide stream.
///
/// Both sinks are async-wrapped (background thread + bounded queue) so the
/// pipeline code never blocks on disk I/O.  Buffered writes are disabled so
/// crashes don't lose recent events.
/// </summary>
public static class LoggerSetup
{
    /// <summary>Text-sink layout used by both the global text mirror and the
    /// per-file decisions.log.  File/Op default to "—" so non-pool events
    /// (the bulk of the stream) don't render an empty pair of slashes.</summary>
    const string TextOutputTemplate =
        "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    /// <summary>
    /// Build and assign the global logger.  Call once at run start; dispose
    /// at run end (returned <see cref="Logger"/> implements <see cref="IDisposable"/>
    /// which flushes both async sinks).
    /// </summary>
    public static Logger BuildGlobalLogger(string outputFolder)
    {
        Directory.CreateDirectory(outputFolder);
        string jsonlPath = Path.Combine(outputFolder, "events.jsonl");
        string textPath  = Path.Combine(outputFolder, "events.log");

        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.File(
                formatter: new CompactJsonFormatter(),
                path: jsonlPath,
                buffered: false,
                shared: false,
                rollOnFileSizeLimit: false))
            .WriteTo.Async(a => a.File(
                path: textPath,
                outputTemplate: TextOutputTemplate,
                buffered: false,
                shared: false,
                rollOnFileSizeLimit: false))
            .CreateLogger();
    }

    /// <summary>
    /// Build a logger that writes only to the given per-file path.  Caller
    /// owns the lifetime — dispose at end of file processing.
    /// </summary>
    public static Logger BuildPerFileLogger(string decisionsLogPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(decisionsLogPath)!);
        return new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Async(a => a.File(
                path: decisionsLogPath,
                outputTemplate: TextOutputTemplate,
                buffered: false,
                shared: false,
                rollOnFileSizeLimit: false))
            .CreateLogger();
    }
}
