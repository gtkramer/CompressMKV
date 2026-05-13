using System.Globalization;
using Serilog;

namespace MkvHelper;

/// <summary>
/// Background loop that periodically samples real system utilization and
/// emits a structured event so post-hoc analysis can compare the
/// <see cref="ResourcePool"/>'s internal accounting against what the OS and
/// GPU are actually doing.
///
/// What it captures, per tick:
///   • <see cref="ResourcePool.Snapshot"/> — what the pool thinks is free.
///   • <c>/proc/stat</c> first line delta → user-mode CPU utilization %.
///   • <c>/proc/loadavg</c> → 1-minute load average.
///   • <c>nvidia-smi --query-gpu</c> → GPU utilization %, memory utilization
///     %, VRAM used, and NVENC session count (when the driver reports it).
///
/// Why it exists: the pool's "12 of 20 CPU reserved" is a *declared* count
/// from the cost shapes in <see cref="Config"/>.  If pinning is wrong or a
/// CPU-side filter scales differently than assumed, real CPU usage can
/// diverge from the declared count.  A periodic side-by-side sample makes
/// the divergence visible without instrumenting every ffmpeg invocation.
///
/// Cost: one <c>nvidia-smi</c> subprocess per tick (~100 ms), two small
/// <c>/proc</c> reads.  At 5 s cadence, ~2 % overhead.  Failures (no GPU
/// present, malformed CSV) are caught and logged once — the sampler keeps
/// running with the pool snapshot only so the run-wide log still has
/// continuity.
/// </summary>
public sealed class SystemSampler : IAsyncDisposable
{
    private readonly ResourcePool _pool;
    private readonly TimeSpan _interval;
    private readonly CancellationTokenSource _cts;
    private readonly Task _loop;
    private bool _gpuQueryWarned;

    // Previous /proc/stat first-line counters; null on the first tick.
    private (ulong Total, ulong Idle)? _prevCpu;

    // Streaming accumulator over every tick.  Read at run-end via Summarize()
    // to compare what the pool *thought* it was scheduling against what the
    // hardware actually did.  Kept as running sums + counts so memory cost is
    // O(1) regardless of run length.
    private readonly Lock _statLock = new();
    private long _sampleCount;
    private long _cpuFreeSum, _nvencFreeSum, _nvdecFreeSum, _cudaFreeSum;
    private int _cpuFreeMin, _nvencFreeMin, _nvdecFreeMin, _cudaFreeMin;
    private double _cpuPctSum;
    private double _cpuPctMax;
    private long _gpuPctCount;
    private double _gpuPctSum;
    private int _gpuPctMax;
    private long _memPctCount;
    private double _memPctSum;
    private long _vramMbCount;
    private double _vramMbSum;
    private int _vramMbMax;
    private long _nvencSessCount;
    private double _nvencSessSum;
    private int _nvencSessMax;

    public SystemSampler(ResourcePool pool, TimeSpan interval, CancellationToken parent)
    {
        _pool = pool;
        _interval = interval;
        _cpuFreeMin = pool.CpuTotal;
        _nvencFreeMin = pool.NvencTotal;
        _nvdecFreeMin = pool.NvdecTotal;
        _cudaFreeMin = pool.CudaTotal;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await EmitOneAsync(ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Don't let a sampling hiccup crash the run.  Emit one warning
                // and continue; subsequent ticks will retry from a clean state.
                Log.Logger.Warning(ex, "System sampler tick failed");
            }

            try { await Task.Delay(_interval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task EmitOneAsync(CancellationToken ct)
    {
        PoolSnapshot pool = _pool.Snapshot();
        (double cpuPct, double load1m) = ReadCpuUsage();
        GpuStats gpu = await ReadGpuStatsAsync(ct);

        Accumulate(pool, cpuPct, gpu);

        Log.Logger.Information(
            "System util sample: pool {@Pool}; cpu {CpuPct:F1}% (load1m {Load1m:F2}); " +
            "gpu {@Gpu}",
            pool, cpuPct, load1m, gpu);
    }

    private void Accumulate(PoolSnapshot pool, double cpuPct, GpuStats gpu)
    {
        lock (_statLock)
        {
            _sampleCount++;
            _cpuFreeSum   += pool.Cpu;
            _nvencFreeSum += pool.Nvenc;
            _nvdecFreeSum += pool.Nvdec;
            _cudaFreeSum  += pool.Cuda;
            if (pool.Cpu   < _cpuFreeMin)   _cpuFreeMin   = pool.Cpu;
            if (pool.Nvenc < _nvencFreeMin) _nvencFreeMin = pool.Nvenc;
            if (pool.Nvdec < _nvdecFreeMin) _nvdecFreeMin = pool.Nvdec;
            if (pool.Cuda  < _cudaFreeMin)  _cudaFreeMin  = pool.Cuda;

            _cpuPctSum += cpuPct;
            if (cpuPct > _cpuPctMax) _cpuPctMax = cpuPct;

            if (gpu.GpuUtilPct is { } g)
            {
                _gpuPctCount++;
                _gpuPctSum += g;
                if (g > _gpuPctMax) _gpuPctMax = g;
            }
            if (gpu.MemoryUtilPct is { } m)
            {
                _memPctCount++;
                _memPctSum += m;
            }
            if (gpu.VramMbUsed is { } v)
            {
                _vramMbCount++;
                _vramMbSum += v;
                if (v > _vramMbMax) _vramMbMax = v;
            }
            if (gpu.NvencSessionCount is { } n)
            {
                _nvencSessCount++;
                _nvencSessSum += n;
                if (n > _nvencSessMax) _nvencSessMax = n;
            }
        }
    }

    /// <summary>
    /// Snapshot the accumulator and return per-pool declared-busy% next to the
    /// real CPU/GPU/NVENC-session figures.  Safe to call at any time; the
    /// caller decides when to render it (typically at run end before
    /// <see cref="DisposeAsync"/>).
    /// </summary>
    public SystemSamplerSummary Summarize()
    {
        int cpuTotal   = _pool.CpuTotal;
        int nvencTotal = _pool.NvencTotal;
        int nvdecTotal = _pool.NvdecTotal;
        int cudaTotal  = _pool.CudaTotal;

        lock (_statLock)
        {
            if (_sampleCount == 0) return SystemSamplerSummary.Empty;

            double cpuFreeAvg   = (double)_cpuFreeSum   / _sampleCount;
            double nvencFreeAvg = (double)_nvencFreeSum / _sampleCount;
            double nvdecFreeAvg = (double)_nvdecFreeSum / _sampleCount;
            double cudaFreeAvg  = (double)_cudaFreeSum  / _sampleCount;

            double cpuBusyPct   = cpuTotal   > 0 ? 100.0 * (cpuTotal   - cpuFreeAvg)   / cpuTotal   : 0;
            double nvencBusyPct = nvencTotal > 0 ? 100.0 * (nvencTotal - nvencFreeAvg) / nvencTotal : 0;
            double nvdecBusyPct = nvdecTotal > 0 ? 100.0 * (nvdecTotal - nvdecFreeAvg) / nvdecTotal : 0;
            double cudaBusyPct  = cudaTotal  > 0 ? 100.0 * (cudaTotal  - cudaFreeAvg)  / cudaTotal  : 0;

            double cpuPctAvg = _cpuPctSum / _sampleCount;
            double? gpuPctAvg     = _gpuPctCount    > 0 ? _gpuPctSum    / _gpuPctCount    : null;
            double? memPctAvg     = _memPctCount    > 0 ? _memPctSum    / _memPctCount    : null;
            double? vramMbAvg     = _vramMbCount    > 0 ? _vramMbSum    / _vramMbCount    : null;
            double? nvencSessAvg  = _nvencSessCount > 0 ? _nvencSessSum / _nvencSessCount : null;

            return new SystemSamplerSummary(
                _sampleCount,
                cpuTotal, nvencTotal, nvdecTotal, cudaTotal,
                CpuBusyPct:   cpuBusyPct,
                NvencBusyPct: nvencBusyPct,
                NvdecBusyPct: nvdecBusyPct,
                CudaBusyPct:  cudaBusyPct,
                CpuFreeMin:   _cpuFreeMin,
                NvencFreeMin: _nvencFreeMin,
                NvdecFreeMin: _nvdecFreeMin,
                CudaFreeMin:  _cudaFreeMin,
                CpuPctAvg: cpuPctAvg,
                CpuPctMax: _cpuPctMax,
                GpuPctAvg: gpuPctAvg,
                GpuPctMax: _gpuPctCount > 0 ? _gpuPctMax : null,
                MemoryPctAvg: memPctAvg,
                VramMbAvg: vramMbAvg,
                VramMbMax: _vramMbCount > 0 ? _vramMbMax : null,
                NvencSessionAvg: nvencSessAvg,
                NvencSessionMax: _nvencSessCount > 0 ? _nvencSessMax : null);
        }
    }

    // -----------------------------------------------------------------
    //  CPU sampling — /proc/stat first line is the all-CPUs aggregate:
    //    cpu  user nice system idle iowait irq softirq steal guest guest_nice
    //  Utilization between two samples is (Δtotal - Δidle) / Δtotal.
    //  /proc/loadavg first value is the 1-minute load average.
    // -----------------------------------------------------------------
    private (double CpuPct, double Load1m) ReadCpuUsage()
    {
        double cpuPct = 0;
        try
        {
            string line = File.ReadAllLines("/proc/stat")[0];
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // parts[0] is "cpu"; counters follow.
            ulong user    = ulong.Parse(parts[1], CultureInfo.InvariantCulture);
            ulong nice    = ulong.Parse(parts[2], CultureInfo.InvariantCulture);
            ulong system  = ulong.Parse(parts[3], CultureInfo.InvariantCulture);
            ulong idle    = ulong.Parse(parts[4], CultureInfo.InvariantCulture);
            ulong iowait  = parts.Length > 5 ? ulong.Parse(parts[5], CultureInfo.InvariantCulture) : 0;
            ulong irq     = parts.Length > 6 ? ulong.Parse(parts[6], CultureInfo.InvariantCulture) : 0;
            ulong softirq = parts.Length > 7 ? ulong.Parse(parts[7], CultureInfo.InvariantCulture) : 0;
            ulong steal   = parts.Length > 8 ? ulong.Parse(parts[8], CultureInfo.InvariantCulture) : 0;

            ulong idleSum  = idle + iowait;
            ulong busySum  = user + nice + system + irq + softirq + steal;
            ulong totalSum = idleSum + busySum;

            if (_prevCpu is { } prev && totalSum > prev.Total)
            {
                ulong dTotal = totalSum - prev.Total;
                ulong dIdle  = idleSum > prev.Idle ? idleSum - prev.Idle : 0;
                cpuPct = 100.0 * (dTotal - dIdle) / dTotal;
            }
            _prevCpu = (totalSum, idleSum);
        }
        catch { /* /proc/stat unavailable — leave cpuPct = 0 */ }

        double load1m = 0;
        try
        {
            string loadLine = File.ReadAllText("/proc/loadavg").Trim();
            string first = loadLine.Split(' ', 2)[0];
            load1m = double.Parse(first, CultureInfo.InvariantCulture);
        }
        catch { /* /proc/loadavg unavailable */ }

        return (cpuPct, load1m);
    }

    // -----------------------------------------------------------------
    //  GPU sampling via nvidia-smi.  Single subprocess per tick:
    //
    //    nvidia-smi --query-gpu=utilization.gpu,utilization.memory,memory.used,
    //                          encoder.stats.sessionCount
    //               --format=csv,noheader,nounits
    //
    //  encoder.stats.sessionCount is reported on most desktop NVIDIA drivers
    //  but isn't strictly guaranteed; if missing we emit null and continue.
    //  First sustained failure emits a one-time warning so the user can see
    //  the GPU column is going to be blank for the rest of the run.
    // -----------------------------------------------------------------
    private async Task<GpuStats> ReadGpuStatsAsync(CancellationToken ct)
    {
        try
        {
            (int code, string stdout, string _) = await Proc.RunAsync(
                "nvidia-smi",
                [
                    "--query-gpu=utilization.gpu,utilization.memory,memory.used,encoder.stats.sessionCount",
                    "--format=csv,noheader,nounits",
                ],
                ct);

            if (code != 0)
            {
                if (!_gpuQueryWarned)
                {
                    Log.Logger.Warning("nvidia-smi exited {ExitCode}; GPU samples will be skipped.", code);
                    _gpuQueryWarned = true;
                }
                return GpuStats.Empty;
            }

            string? line = stdout.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            if (string.IsNullOrEmpty(line)) return GpuStats.Empty;

            // Fields are comma-separated; some drivers emit "[N/A]" when a
            // metric isn't supported — treat those as null.
            string[] fields = line.Split(',').Select(s => s.Trim()).ToArray();
            int? gpuPct       = TryParseInt(fields, 0);
            int? memPct       = TryParseInt(fields, 1);
            int? vramMb       = TryParseInt(fields, 2);
            int? nvencSession = TryParseInt(fields, 3);

            return new GpuStats(gpuPct, memPct, vramMb, nvencSession);
        }
        catch (Exception ex) when (!_gpuQueryWarned)
        {
            Log.Logger.Warning(ex, "nvidia-smi unavailable; GPU samples will be skipped.");
            _gpuQueryWarned = true;
            return GpuStats.Empty;
        }
        catch
        {
            return GpuStats.Empty;
        }
    }

    private static int? TryParseInt(string[] fields, int idx)
    {
        if (idx >= fields.Length) return null;
        string s = fields[idx];
        if (string.IsNullOrEmpty(s) || s.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { await _loop; }
        catch (OperationCanceledException) { /* expected */ }
        _cts.Dispose();
    }
}

/// <summary>
/// GPU snapshot from one nvidia-smi sample.  Any field may be null when the
/// driver doesn't report it; serialized as JSON null so a downstream reader
/// can distinguish "unknown" from "zero".
/// </summary>
public readonly record struct GpuStats(
    int? GpuUtilPct,
    int? MemoryUtilPct,
    int? VramMbUsed,
    int? NvencSessionCount)
{
    public static GpuStats Empty => new(null, null, null, null);
}

/// <summary>
/// Run-end aggregate of <see cref="SystemSampler"/> ticks, paired up against
/// the pool's declared capacities so a reader can see whether the pool was
/// over- or under-subscribed relative to what the hardware actually did.
///
/// Each <c>*BusyPct</c> is the time-weighted fraction of pool slots that were
/// in-use (averaged across ticks); each *PctAvg is the matching real-hardware
/// number from <c>/proc/stat</c> or nvidia-smi.  A divergence of "pool 90%
/// busy / hardware 5% busy" is the textbook signature of a pool whose slot
/// count is too tight — the pool is queueing work that the silicon could
/// have absorbed.
/// </summary>
public sealed record class SystemSamplerSummary(
    long SampleCount,
    int CpuTotal, int NvencTotal, int NvdecTotal, int CudaTotal,
    double CpuBusyPct, double NvencBusyPct, double NvdecBusyPct, double CudaBusyPct,
    int CpuFreeMin, int NvencFreeMin, int NvdecFreeMin, int CudaFreeMin,
    double CpuPctAvg, double CpuPctMax,
    double? GpuPctAvg, int? GpuPctMax,
    double? MemoryPctAvg,
    double? VramMbAvg, int? VramMbMax,
    double? NvencSessionAvg, int? NvencSessionMax)
{
    public static SystemSamplerSummary Empty { get; } = new(
        SampleCount: 0,
        CpuTotal: 0, NvencTotal: 0, NvdecTotal: 0, CudaTotal: 0,
        CpuBusyPct: 0, NvencBusyPct: 0, NvdecBusyPct: 0, CudaBusyPct: 0,
        CpuFreeMin: 0, NvencFreeMin: 0, NvdecFreeMin: 0, CudaFreeMin: 0,
        CpuPctAvg: 0, CpuPctMax: 0,
        GpuPctAvg: null, GpuPctMax: null,
        MemoryPctAvg: null,
        VramMbAvg: null, VramMbMax: null,
        NvencSessionAvg: null, NvencSessionMax: null);
}
