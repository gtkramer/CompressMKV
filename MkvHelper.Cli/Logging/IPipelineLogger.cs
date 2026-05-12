namespace MkvHelper;

/// <summary>
/// Logger threaded through every per-file pipeline operation.  Two channels:
///
///   Log channel — chronological, written to a per-file decisions.log text
///   file alongside log.json.  Captures every decision, warning, and event
///   for after-the-fact inspection.  Includes things like fieldmatch warning
///   lines, parser-disagreement notices, adaptive-sampling events, etc.
///   that previously only existed in the scrolled-past console output.
///
///   Stage channel — current activity for the live UI.  Updates the row
///   shown for this file in the live status table; not persisted.
///
/// Implementations:
///   <see cref="NullLogger"/>      — no-op, used by tests.
///   <see cref="PerFileLogger"/>   — writes log to disk + forwards stage to
///                                    the run-wide <see cref="StageReporter"/>.
/// </summary>
public interface IPipelineLogger
{
    /// <summary>
    /// Stable identifier for the input file this logger belongs to (the same
    /// <c>SafeId</c>-d basename used for the output sub-directory).  Used to
    /// tag <see cref="ResourcePool"/> acquire events with the originating
    /// file so cross-file analysis can attribute wait times.  Empty string
    /// on loggers that aren't file-scoped (e.g. <see cref="NullLogger"/>).
    /// </summary>
    string VideoId { get; }

    /// <summary>Append an informational line to the per-file decisions.log.</summary>
    void LogInfo(string message);

    /// <summary>Append a warning line.  Surfaced in the run summary at the end.</summary>
    void LogWarning(string message);

    /// <summary>Append an error line.  Halts the file's processing if thrown.</summary>
    void LogError(string message);

    /// <summary>
    /// Update the file's live-UI row.  Stage is the broad phase ("Detect",
    /// "Phase 1", "Tuning", "Final encode", "Verify").  Detail is the
    /// fine-grained progress within that stage ("CQ=30 sample 4/16",
    /// "Extracting refs 12/16").
    /// </summary>
    void SetStage(string stage, string? detail = null);
}
