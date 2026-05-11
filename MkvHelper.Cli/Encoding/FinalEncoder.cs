namespace MkvHelper;

/// <summary>
/// Final encode orchestration.  Declares CPU + NVENC + NVDEC needs to the
/// pool; the encode itself is mostly GPU (NVDEC→NVENC) with light CPU
/// orchestration in the Progressive case or heavier CPU work when an
/// IVTC/Deint filter sits in the middle.  One CPU budget covers both
/// cases — see <see cref="Config.FinalEncodeThreads"/>.
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

        using (await pool.AcquireAsync(cfg.FinalEncodeRequest, ct))
        {
            await Pipelines.EncodeFullNvencAsync(cfg, input, output, restore, cq, format, ct, logger);
        }
    }
}
