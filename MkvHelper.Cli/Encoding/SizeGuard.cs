namespace MkvHelper;

/// <summary>
/// Outcome of the size-guard check after a final encode.
/// </summary>
public sealed class SizeGuardOutcome
{
    /// <summary>True when the AV1 encode was discarded and replaced with a stream-copy passthrough.</summary>
    public bool FellBack { get; init; }

    /// <summary>Path of the file the user actually keeps (AV1 encode or passthrough).</summary>
    public string OutputPath { get; init; } = "";

    public long InputBytes { get; init; }
    public long EncodedBytes { get; init; }
    public long? PassthroughBytes { get; init; }

    /// <summary>Human-readable explanation of the decision (kept verbatim in log.json).</summary>
    public string Reason { get; init; } = "";
}

/// <summary>
/// Trust-but-verify check after the final encode: if AV1 came out larger
/// than the source, the source is already efficiently compressed and the
/// extra bits are paying to faithfully reproduce existing artifacts.
/// Fall back to a stream-copy passthrough so the user always gets the
/// smaller of the two files.
///
/// Only fires for Progressive sources.  For IVTC/Deinterlace, the encode
/// is structurally different from the source (different framerate,
/// deinterlaced) and passthrough would lose the user's intended
/// restoration — log a warning instead.
/// </summary>
public static class SizeGuard
{
    public static async Task<SizeGuardOutcome> MaybeFallbackAsync(
        Config cfg, ResourcePool pool, string input, string encodedOutput, RestoreDecision restore,
        IPipelineLogger logger, CancellationToken ct)
    {
        long inputBytes = new FileInfo(input).Length;
        long encodedBytes = new FileInfo(encodedOutput).Length;

        if (encodedBytes < inputBytes)
        {
            double saved = 100.0 * (1.0 - (double)encodedBytes / inputBytes);
            logger.LogInfo(
                $"Size guard: encode {FormatBytes(encodedBytes)} vs source {FormatBytes(inputBytes)} " +
                $"({saved:F1}% smaller) — kept.");
            return new SizeGuardOutcome
            {
                FellBack = false,
                OutputPath = encodedOutput,
                InputBytes = inputBytes,
                EncodedBytes = encodedBytes,
                Reason = $"AV1 encode saved {saved:F1}% vs source — kept.",
            };
        }

        double bloat = 100.0 * ((double)encodedBytes / inputBytes - 1.0);

        if (restore.Mode != RestoreMode.None)
        {
            // IVTC/Deinterlace produced a structurally-different file (different
            // fps, deinterlaced).  A stream-copy passthrough would silently undo
            // the user's intended restoration, so we keep the encode and warn.
            logger.LogWarning(
                $"Size guard: AV1 encode is {bloat:+0.0;-0.0;0.0}% LARGER than source " +
                $"({FormatBytes(encodedBytes)} vs {FormatBytes(inputBytes)}).  " +
                $"Restoration ({restore.Mode}) was applied — passthrough would lose " +
                "that, so the encode is kept.  Source may already be efficiently compressed.");
            return new SizeGuardOutcome
            {
                FellBack = false,
                OutputPath = encodedOutput,
                InputBytes = inputBytes,
                EncodedBytes = encodedBytes,
                Reason = $"AV1 encode +{bloat:F1}% larger but restoration applied — kept.",
            };
        }

        // Progressive source + AV1 encode larger than source → passthrough.
        string dir = Path.GetDirectoryName(encodedOutput) ?? ".";
        string id = Path.GetFileName(dir);
        string passthroughOut = Path.Combine(dir, $"{id}_passthrough{cfg.OutputExtension}");

        logger.LogWarning(
            $"Size guard: AV1 encode is {bloat:+0.0;-0.0;0.0}% LARGER than source " +
            $"({FormatBytes(encodedBytes)} vs {FormatBytes(inputBytes)}).  " +
            "Source is already efficiently compressed; falling back to stream-copy passthrough.");

        try
        {
            // Pool gate only here: file-size comparison above is instant,
            // but the remux (-c copy) holds a CPU slice while ffmpeg runs.
            using (await pool.AcquireAsync(
                cfg.SizeGuardRemuxRequest, ct, file: logger.VideoId, op: "size-guard-remux"))
                await RemuxPassthroughAsync(cfg, input, passthroughOut, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                $"Size guard: passthrough remux failed ({ex.Message.Trim()}).  " +
                "Keeping AV1 encode despite size.");
            return new SizeGuardOutcome
            {
                FellBack = false,
                OutputPath = encodedOutput,
                InputBytes = inputBytes,
                EncodedBytes = encodedBytes,
                Reason = $"AV1 encode +{bloat:F1}% but passthrough remux failed — kept.",
            };
        }

        long passthroughBytes = new FileInfo(passthroughOut).Length;

        // Cover the (rare) case where the remux container overhead actually
        // makes the passthrough bigger than the source too.  Pick whichever
        // of {encode, passthrough} is smallest.
        if (passthroughBytes >= encodedBytes)
        {
            File.Delete(passthroughOut);
            logger.LogWarning(
                $"Size guard: passthrough ({FormatBytes(passthroughBytes)}) is no smaller " +
                $"than encode ({FormatBytes(encodedBytes)}) — keeping encode.");
            return new SizeGuardOutcome
            {
                FellBack = false,
                OutputPath = encodedOutput,
                InputBytes = inputBytes,
                EncodedBytes = encodedBytes,
                PassthroughBytes = passthroughBytes,
                Reason = $"Passthrough ({FormatBytes(passthroughBytes)}) ≥ encode ({FormatBytes(encodedBytes)}) — kept encode.",
            };
        }

        File.Delete(encodedOutput);
        double savedVsSource = 100.0 * (1.0 - (double)passthroughBytes / inputBytes);
        logger.LogInfo(
            $"Passthrough written: {passthroughOut} ({FormatBytes(passthroughBytes)}, " +
            $"{savedVsSource:+0.0;-0.0;0.0}% vs source).");

        return new SizeGuardOutcome
        {
            FellBack = true,
            OutputPath = passthroughOut,
            InputBytes = inputBytes,
            EncodedBytes = encodedBytes,
            PassthroughBytes = passthroughBytes,
            Reason = $"AV1 encode +{bloat:F1}% — fell back to stream-copy passthrough.",
        };
    }

    private static async Task RemuxPassthroughAsync(
        Config cfg, string input, string output, CancellationToken ct)
    {
        if (File.Exists(output)) File.Delete(output);
        var args = new[]
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", input,
            "-map", "0",
            "-c", "copy",
            output
        };
        var (code, _, err) = await ContainerTools.RunFfmpegAsync(args, ct);
        if (code != 0)
            throw new InvalidOperationException(err);
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F2} GiB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MiB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KiB";
        return $"{bytes} B";
    }
}
