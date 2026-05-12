using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MkvHelper;

public sealed class PrintChaptersSettings : CommandSettings
{
    [CommandOption("-i|--input <FILE>")]
    [Description("Input MKV file with a chapter track.")]
    public string? InputFile { get; init; }

    [CommandOption("-t|--episode-chapter-threshold <SECONDS>")]
    [Description("Chapters with duration ≥ this many seconds are flagged as \"main\" content in the output.  Same semantics as `mkvhelper split`.  Default 360.")]
    [DefaultValue(360.0)]
    public double EpisodeChapterThreshold { get; init; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(InputFile))
            return ValidationResult.Error("--input is required.");
        if (!File.Exists(InputFile))
            return ValidationResult.Error($"Input file does not exist: {InputFile}");
        if (EpisodeChapterThreshold <= 0)
            return ValidationResult.Error("--episode-chapter-threshold must be > 0.");
        return ValidationResult.Success();
    }
}

/// <summary>
/// Prints the chapter list of an MKV file to the terminal as a table.
/// Useful as a dry run before <c>mkvhelper split</c> — it surfaces which
/// chapters the splitter would treat as "main" content under a given
/// threshold so you can pick a sensible value without actually slicing.
/// </summary>
public sealed class PrintChaptersCommand : AsyncCommand<PrintChaptersSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, PrintChaptersSettings settings, CancellationToken token)
    {
        using CancellationTokenSource cts = ConsoleCancellation.LinkToConsole(token);

        // Ensure the dependency container is ready and mount the directory
        // holding the input file.  mkvextract runs inside the container.
        string mountDir = Path.GetDirectoryName(Path.GetFullPath(settings.InputFile!))
            ?? throw new InvalidOperationException($"Input file has no directory: {settings.InputFile}");
        try
        {
            await ContainerBuilder.EnsureReadyAsync(mounts: [mountDir], ct: cts.Token);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Container setup failed:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        Chapters chapters;
        try
        {
            chapters = await MkvTools.ExtractChaptersAsync(settings.InputFile!, cts.Token);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to read chapters:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        List<ChapterAtom> atoms = chapters.EditionEntry.ChapterAtoms;
        if (atoms.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No chapters in {Markup.Escape(settings.InputFile!)}.[/]");
            return 0;
        }

        Table table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold]{Markup.Escape(Path.GetFileName(settings.InputFile!))}[/]  " +
                   $"({atoms.Count} chapters, threshold {settings.EpisodeChapterThreshold:F0}s)")
            .AddColumn("[grey]#[/]", c => c.RightAligned())
            .AddColumn("[grey]Title[/]")
            .AddColumn("[grey]Start[/]")
            .AddColumn("[grey]End[/]")
            .AddColumn("[grey]Duration[/]", c => c.RightAligned())
            .AddColumn("[grey]Main?[/]");

        int mainCount = 0;
        for (int i = 0; i < atoms.Count; i++)
        {
            ChapterAtom a = atoms[i];
            double dur = a.GetDurationSeconds();
            bool isMain = dur >= settings.EpisodeChapterThreshold;
            if (isMain) mainCount++;

            string title = string.IsNullOrEmpty(a.ChapterDisplay.ChapterString)
                ? "[dim](untitled)[/]"
                : Markup.Escape(a.ChapterDisplay.ChapterString);

            table.AddRow(
                new Markup($"[dim]{i + 1}[/]"),
                new Markup(title),
                new Markup($"[dim]{Markup.Escape(a.ChapterTimeStart)}[/]"),
                new Markup($"[dim]{Markup.Escape(a.ChapterTimeEnd)}[/]"),
                new Markup($"[dim]{FormatDuration(dur)}[/]"),
                new Markup(isMain ? "[green]main[/]" : "[grey]—[/]"));
        }

        AnsiConsole.Write(table);
        AnsiConsole.MarkupLine(
            $"[grey]{mainCount} of {atoms.Count} chapter(s) classified as main content " +
            $"at threshold {settings.EpisodeChapterThreshold:F0}s.[/]");
        return 0;
    }

    private static string FormatDuration(double seconds)
    {
        TimeSpan ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
