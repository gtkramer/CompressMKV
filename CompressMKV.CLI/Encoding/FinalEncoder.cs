namespace CompressMkv;

/// <summary>
/// Final encode orchestration.
/// </summary>
public static class FinalEncoder
{
    public static async Task EncodeAsync(
        Config cfg, GpuGate gpu, string input, string output,
        RestoreDecision restore, int cq, PipelineFormat format,
        CancellationToken ct, IPipelineLogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        if (File.Exists(output)) File.Delete(output);

        logger.SetStage("Final encode", $"CQ={cq} ({format})");
        logger.LogInfo($"Final encode: CQ={cq}, {format}.");

        using (await gpu.AcquireAsync(nvenc: 1, nvdec: cfg.UseNvdecForEncode ? 1 : 0, cuda: 0, ct))
        {
            await Pipelines.EncodeFullNvencAsync(cfg, input, output, restore, cq, format, ct, logger);
        }
    }
}
