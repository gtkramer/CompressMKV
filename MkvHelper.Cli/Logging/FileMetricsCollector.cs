namespace MkvHelper;

/// <summary>
/// Per-file accumulator of every pool acquire's outcome: which op, what
/// resource shape it was granted, how long it queued, and how long it held.
/// Used by the per-file time card (decisions.log end-of-file summary) and by
/// the cross-file pool-share rollup at run end.  One instance per file,
/// owned by <see cref="PerFileLogger"/>.
///
/// Recording is fire-and-forget from each acquire site; aggregation happens
/// once when the file completes.  Thread-safe — Phase 2 records from
/// parallel sample tasks.
/// </summary>
public sealed class FileMetricsCollector
{
    private readonly Lock _lock = new();
    private readonly List<OpRecord> _records = [];

    public string VideoId { get; }

    public FileMetricsCollector(string videoId) { VideoId = videoId; }

    /// <summary>
    /// One pool transaction: op name, granted shape, queue wait, and the
    /// wall-clock the lease was held.  WaitMs and HoldMs are integer ms
    /// because that's what the pool emits — sub-ms precision wouldn't add
    /// signal at the scale of phases that take seconds to minutes.
    /// </summary>
    public readonly record struct OpRecord(
        string Op, ResourceRequest Granted, int WaitMs, int HoldMs);

    public void Record(string op, ResourceRequest granted, int waitMs, int holdMs)
    {
        lock (_lock) _records.Add(new OpRecord(op, granted, waitMs, holdMs));
    }

    /// <summary>Copy of every recorded op, in the order they finished.</summary>
    public IReadOnlyList<OpRecord> Snapshot()
    {
        lock (_lock) return _records.ToArray();
    }

    /// <summary>
    /// Top-<paramref name="n"/> records by queue wait time.  Used to surface
    /// the worst offenders in the per-file time card so a reader doesn't have
    /// to grep events.jsonl for the slowest acquires.
    /// </summary>
    public IReadOnlyList<OpRecord> TopWaits(int n)
    {
        lock (_lock)
            return _records.OrderByDescending(r => r.WaitMs).Take(n).ToArray();
    }

    /// <summary>
    /// Resource-time consumed by this file: sum over all ops of
    /// (granted_units × hold_ms) per resource.  Cross-file rollup of these
    /// numbers gives a "share of each pool by file" view useful for spotting
    /// hot files in a large batch.
    /// </summary>
    public ResourceTimeShare ResourceShare()
    {
        long cpu = 0, nvenc = 0, nvdec = 0, cuda = 0;
        lock (_lock)
        {
            foreach (OpRecord r in _records)
            {
                cpu   += (long)r.Granted.Cpu   * r.HoldMs;
                nvenc += (long)r.Granted.Nvenc * r.HoldMs;
                nvdec += (long)r.Granted.Nvdec * r.HoldMs;
                cuda  += (long)r.Granted.Cuda  * r.HoldMs;
            }
        }
        return new ResourceTimeShare(cpu, nvenc, nvdec, cuda);
    }
}

/// <summary>
/// Total resource-time (unit × millisecond) consumed by one file across all
/// its pool transactions.  CPU is in thread-ms; NVENC/NVDEC/CUDA are in
/// slot-ms.  Sums across files give the run-wide ownership breakdown — used
/// by the run-end "Pool share by file" table.
/// </summary>
public readonly record struct ResourceTimeShare(long CpuMs, long NvencMs, long NvdecMs, long CudaMs)
{
    public static ResourceTimeShare operator +(ResourceTimeShare a, ResourceTimeShare b) =>
        new(a.CpuMs + b.CpuMs, a.NvencMs + b.NvencMs, a.NvdecMs + b.NvdecMs, a.CudaMs + b.CudaMs);
}
