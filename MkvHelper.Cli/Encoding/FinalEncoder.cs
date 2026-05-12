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
        IDisposable lease = await pool.AcquireAsync(request, ct, file: logger.VideoId, op: "final-encode");
        using (lease)
        {
            logger.LogInfo(
                $"Final encode acquired: requested CPU:{request.Cpu} NVENC:{request.Nvenc} " +
                $"NVDEC:{request.Nvdec}, pool now {CompressCommand.FormatPool(pool.Snapshot(), pool)}.");
            await Pipelines.EncodeFullNvencAsync(
                cfg, input, output, restore, cq, format, request.Cpu, ct, logger);
        }
    }
}
