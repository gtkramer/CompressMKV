using System.Threading;
using Spectre.Console;

namespace MkvHelper;

/// <summary>
/// Walks the input folder and identifies files containing real video content
/// by probing each candidate with ffprobe.  Extension-agnostic — accepts any
/// container ffmpeg can read, including weird ones like .rm/.rmvb/.flv/.vob
/// or files with no extension at all.
///
/// Probing is parallelised across the file list (bounded), so even directories
/// with hundreds of mixed-content files complete discovery in a couple of
/// seconds.  Files that ffprobe rejects, or that contain only audio / subtitles
/// / cover art / single-frame images, are filtered out.
/// </summary>
public static class VideoFileDiscovery
{
    /// <summary>
    /// Files smaller than this are skipped without probing — no real video file
    /// is this small, and skipping the ffprobe call avoids spending probe time
    /// on configs, manifest files, sidecars, etc.
    /// </summary>
    private const long MinSizeBytes = 10 * 1024;

    /// <summary>
    /// Maximum number of concurrent ffprobe calls during discovery.  Each ffprobe
    /// is short (~50ms typical, mostly stat + container header read) so a low
    /// degree of parallelism is plenty.
    /// </summary>
    private const int ProbeParallelism = 8;

    /// <summary>
    /// Enumerates the directory recursively, probes each non-trivial file, and
    /// returns those that contain real video content.  Each result includes the
    /// pre-computed <see cref="FfprobeRoot"/> so downstream code doesn't need
    /// to re-probe.
    /// </summary>
    public static async Task<List<DiscoveredVideo>> DiscoverAsync(
        Config cfg, string inputFolder, CancellationToken ct)
    {
        // Cheap pre-filter: enumerate all files, drop hidden + tiny ones.
        List<string> candidates = Directory.EnumerateFiles(inputFolder, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetFileName(f).StartsWith('.'))
            .Where(f =>
            {
                try { return new FileInfo(f).Length >= MinSizeBytes; }
                catch { return false; }
            })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0) return [];

        List<DiscoveredVideo> results = new(candidates.Count);
        Lock resultsLock = new();

        await AnsiConsole.Progress()
            .AutoClear(true)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn())
            .StartAsync(async progressCtx =>
            {
                ProgressTask task = progressCtx.AddTask(
                    $"[green]Probing {candidates.Count} candidate file(s)[/]",
                    maxValue: candidates.Count);

                using SemaphoreSlim sem = new(ProbeParallelism, ProbeParallelism);

                IEnumerable<Task> probeTasks = candidates.Select(async path =>
                {
                    await sem.WaitAsync(ct);
                    try
                    {
                        FfprobeRoot? probe = await ProbeQuietlyAsync(cfg, path, ct);
                        if (probe is not null && probe.HasUsableVideoContent())
                        {
                            lock (resultsLock)
                                results.Add(new DiscoveredVideo(path, probe));
                        }
                    }
                    finally
                    {
                        sem.Release();
                        task.Increment(1);
                    }
                });

                await Task.WhenAll(probeTasks);
            });

        // Sort the kept files for deterministic processing order.
        results.Sort((a, b) =>
            StringComparer.OrdinalIgnoreCase.Compare(a.Path, b.Path));
        return results;
    }

    /// <summary>
    /// Runs ffprobe and returns null on any failure.  Many candidates will be
    /// non-media files (manifests, configs, sidecars, ...) and ffprobe will
    /// refuse to parse them — we treat that as "not a video" rather than a
    /// hard error so discovery doesn't stop on the first oddball.
    /// </summary>
    private static async Task<FfprobeRoot?> ProbeQuietlyAsync(
        Config cfg, string path, CancellationToken ct)
    {
        try { return await Ffprobe.RunAsync(cfg, path, ct); }
        catch { return null; }
    }
}

/// <summary>
/// A file that passed video-content discovery, paired with its probe result so
/// downstream pipeline stages don't need to re-probe.
/// </summary>
public sealed record DiscoveredVideo(string Path, FfprobeRoot Probe);
