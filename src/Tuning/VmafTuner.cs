using System.Globalization;

namespace CompressMkv;

/// <summary>
/// VMAF tuning (HDR CQ ladder shift applied here).
/// </summary>
public static class VmafTuner
{
    public static async Task<TuningResult> TuneAsync(Config cfg, GpuGate gpu, string input, string outDir, RestoreDecision restore, bool isHdr, CancellationToken ct)
    {
        var probe = await Ffprobe.RunAsync(cfg, input, ct);
        var dur = probe.Format?.DurationSeconds ?? throw new InvalidOperationException("No duration.");

        var sampler = new Sampler(cfg.RandomSeed);
        var windows = sampler.RandomWindows(dur, cfg.SampleCount, cfg.SampleWindowSeconds);

        var samplesDir = Path.Combine(outDir, "samples");
        var vmafDir = Path.Combine(outDir, "vmaf");
        Directory.CreateDirectory(samplesDir);
        Directory.CreateDirectory(vmafDir);

        var baseCq = cfg.CandidateCq.ToList();
        bool hdrShiftApplied = isHdr && cfg.HdrApplyCqLadderShift && cfg.HdrCqLadderDelta > 0;

        var effectiveCq = hdrShiftApplied
            ? baseCq.Select(cq => Math.Max(cfg.MinCq, cq - cfg.HdrCqLadderDelta)).Distinct().OrderBy(x => x).ToList()
            : baseCq.Distinct().OrderBy(x => x).ToList();

        if (hdrShiftApplied)
            Console.WriteLine($"  HDR tuning: CQ ladder shifted by -{cfg.HdrCqLadderDelta}. Base=[{string.Join(",", baseCq)}] Effective=[{string.Join(",", effectiveCq)}]");

        var cqResults = new List<CqAggregate>();

        foreach (var cq in effectiveCq)
        {
            ct.ThrowIfCancellationRequested();
            Console.WriteLine($"  Tuning CQ={cq}...");

            var tasks = windows.Select(async (w, i) =>
            {
                string tag = $"s{i:00}_t{w.StartSeconds.ToString("F3", CultureInfo.InvariantCulture)}";
                string encPath = Path.Combine(samplesDir, $"cq{cq}_{tag}.mkv");
                string vmafLog = Path.Combine(vmafDir, $"cq{cq}_{tag}.json");

                using (await gpu.AcquireAsync(nvenc: 1, nvdec: cfg.UseNvdecForEncode ? 1 : 0, ct))
                {
                    await Pipelines.EncodeSampleNvencAsync(cfg, input, encPath, w, restore, cq, ct);
                }

                using (await gpu.AcquireAsync(nvenc: 0, nvdec: cfg.UseNvdecForVmaf ? 2 : 0, ct))
                {
                    await Pipelines.RunVmafAsync(cfg, input, encPath, w, restore, isHdr, vmafLog, ct);
                }

                double mean = await Vmaf.ParseMeanAsync(vmafLog, ct);
                return new SampleMetric { SampleIndex = i, Window = w, EncodedPath = encPath, VmafLogPath = vmafLog, VmafMean = mean };
            }).ToList();

            var metrics = new List<SampleMetric>();
            foreach (var batch in tasks.Chunk(8))
                metrics.AddRange(await Task.WhenAll(batch));

            var agg = CqAggregate.From(cq, metrics);
            cqResults.Add(agg);

            Console.WriteLine($"    mean={agg.MeanVmaf:F2} p05={agg.P05Vmaf:F2} min={agg.MinVmaf:F2}");
        }

        var selection = Selector.Select(cfg, cqResults);

        return new TuningResult
        {
            SampleWindows = windows,
            CqResults = cqResults,
            Selection = selection,

            BaseCqList = baseCq,
            EffectiveCqList = effectiveCq,
            HdrCqShiftApplied = hdrShiftApplied,
            HdrCqShiftDelta = hdrShiftApplied ? cfg.HdrCqLadderDelta : 0
        };
    }
}
