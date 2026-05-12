using System.Diagnostics;
using System.Threading;
using Serilog;

namespace MkvHelper;

/// <summary>
/// Weighted multi-resource transactional semaphore covering the four pools
/// the pipeline actually contends for: CPU threads, NVENC streams, NVDEC
/// streams, and CUDA compute lanes.  Operations declare their cost up-front
/// via <see cref="ResourceRequest"/>; the pool grants them atomically when
/// every count is available, or queues them until the resources free up.
///
/// Two acquisition modes:
///
///   <see cref="AcquireAsync"/> takes a single request shape — used when an
///   operation has one valid resource profile.
///
///   <see cref="AcquireAnyAsync"/> takes a list of alternatives — used when
///   one logical task can run on multiple resource profiles (e.g. an idet
///   pass that can decode on either the CPU or NVDEC).  The pool picks the
///   first listed alternative that fits the current state, both on the fast
///   path and inside the slow-path grant scan.
///
/// FIFO with head-skip: the queue is a FIFO list, and on every release the
/// pool scans from head to tail, granting each waiter whose alternatives
/// fit.  If the head waiter is blocked on a specific resource (say CUDA)
/// the scan does NOT stop — it continues past the head and grants later
/// waiters whose alternatives don't need that resource.  This keeps NVENC
/// and the CPU pool busy when a head VMAF is waiting for a CUDA lane.
///
/// Anti-starvation: a head waiter is checked first on EVERY release, so as
/// soon as the resource it's blocked on frees, the head gets granted.  No
/// later waiter can hoard that resource ahead of the head, because the
/// scan order honors queue order.  Head-of-line delay is bounded by the
/// runtime of whoever currently holds the head's missing resource.
///
/// Cancellation: a waiter that is cancelled before being granted is removed
/// from the queue and its task completes with OperationCanceledException.
/// A waiter cancelled in the race against grant either gets the granted
/// resources (and disposes them via the returned <see cref="IDisposable"/>)
/// or sees the cancellation — the pool's <c>Granted</c> flag, set under
/// the pool lock, settles the race so resources never leak.
///
/// Event emission: every acquire (fast-path or after-queue) and release
/// fires a structured Serilog event with file/op labels, granted shape,
/// queue wait, and a post-state pool snapshot.  This lets after-the-fact
/// analysis answer "how long did file 5 wait for a CUDA lane?" without
/// instrumenting every call site.
/// </summary>
public sealed class ResourcePool
{
    private readonly int _cpuMax, _nvencMax, _nvdecMax, _cudaMax;
    private int _cpu, _nvenc, _nvdec, _cuda;
    private readonly LinkedList<Waiter> _waiters = new();
    private readonly Lock _lock = new();

    public ResourcePool(int cpu, int nvenc, int nvdec, int cuda)
    {
        if (cpu <= 0)   throw new ArgumentOutOfRangeException(nameof(cpu),   "cpu pool must be > 0");
        if (nvenc < 0)  throw new ArgumentOutOfRangeException(nameof(nvenc), "nvenc pool must be ≥ 0");
        if (nvdec < 0)  throw new ArgumentOutOfRangeException(nameof(nvdec), "nvdec pool must be ≥ 0");
        if (cuda  < 0)  throw new ArgumentOutOfRangeException(nameof(cuda),  "cuda pool must be ≥ 0");

        _cpuMax   = _cpu   = cpu;
        _nvencMax = _nvenc = nvenc;
        _nvdecMax = _nvdec = nvdec;
        _cudaMax  = _cuda  = cuda;
    }

    public int CpuTotal   => _cpuMax;
    public int NvencTotal => _nvencMax;
    public int NvdecTotal => _nvdecMax;
    public int CudaTotal  => _cudaMax;

    /// <summary>
    /// Snapshot of currently-available capacity.  Useful for diagnostics and
    /// for the system-utilization sampler; the values can change before any
    /// subsequent <see cref="AcquireAsync"/> call.
    /// </summary>
    public PoolSnapshot Snapshot()
    {
        lock (_lock) return new PoolSnapshot(_cpu, _nvenc, _nvdec, _cuda);
    }

    /// <summary>
    /// Acquire a single resource shape.  Convenience wrapper around
    /// <see cref="AcquireAnyAsync"/> with a one-element alternatives list.
    /// </summary>
    public async Task<IDisposable> AcquireAsync(
        ResourceRequest req, CancellationToken ct,
        string? file = null, string? op = null)
    {
        AcquireResult result = await AcquireAnyAsync([req], ct, file, op).ConfigureAwait(false);
        return result.Lease;
    }

    /// <summary>
    /// Acquire resources for an operation that has multiple valid resource
    /// shapes.  The pool tries each alternative in order; the first one
    /// satisfiable by the current pool state is granted.  When none fits,
    /// the waiter is queued and woken when ANY of its alternatives becomes
    /// satisfiable on a future release.
    ///
    /// The returned <see cref="AcquireResult"/> contains the lease (dispose
    /// to release the resources) and the index of the alternative that was
    /// granted, so the caller can adapt its work (e.g. choose a CPU or
    /// NVDEC ffmpeg invocation to match the granted slot).
    ///
    /// <paramref name="file"/> and <paramref name="op"/> identify the
    /// caller for structured logging — they end up on every acquire and
    /// release event for this operation and are echoed in the
    /// <see cref="AcquireResult"/> so per-file loggers can mirror them.
    /// </summary>
    public Task<AcquireResult> AcquireAnyAsync(
        IReadOnlyList<ResourceRequest> alternatives, CancellationToken ct,
        string? file = null, string? op = null)
    {
        if (alternatives == null || alternatives.Count == 0)
            throw new ArgumentException("At least one alternative required.", nameof(alternatives));

        for (int i = 0; i < alternatives.Count; i++)
        {
            ResourceRequest alt = alternatives[i];
            if (alt.Cpu < 0 || alt.Nvenc < 0 || alt.Nvdec < 0 || alt.Cuda < 0)
                throw new ArgumentException(
                    $"Negative resource request at index {i}: {alt}", nameof(alternatives));
            if (alt.Cpu > _cpuMax || alt.Nvenc > _nvencMax ||
                alt.Nvdec > _nvdecMax || alt.Cuda > _cudaMax)
                throw new ArgumentException(
                    $"Request {alt} at index {i} exceeds pool capacity " +
                    $"(cpu={_cpuMax}, nvenc={_nvencMax}, nvdec={_nvdecMax}, cuda={_cudaMax}).",
                    nameof(alternatives));
        }

        lock (_lock)
        {
            // Fast path: try each alternative in order; first that fits, grant.
            // Note: we admit on fast path even when the queue is non-empty —
            // queued waiters by definition can't be granted at this exact
            // resource state (otherwise the previous release would have woken
            // them), so a fitting new arrival doesn't displace any of them.
            for (int i = 0; i < alternatives.Count; i++)
            {
                ResourceRequest alt = alternatives[i];
                if (CanSatisfy(alt))
                {
                    Take(alt);
                    PoolSnapshot snapshot = new(_cpu, _nvenc, _nvdec, _cuda);
                    EmitAcquired(file, op, alternatives, alt, i, waitMs: 0, fastPath: true, snapshot);
                    return Task.FromResult(new AcquireResult(
                        new Releaser(this, alt, file, op), alt, i, WaitMs: 0, snapshot, op));
                }
            }

            // Slow path: enqueue with all alternatives; any of them becoming
            // satisfiable will wake the waiter.
            TaskCompletionSource<AcquireResult> tcs = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Waiter waiter = new(alternatives, tcs, file, op, Stopwatch.StartNew());
            LinkedListNode<Waiter> node = _waiters.AddLast(waiter);

            ct.Register(static state =>
            {
                (ResourcePool pool, Waiter w, LinkedListNode<Waiter> n) = ((ResourcePool, Waiter, LinkedListNode<Waiter>))state!;
                lock (pool._lock)
                {
                    // Resolve the cancel-vs-grant race: if the pool already
                    // granted this waiter (under the same lock), the Releaser
                    // is on its way to the caller and the resources will be
                    // returned via Dispose.  Skip the cancel-side cleanup.
                    if (w.Granted) return;
                    if (n.List != null) pool._waiters.Remove(n);
                }
                w.Tcs.TrySetCanceled();
            }, (this, waiter, node));

            return tcs.Task;
        }
    }

    private bool CanSatisfy(in ResourceRequest r) =>
        _cpu >= r.Cpu && _nvenc >= r.Nvenc && _nvdec >= r.Nvdec && _cuda >= r.Cuda;

    private void Take(in ResourceRequest r)
    {
        _cpu   -= r.Cpu;
        _nvenc -= r.Nvenc;
        _nvdec -= r.Nvdec;
        _cuda  -= r.Cuda;
    }

    private void Return(in ResourceRequest r)
    {
        _cpu   += r.Cpu;
        _nvenc += r.Nvenc;
        _nvdec += r.Nvdec;
        _cuda  += r.Cuda;
    }

    private void Release(in ResourceRequest r, string? file, string? op)
    {
        int wokenCount = 0;
        PoolSnapshot postRelease;
        // Queued waiters that this release ended up granting.  Their acquire
        // events are emitted AFTER the lock is dropped — Serilog's async sink
        // doesn't block but we still want minimum lock time.
        List<(string? File, string? Op, IReadOnlyList<ResourceRequest> Requested,
              ResourceRequest Granted, int AltIndex, int WaitMs, PoolSnapshot Snapshot)>?
            grantedInScan = null;

        lock (_lock)
        {
            Return(r);

            // Head-skip FIFO grant scan: iterate from head to tail; grant
            // each waiter whose alternatives fit the current state.  Past
            // versions stopped on the first non-fitting head — that caused
            // NVENC to sit idle while a queued VMAF blocked on CUDA stalled
            // sample encodes behind it.  Continuing past the head lets
            // non-conflicting work make progress.  The head still gets
            // priority within each scan, so anti-starvation holds: as soon
            // as the resource the head needs frees, the next release grants
            // it before considering later waiters.
            LinkedListNode<Waiter>? node = _waiters.First;
            while (node != null)
            {
                LinkedListNode<Waiter>? next = node.Next;
                Waiter w = node.Value;
                for (int i = 0; i < w.Alternatives.Count; i++)
                {
                    ResourceRequest alt = w.Alternatives[i];
                    if (CanSatisfy(alt))
                    {
                        _waiters.Remove(node);
                        Take(alt);
                        w.Granted = true;
                        PoolSnapshot snap = new(_cpu, _nvenc, _nvdec, _cuda);
                        int waitMs = (int)w.QueueTimer.ElapsedMilliseconds;
                        grantedInScan ??= [];
                        grantedInScan.Add((w.File, w.Op, w.Alternatives, alt, i, waitMs, snap));
                        w.Tcs.TrySetResult(new AcquireResult(
                            new Releaser(this, alt, w.File, w.Op), alt, i, waitMs, snap, w.Op));
                        wokenCount++;
                        break;
                    }
                }
                node = next;
            }
            postRelease = new PoolSnapshot(_cpu, _nvenc, _nvdec, _cuda);
        }

        // Outside the lock: emit events.  Order is release first, then any
        // acquire events for waiters granted in the same scan, so a reader
        // can see the cause/effect chain.
        EmitReleased(file, op, r, postRelease, wokenCount);
        if (grantedInScan != null)
        {
            foreach ((string? File, string? Op, IReadOnlyList<ResourceRequest> Requested,
                      ResourceRequest Granted, int AltIndex, int WaitMs, PoolSnapshot Snapshot) g in grantedInScan)
                EmitAcquired(g.File, g.Op, g.Requested, g.Granted, g.AltIndex,
                    g.WaitMs, fastPath: false, g.Snapshot);
        }
    }

    private static void EmitAcquired(
        string? file, string? op,
        IReadOnlyList<ResourceRequest> requested,
        ResourceRequest granted, int altIndex,
        int waitMs, bool fastPath, PoolSnapshot poolAfter)
    {
        Log.Logger.Information(
            "Pool acquired {Op} for {File}: granted {@Granted}, alt {AltIndex}, " +
            "waited {WaitMs}ms ({Path}); pool now {@PoolAfter}",
            op ?? "—", file ?? "—", granted, altIndex, waitMs,
            fastPath ? "fast" : "queued", poolAfter);
    }

    private static void EmitReleased(
        string? file, string? op,
        ResourceRequest released, PoolSnapshot poolAfter, int waitersGrantedInScan)
    {
        Log.Logger.Information(
            "Pool released {Op} for {File}: released {@Released}; pool now {@PoolAfter}; " +
            "granted {WaitersGrantedInScan} waiter(s) in same scan",
            op ?? "—", file ?? "—", released, poolAfter, waitersGrantedInScan);
    }

    private sealed class Waiter(
        IReadOnlyList<ResourceRequest> alternatives,
        TaskCompletionSource<AcquireResult> tcs,
        string? file, string? op, Stopwatch queueTimer)
    {
        public IReadOnlyList<ResourceRequest> Alternatives { get; } = alternatives;
        public TaskCompletionSource<AcquireResult> Tcs { get; } = tcs;
        public string? File { get; } = file;
        public string? Op { get; } = op;
        public Stopwatch QueueTimer { get; } = queueTimer;
        public bool Granted { get; set; }
    }

    private sealed class Releaser(ResourcePool pool, ResourceRequest req, string? file, string? op) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            pool.Release(req, file, op);
        }
    }
}

/// <summary>
/// Snapshot of pool capacity at a point in time.  Used both diagnostically
/// (via <see cref="ResourcePool.Snapshot"/>) and inside acquire/release event
/// payloads so a reader can reconstruct pool state at any logged event.
/// </summary>
public readonly record struct PoolSnapshot(int Cpu, int Nvenc, int Nvdec, int Cuda);

/// <summary>
/// Outcome of <see cref="ResourcePool.AcquireAnyAsync"/>.  <see cref="Lease"/>
/// must be disposed to release the resources back to the pool.
/// <see cref="Granted"/> is the alternative the pool actually picked, and
/// <see cref="AlternativeIndex"/> is its position in the alternatives list
/// passed in — the caller uses one or the other to branch on which
/// implementation to invoke (e.g. CPU vs GPU ffmpeg args).
/// <see cref="WaitMs"/> is how long the request sat in the queue before being
/// granted (0 on the fast path), and <see cref="PoolAfter"/> is the pool
/// state immediately after the grant — both useful for per-file logging at
/// the call site without re-querying the pool.
/// </summary>
public sealed record class AcquireResult(
    IDisposable Lease,
    ResourceRequest Granted,
    int AlternativeIndex,
    int WaitMs,
    PoolSnapshot PoolAfter,
    string? Op);
