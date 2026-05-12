using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MkvHelper;

public sealed class SplitSettings : CommandSettings
{
    [CommandOption("-i|--input <FILE>")]
    [Description("Input MKV file containing all the episodes back-to-back, with chapters.")]
    public string? InputFile { get; init; }

    [CommandOption("-n|--series-name <NAME>")]
    [Description("Series name used to build output filenames (e.g. \"My Show\" → \"My Show - S01E01.mkv\").")]
    public string? SeriesName { get; init; }

    [CommandOption("-t|--episode-chapter-threshold <SECONDS>")]
    [Description("Minimum chapter duration (seconds) for a chapter to count as \"main\" content.  Chapters under this duration are intros/transitions/credits.  Default 360 (6 minutes).")]
    [DefaultValue(360.0)]
    public double EpisodeChapterThreshold { get; init; }

    [CommandOption("-a|--additional-chapters <COUNT>")]
    [Description("Number of trailing chapters (credits, end card) to include after the last \"main\" chapter of each episode.  Default 2.")]
    [DefaultValue(2)]
    public int AdditionalChapters { get; init; }

    [CommandOption("-c|--start-chapter <INDEX>")]
    [Description("1-indexed chapter at which to start scanning.  Use to skip a leading bonus feature.  Default 1.")]
    [DefaultValue(1)]
    public int StartChapter { get; init; }

    [CommandOption("-s|--season-num <NUMBER>")]
    [Description("Season number used in output filenames.  Default 1.")]
    [DefaultValue(1)]
    public int SeasonNum { get; init; }

    [CommandOption("-e|--start-episode-num <NUMBER>")]
    [Description("Episode number for the first split-out file; subsequent episodes increment from this.  Default 1.")]
    [DefaultValue(1)]
    public int StartEpisodeNum { get; init; }

    public override ValidationResult Validate()
    {
        if (string.IsNullOrWhiteSpace(InputFile))
            return ValidationResult.Error("--input is required.");
        if (!File.Exists(InputFile))
            return ValidationResult.Error($"Input file does not exist: {InputFile}");
        if (string.IsNullOrWhiteSpace(SeriesName))
            return ValidationResult.Error("--series-name is required.");
        if (EpisodeChapterThreshold <= 0)
            return ValidationResult.Error("--episode-chapter-threshold must be > 0.");
        if (AdditionalChapters < 0)
            return ValidationResult.Error("--additional-chapters cannot be negative.");
        if (StartChapter < 1)
            return ValidationResult.Error("--start-chapter must be >= 1.");
        if (StartEpisodeNum < 1)
            return ValidationResult.Error("--start-episode-num must be >= 1.");
        return ValidationResult.Success();
    }
}

/// <summary>
/// Splits a multi-episode MKV (a "season disc") into one MKV per episode by
/// reading the source's chapter list, identifying main-content chapters via
/// a duration threshold, and slicing on those boundaries with mkvmerge.
///
/// Algorithm: walk the chapters, mark each as "main" if its duration meets
/// the threshold, then for each main → non-main transition record an
/// episode boundary at <c>i + additionalChapters</c> (so trailing credits
/// are bundled with the episode they follow).  The resulting (start, end)
/// chapter pairs become mkvmerge <c>--split parts:</c> windows.
/// </summary>
public sealed class SplitCommand : AsyncCommand<SplitSettings>
{
    protected override async Task<int> ExecuteAsync(
        CommandContext context, SplitSettings settings, CancellationToken token)
    {
        using CancellationTokenSource cts = ConsoleCancellation.LinkToConsole(token);

        // Required-field guarantees come from Validate(); these locals just
        // narrow nullability for the rest of the method.
        string inputFile = settings.InputFile!;
        string seriesName = settings.SeriesName!;

        // Ensure the dependency container is ready and mount the directory
        // that holds the input + the output episodes.  mkvextract/mkvmerge
        // run inside the container via ContainerTools.
        string mountDir = Path.GetDirectoryName(Path.GetFullPath(inputFile))
            ?? throw new InvalidOperationException($"Input file has no directory: {inputFile}");
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
            chapters = await MkvToolNixChapters.ExtractChaptersAsync(inputFile, cts.Token);
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Failed to read chapters:[/] {Markup.Escape(ex.Message)}");
            return 1;
        }

        List<ChapterAtom> allChapters = chapters.EditionEntry.ChapterAtoms;
        if (allChapters.Count == 0)
        {
            AnsiConsole.MarkupLine($"[yellow]No chapters found in {Markup.Escape(inputFile)} — nothing to split.[/]");
            return 1;
        }

        // Apply --start-chapter (1-indexed) by trimming the chapter list head.
        // Indices used hereafter are relative to the trimmed list, so the
        // mkvmerge output naturally excludes any chapters the user wanted to skip.
        int startIndex0 = settings.StartChapter - 1;
        if (startIndex0 >= allChapters.Count)
        {
            AnsiConsole.MarkupLine(
                $"[red]--start-chapter={settings.StartChapter} is past the end[/] " +
                $"(file has {allChapters.Count} chapters).");
            return 1;
        }
        List<ChapterAtom> chapterAtoms = allChapters.GetRange(startIndex0, allChapters.Count - startIndex0);

        // Phase 1: classify each chapter as main vs. non-main by duration.
        bool[] isMain = new bool[chapterAtoms.Count];
        for (int i = 0; i < chapterAtoms.Count; i++)
            isMain[i] = chapterAtoms[i].GetDurationSeconds() >= settings.EpisodeChapterThreshold;

        // Phase 2: walk for main → non-main transitions, recording episode
        // (start, end) chapter index pairs.  endIndex = transition + additionalChapters,
        // clamped to the last chapter to handle "the last episode has fewer
        // trailing chapters than additionalChapters" cleanly.
        List<(int Start, int End)> episodeRanges = [];
        int episodeStart = 0;
        for (int i = 0; i < chapterAtoms.Count; i++)
        {
            int j = i + 1;
            if (isMain[i] && j < chapterAtoms.Count && !isMain[j])
            {
                int episodeEnd = Math.Min(i + settings.AdditionalChapters, chapterAtoms.Count - 1);
                episodeRanges.Add((episodeStart, episodeEnd));
                episodeStart = episodeEnd + 1;
            }
        }

        // If the last episode has no trailing non-main chapters (i.e. the
        // file ends right after main content), the loop above won't capture
        // it.  Recover it explicitly: any unclaimed tail that contains main
        // content is its own final episode.
        if (episodeStart < chapterAtoms.Count && isMain.Skip(episodeStart).Any(x => x))
            episodeRanges.Add((episodeStart, chapterAtoms.Count - 1));

        if (episodeRanges.Count == 0)
        {
            AnsiConsole.MarkupLine(
                $"[yellow]No episodes detected[/] (no main → non-main transitions found above " +
                $"threshold {settings.EpisodeChapterThreshold:F0}s).  " +
                "Try lowering --episode-chapter-threshold.");
            return 1;
        }

        AnsiConsole.MarkupLine(
            $"[bold]{episodeRanges.Count} episode(s) detected[/] " +
            $"in {Markup.Escape(Path.GetFileName(inputFile))} " +
            $"(threshold: {settings.EpisodeChapterThreshold:F0}s, additional chapters: {settings.AdditionalChapters}).");

        string inputDir = Path.GetDirectoryName(inputFile) is { Length: > 0 } d ? d : ".";

        int written = 0;
        await AnsiConsole.Status()
            .AutoRefresh(true)
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Preparing...", async ctx =>
            {
                for (int i = 0; i < episodeRanges.Count; i++)
                {
                    cts.Token.ThrowIfCancellationRequested();
                    (int rangeStart, int rangeEnd) = episodeRanges[i];

                    int episodeNum = settings.StartEpisodeNum + i;
                    string fileName = $"{seriesName} - S{settings.SeasonNum:D2}E{episodeNum:D2}.mkv";
                    string outputPath = Path.Combine(inputDir, fileName);

                    ctx.Status($"Writing {Markup.Escape(fileName)} ({i + 1}/{episodeRanges.Count})...");

                    Chapters renumbered = BuildEpisodeChapters(chapterAtoms, rangeStart, rangeEnd);
                    string startTs = chapterAtoms[rangeStart].ChapterTimeStart;
                    string endTs = chapterAtoms[rangeEnd].ChapterTimeEnd;

                    await MkvToolNixChapters.SplitWithChaptersAsync(
                        inputFile, startTs, endTs, renumbered, outputPath, cts.Token);
                    written++;
                }
            });

        AnsiConsole.MarkupLine($"[green]Wrote {written} episode file(s) to[/] {Markup.Escape(inputDir)}");
        return 0;
    }

    /// <summary>
    /// Slice the source chapter list to one episode's range and renumber the
    /// chapter labels to "Chapter 1", "Chapter 2", … so each output file has
    /// a clean, episode-local chapter view in players.
    /// </summary>
    private static Chapters BuildEpisodeChapters(List<ChapterAtom> source, int startIdx, int endIdx)
    {
        int count = endIdx - startIdx + 1;
        List<ChapterAtom> sliced = source.GetRange(startIdx, count);
        for (int i = 0; i < sliced.Count; i++)
        {
            sliced[i].ChapterDisplay.ChapterString = $"Chapter {i + 1}";
            sliced[i].ChapterDisplay.ChapterLanguage = "en";  // IETF BCP 47
        }
        return new Chapters { EditionEntry = new EditionEntry { ChapterAtoms = sliced } };
    }
}
