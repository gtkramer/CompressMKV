using System.Diagnostics;
using System.Globalization;

namespace MkvHelper;

/// <summary>
/// Final encode orchestration.  Declares CPU + NVENC + NVDEC needs to the
/// pool; the encode itself is mostly GPU (NVDEC→NVENC) with light CPU
/// orchestration in the Progressive case or heavier CPU work when an
/// IVTC/Deint filter sits in the middle.  Two cost shapes — progressive
/// and filtered — are selected by <see cref="Config.FinalEncodeRequestFor"/>
/// so progressive encodes release their unused CPU cores back to the pool.
/// </summary>
public static class FinalEncoder
{
    public static async Task EncodeAsync(
        Config cfg, ResourcePool pool, string input, string output,
        RestoreDecision restore, int cq, PipelineFormat format,
        CancellationToken ct, IPipelineLogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (File.Exists(output)) File.Delete(output);

        logger.SetStage("Final encode", $"CQ={cq} ({format})");
        logger.LogInfo($"Final encode: CQ={cq}, {format}.");

        ResourceRequest request = cfg.FinalEncodeRequestFor(restore);
        AcquireResult admit = await pool.AcquireAnyAsync(
            [request], ct, file: logger.VideoId, op: "final-encode");
        Stopwatch holdSw = Stopwatch.StartNew();
        using (admit.Lease)
        {
            logger.LogInfo(
                $"Final encode acquired: requested CPU:{request.Cpu} NVENC:{request.Nvenc} " +
                $"NVDEC:{request.Nvdec}, waited {admit.WaitMs}ms, pool now " +
                $"{CompressCommand.FormatPool(pool.Snapshot(), pool)}.");
            await Pipelines.EncodeFullNvencAsync(
                cfg, input, output, restore, cq, format, request.Cpu, ct, logger);
        }
        holdSw.Stop();
        int holdMs = (int)holdSw.ElapsedMilliseconds;
        logger.RecordOp("final-encode", admit.Granted, admit.WaitMs, holdMs);
        logger.LogInfo($"Final encode released: held resources for {holdSw.Elapsed.TotalSeconds:F1}s.");

        // Trust-but-verify the output stream characteristics — ffprobe is a
        // light read with no pool gate, and confirming "output is 24000/1001
        // fps as expected" closes the loop on the IVTC chain's rate pin.
        // Best-effort: failure here is logged, not fatal.
        try
        {
            FfprobeRoot probe = await Ffprobe.RunAsync(cfg, output, ct);
            LogOutputStats(logger, probe);
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Output ffprobe failed: {ex.Message}");
        }
    }

    private static void LogOutputStats(IPipelineLogger logger, FfprobeRoot probe)
    {
        FfprobeStream? vstream = probe.Streams?.FirstOrDefault(s => s.IsActualVideo());
        double durationSec = probe.Format?.DurationSeconds ?? 0;
        long bitrateBps = 0;
        if (probe.Format?.BitRate is { } br &&
            long.TryParse(br, NumberStyles.Integer, CultureInfo.InvariantCulture, out long b))
            bitrateBps = b;
        long sizeBytes = 0;
        if (probe.Format?.Size is { } sz &&
            long.TryParse(sz, NumberStyles.Integer, CultureInfo.InvariantCulture, out long s))
            sizeBytes = s;
        Fps? fps = vstream?.ResolveFps();
        string fpsStr = fps?.ToString() ?? "?";
        string codec = vstream?.CodecName ?? "?";
        string res = vstream is null ? "?" : $"{vstream.Width}x{vstream.Height}";

        // Single-line summary covering everything the user wants to confirm
        // about the output: codec/resolution, fps (the IVTC rate-pin check),
        // duration, bitrate, and file size.
        logger.LogInfo(
            $"Output stream: {codec} {res} @ {fpsStr} fps, " +
            $"duration {FormatDuration(durationSec)}, " +
            $"bitrate {FormatBitrate(bitrateBps)}, " +
            $"file {FormatBytes(sizeBytes)}.");
    }

    private static string FormatDuration(double sec)
    {
        if (sec <= 0) return "?";
        int total = (int)Math.Round(sec);
        return $"{total / 3600}:{(total % 3600) / 60:D2}:{total % 60:D2}";
    }

    private static string FormatBitrate(long bps)
    {
        if (bps <= 0) return "?";
        if (bps >= 1_000_000) return $"{bps / 1_000_000.0:F2} Mbps";
        if (bps >= 1_000)     return $"{bps / 1_000.0:F1} kbps";
        return $"{bps} bps";
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "?";
        if (bytes >= 1L << 30) return $"{bytes / (double)(1L << 30):F2} GiB";
        if (bytes >= 1L << 20) return $"{bytes / (double)(1L << 20):F1} MiB";
        if (bytes >= 1L << 10) return $"{bytes / (double)(1L << 10):F0} KiB";
        return $"{bytes} B";
    }
}
