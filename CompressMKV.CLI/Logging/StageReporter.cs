using System.Diagnostics;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace CompressMkv;

/// <summary>
/// Run-wide live status display.  Renders an overall progress bar and a
/// per-file activity table that updates in place using Spectre.Console.
///
/// Layout:
///   ┌─ CompressMKV — 47 files ──────────────────────────────────────┐
///   │ Overall: [#####################                ]  53% (24/47) │
///   │                                                                │
///   │ Active                                                         │
///   │ ─────────────────────────────────────────────────────────────  │
///   │ [25] big_buck_bunny.mkv         Tuning   CQ=30 sample 4/16     │
///   │ [26] sintel_2160p.mkv           Phase 1  Extracting refs 12/16 │
///   │                                                                │
///   │ Recent                                                         │
///   │ ─────────────────────────────────────────────────────────────  │
///   │ [23] avatar_disc.mkv          ✓ CQ=28   47:21                  │
///   │ [22] dune_part2.mkv           ⚠ CQ=24   89:14   marginal       │
///   └────────────────────────────────────────────────────────────────┘
///
/// A "slot" is an in-flight file's row in the active table.  Slots are
/// allocated when a file starts and recycled when it completes.  The
/// reporter is thread-safe for concurrent SetStage/Complete calls from
/// the per-file pipeline workers.
///
/// Falls back gracefully when the output isn't a TTY (Spectre detects this
/// and renders without ANSI codes), so piped output still works.
/// </summary>
public sealed class StageReporter
{
    private readonly object _lock = new();
    private readonly int _totalFiles;
    private int _completedFiles;
    private int _skippedFiles;

    private readonly Dictionary<int, ActiveSlot> _active = new();
    private readonly Queue<RecentEntry> _recent = new();
    private const int RecentCapacity = 8;

    public StageReporter(int totalFiles)
    {
        _totalFiles = totalFiles;
    }

    /// <summary>Total files to process (set at construction, immutable).</summary>
    public int TotalFiles => _totalFiles;

    // ---- Public update API (thread-safe) ----

    /// <summary>Begin tracking a file.  fileSlot is opaque — caller's choice.</summary>
    public void BeginFile(int fileSlot, int idx, string fileName)
    {
        lock (_lock)
        {
            _active[fileSlot] = new ActiveSlot(idx, fileName, Stopwatch.StartNew())
            {
                Stage = "Starting",
                Detail = "",
            };
        }
    }

    public void SetStage(int fileSlot, string stage, string? detail = null)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(fileSlot, out var slot))
            {
                slot.Stage = stage;
                slot.Detail = detail ?? "";
            }
        }
    }

    /// <summary>Mark a file done.  status is the icon/result phrase
    /// ("✓ CQ=28" / "⚠ CQ=24 marginal" / "✗ failed").</summary>
    public void CompleteFile(int fileSlot, string status, ResultLevel level)
    {
        lock (_lock)
        {
            if (_active.TryGetValue(fileSlot, out var slot))
            {
                _recent.Enqueue(new RecentEntry(
                    slot.Idx, slot.FileName, status, level, slot.Watch.Elapsed));
                while (_recent.Count > RecentCapacity) _recent.Dequeue();
                _active.Remove(fileSlot);
                _completedFiles++;
            }
        }
    }

    /// <summary>Mark a file as skipped (resume from prior log.json).</summary>
    public void SkipFile(int idx, string fileName, string reason)
    {
        lock (_lock)
        {
            _recent.Enqueue(new RecentEntry(idx, fileName,
                $"skipped — {reason}", ResultLevel.Skipped, TimeSpan.Zero));
            while (_recent.Count > RecentCapacity) _recent.Dequeue();
            _completedFiles++;
            _skippedFiles++;
        }
    }

    // ---- Rendering for Spectre.Console.Live ----

    /// <summary>
    /// Builds the renderable that Live() displays.  Called repeatedly by the
    /// background refresh loop; returns a fresh tree each time so updates land.
    /// </summary>
    public IRenderable BuildRenderable()
    {
        lock (_lock)
        {
            var grid = new Grid();
            grid.AddColumn();

            // Overall progress bar.
            int done = _completedFiles;
            int total = _totalFiles;
            double pct = total > 0 ? (double)done / total : 0;
            int barWidth = 40;
            int filled = (int)Math.Round(pct * barWidth);
            string bar = new string('#', filled) + new string(' ', barWidth - filled);
            string skipped = _skippedFiles > 0 ? $"  ({_skippedFiles} skipped via resume)" : "";

            // Spectre markup escapes `[` and `]` by doubling them.  The earlier
            // `\[` form looked like a C# escape but Spectre treats it as a real
            // backslash followed by the opening of a new tag — which then ate
            // the bar's spaces and crashed with "Could not find color or style ''".
            grid.AddRow(new Markup(
                $"[bold]Overall:[/]  [green][[{bar}]][/]  [yellow]{pct:P0}[/]  ({done}/{total} complete){skipped}"));

            // Active table.
            grid.AddRow(new Markup(""));
            if (_active.Count > 0)
            {
                var activeTable = new Table()
                    .Border(TableBorder.Minimal)
                    .BorderColor(Color.Grey)
                    .Title("[bold]Active[/]")
                    .AddColumn("[grey]#[/]")
                    .AddColumn("[grey]File[/]")
                    .AddColumn("[grey]Stage[/]")
                    .AddColumn("[grey]Detail[/]")
                    .AddColumn("[grey]Elapsed[/]");

                foreach (var slot in _active.Values.OrderBy(s => s.Idx))
                {
                    activeTable.AddRow(
                        new Markup($"[dim]{slot.Idx}/{_totalFiles}[/]"),
                        new Markup(Markup.Escape(Truncate(slot.FileName, 40))),
                        new Markup($"[cyan]{Markup.Escape(slot.Stage)}[/]"),
                        new Markup(Markup.Escape(Truncate(slot.Detail, 40))),
                        new Markup($"[dim]{FormatElapsed(slot.Watch.Elapsed)}[/]"));
                }
                grid.AddRow(activeTable);
            }

            // Recent completions.
            if (_recent.Count > 0)
            {
                grid.AddRow(new Markup(""));
                var recentTable = new Table()
                    .Border(TableBorder.Minimal)
                    .BorderColor(Color.Grey)
                    .Title("[bold]Recent[/]")
                    .AddColumn("[grey]#[/]")
                    .AddColumn("[grey]File[/]")
                    .AddColumn("[grey]Result[/]")
                    .AddColumn("[grey]Time[/]");

                foreach (var entry in _recent)
                {
                    string colour = entry.Level switch
                    {
                        ResultLevel.Success => "green",
                        ResultLevel.Warning => "yellow",
                        ResultLevel.Failure => "red",
                        ResultLevel.Skipped => "grey",
                        _ => "white",
                    };
                    string elapsed = entry.Level == ResultLevel.Skipped
                        ? "—"
                        : FormatElapsed(entry.Elapsed);
                    recentTable.AddRow(
                        new Markup($"[dim]{entry.Idx}/{_totalFiles}[/]"),
                        new Markup(Markup.Escape(Truncate(entry.FileName, 40))),
                        new Markup($"[{colour}]{Markup.Escape(entry.Status)}[/]"),
                        new Markup($"[dim]{elapsed}[/]"));
                }
                grid.AddRow(recentTable);
            }

            return grid;
        }
    }

    private static string FormatElapsed(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
            : $"{t.Minutes:D2}:{t.Seconds:D2}";

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : "…" + s[^(max - 1)..];

    // ---- Internal types ----

    private sealed class ActiveSlot
    {
        public int Idx { get; }
        public string FileName { get; }
        public Stopwatch Watch { get; }
        public string Stage { get; set; } = "";
        public string Detail { get; set; } = "";
        public ActiveSlot(int idx, string fileName, Stopwatch watch)
        { Idx = idx; FileName = fileName; Watch = watch; }
    }

    private record RecentEntry(int Idx, string FileName, string Status, ResultLevel Level, TimeSpan Elapsed);
}

public enum ResultLevel { Success, Warning, Failure, Skipped }
