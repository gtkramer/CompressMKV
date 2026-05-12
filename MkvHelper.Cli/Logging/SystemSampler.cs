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

    public SystemSampler(ResourcePool pool, TimeSpan interval, CancellationToken parent)
    {
        _pool = pool;
        _interval = interval;
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
        var pool = _pool.Snapshot();
        var (cpuPct, load1m) = ReadCpuUsage();
        var gpu = await ReadGpuStatsAsync(ct);

        Log.Logger.Information(
            "System util sample: pool {@Pool}; cpu {CpuPct:F1}% (load1m {Load1m:F2}); " +
            "gpu {@Gpu}",
            pool, cpuPct, load1m, gpu);
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
            var line = File.ReadAllLines("/proc/stat")[0];
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
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
            var loadLine = File.ReadAllText("/proc/loadavg").Trim();
            var first = loadLine.Split(' ', 2)[0];
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
            var (code, stdout, _) = await Proc.RunAsync(
                "nvidia-smi",
                new[]
                {
                    "--query-gpu=utilization.gpu,utilization.memory,memory.used,encoder.stats.sessionCount",
                    "--format=csv,noheader,nounits",
                },
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

            var line = stdout.Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            if (string.IsNullOrEmpty(line)) return GpuStats.Empty;

            // Fields are comma-separated; some drivers emit "[N/A]" when a
            // metric isn't supported — treat those as null.
            var fields = line.Split(',').Select(s => s.Trim()).ToArray();
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
        var s = fields[idx];
        if (string.IsNullOrEmpty(s) || s.Contains("N/A", StringComparison.OrdinalIgnoreCase)) return null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
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
