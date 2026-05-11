namespace MkvHelper;

/// <summary>
/// Thin wrapper around the MKVToolNix binaries (<c>mkvextract</c>,
/// <c>mkvmerge</c>) used by the <c>split</c> and <c>print-chapters</c>
/// subcommands.  Goes through <see cref="Proc.RunAsync"/> rather than the
/// container, since these tools aren't part of the Netflix/vmaf bundle —
/// they run natively on the host.
/// </summary>
public static class MkvTools
{
    public const string Mkvextract = "mkvextract";
    public const string Mkvmerge = "mkvmerge";

    /// <summary>
    /// Run <c>mkvextract chapters &lt;input&gt;</c> and parse the resulting
    /// XML.  Throws <see cref="InvalidOperationException"/> if mkvextract
    /// fails or the input has no chapters.
    /// </summary>
    public static async Task<Chapters> ExtractChaptersAsync(string inputFile, CancellationToken ct)
    {
        var (code, stdout, stderr) = await Proc.RunAsync(
            Mkvextract, ["chapters", inputFile], ct);
        if (code != 0)
            throw new InvalidOperationException(
                $"mkvextract chapters failed (exit {code}): {stderr.Trim()}");

        if (string.IsNullOrWhiteSpace(stdout))
            throw new InvalidOperationException(
                $"mkvextract returned no chapter data for '{inputFile}'.  " +
                "The file may not contain a chapter track.");

        return ChapterSerializer.DeserializeXmlToChapters(stdout);
    }

    /// <summary>
    /// Run <c>mkvmerge --output &lt;out&gt; --split parts:&lt;a&gt;-&lt;b&gt;
    /// --chapters &lt;tmp&gt; --no-chapters &lt;in&gt;</c> to extract one
    /// episode-sized slice with custom chapters.  The chapter file is
    /// written to a temp path and removed afterward (in a finally so it
    /// doesn't leak on cancellation).
    /// </summary>
    public static async Task SplitWithChaptersAsync(
        string inputFile, string startTimestamp, string endTimestamp,
        Chapters chapters, string outputFile, CancellationToken ct)
    {
        string chapterFile = Path.GetTempFileName();
        try
        {
            // UTF-8 encoding matches the declaration written by ChapterSerializer.
            await File.WriteAllTextAsync(chapterFile,
                ChapterSerializer.SerializeChaptersToXml(chapters), ct);

            var (code, _, stderr) = await Proc.RunAsync(Mkvmerge, [
                "--output", outputFile,
                "--split", $"parts:{startTimestamp}-{endTimestamp}",
                "--chapters", chapterFile,
                "--no-chapters", inputFile,
            ], ct);
            if (code != 0)
                throw new InvalidOperationException(
                    $"mkvmerge failed (exit {code}) writing {outputFile}: {stderr.Trim()}");
        }
        finally
        {
            try { if (File.Exists(chapterFile)) File.Delete(chapterFile); }
            catch { /* best-effort */ }
        }
    }

    public static async Task<bool> IsMkvextractAvailableAsync(CancellationToken ct)
    {
        try
        {
            var (code, _, _) = await Proc.RunAsync(Mkvextract, ["--version"], ct);
            return code == 0;
        }
        catch { return false; }
    }

    public static async Task<bool> IsMkvmergeAvailableAsync(CancellationToken ct)
    {
        try
        {
            var (code, _, _) = await Proc.RunAsync(Mkvmerge, ["--version"], ct);
            return code == 0;
        }
        catch { return false; }
    }
}
