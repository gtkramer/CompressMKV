namespace MkvHelper;

/// <summary>
/// Wrappers for the MKVToolNix tools (<c>mkvextract</c>, <c>mkvmerge</c>)
/// used by the <c>split</c> and <c>print-chapters</c> subcommands.
/// Routes through <see cref="ContainerTools"/> — these tools live inside
/// our dependency container alongside ffmpeg/ffprobe, so the host doesn't
/// need a separate <c>mkvtoolnix-cli</c> install.
///
/// Caller is responsible for calling <see cref="ContainerBuilder.EnsureReadyAsync"/>
/// (which configures the container mounts) before invoking anything here.
/// </summary>
public static class MkvTools
{
    /// <summary>
    /// Run <c>mkvextract chapters &lt;input&gt;</c> and parse the resulting
    /// XML.  Throws <see cref="InvalidOperationException"/> if mkvextract
    /// fails or the input has no chapter track.
    /// </summary>
    public static async Task<Chapters> ExtractChaptersAsync(string inputFile, CancellationToken ct)
    {
        var (code, stdout, stderr) = await ContainerTools.RunMkvextractAsync(
            ["chapters", inputFile], ct);
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
    /// episode-sized slice with custom chapters.  The chapter file lives
    /// in the same directory as the output (which is bind-mounted into
    /// the container) and is removed in a finally so it doesn't leak on
    /// cancellation.
    /// </summary>
    public static async Task SplitWithChaptersAsync(
        string inputFile, string startTimestamp, string endTimestamp,
        Chapters chapters, string outputFile, CancellationToken ct)
    {
        // Place the temp chapter file inside the output directory so it
        // sits inside one of the container's bind mounts.  /tmp on the
        // host isn't mounted into the container.
        string outDir = Path.GetDirectoryName(outputFile)
            ?? throw new ArgumentException($"Output file has no directory: {outputFile}");
        string chapterFile = Path.Combine(outDir, $".mkvhelper-chapters-{Guid.NewGuid():N}.xml");

        try
        {
            // UTF-8 encoding matches the declaration written by ChapterSerializer.
            await File.WriteAllTextAsync(chapterFile,
                ChapterSerializer.SerializeChaptersToXml(chapters), ct);

            var (code, _, stderr) = await ContainerTools.RunMkvmergeAsync([
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
}
