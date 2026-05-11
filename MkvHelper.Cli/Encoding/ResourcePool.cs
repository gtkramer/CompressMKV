namespace MkvHelper;

/// <summary>
/// Weighted multi-resource transactional semaphore covering the four pools
/// the pipeline actually contends for: CPU threads, NVENC streams, NVDEC
/// streams, and CUDA compute lanes.  Operations declare their cost up-front
/// via <see cref="ResourceRequest"/>; the pool grants them atomically when
/// every count is available, or queues them FIFO until they are.
///
/// Why transactional: avoids the partial-hold deadlock where two operations
/// each hold half of what the other needs.  An <see cref="AcquireAsync"/>
/// caller either gets the whole request or nothing.
///
/// Why strict FIFO: prevents starvation of large requests by a steady stream
/// of small ones.  A 16-thread VMAF queued behind a release will block
/// later 4-thread ref-extracts at the head of the queue until enough CPU
/// frees up — even if the 4-thread ops could run with what's available now.
/// Trade-off accepted: predictable progress beats opportunistic throughput
/// for this workload.  Head-of-line wait is bounded by the runtime of the
/// in-flight operations holding the requested resources.
///
/// Cancellation: a waiter that is cancelled before being granted is removed
/// from the queue and its task completes with <see cref="OperationCanceledException"/>.
/// A waiter that is cancelled in the race against grant either gets the
/// granted resources (and disposes them via the returned <see cref="IDisposable"/>)
/// or sees the cancellation — the pool's <c>Granted</c> flag, set under the
/// pool lock, settles the race so resources never leak.
/// </summary>
public sealed class ResourcePool
{
    private readonly int _cpuMax, _nvencMax, _nvdecMax, _cudaMax;
    private int _cpu, _nvenc, _nvdec, _cuda;
    private readonly LinkedList<Waiter> _waiters = new();
    private readonly object _lock = new();

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
    /// Snapshot of currently-available capacity.  Useful for diagnostics; the
    /// values can change before any subsequent <see cref="AcquireAsync"/> call.
    /// </summary>
    public (int Cpu, int Nvenc, int Nvdec, int Cuda) Snapshot()
    {
        lock (_lock) return (_cpu, _nvenc, _nvdec, _cuda);
    }

    public Task<IDisposable> AcquireAsync(ResourceRequest req, CancellationToken ct)
    {
        if (req.Cpu < 0 || req.Nvenc < 0 || req.Nvdec < 0 || req.Cuda < 0)
            throw new ArgumentException($"Negative resource request: {req}", nameof(req));

        // Requests that can never be satisfied are programming bugs — fail
        // fast so they surface at the call site, not in a hung await.
        if (req.Cpu > _cpuMax || req.Nvenc > _nvencMax ||
            req.Nvdec > _nvdecMax || req.Cuda > _cudaMax)
            throw new ArgumentException(
                $"Request {req} exceeds pool capacity " +
                $"(cpu={_cpuMax}, nvenc={_nvencMax}, nvdec={_nvdecMax}, cuda={_cudaMax}).",
                nameof(req));

        lock (_lock)
        {
            // Fast path: nobody ahead of us and the resources are sitting there.
            if (_waiters.Count == 0 && CanSatisfy(req))
            {
                Take(req);
                return Task.FromResult<IDisposable>(new Releaser(this, req));
            }

            // Slow path: enqueue and wait.
            var tcs = new TaskCompletionSource<IDisposable>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var waiter = new Waiter(req, tcs);
            var node = _waiters.AddLast(waiter);

            ct.Register(static state =>
            {
                var (pool, w, n) = ((ResourcePool, Waiter, LinkedListNode<Waiter>))state!;
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

    private void Release(in ResourceRequest r)
    {
        lock (_lock)
        {
            Return(r);

            // Strict-FIFO grant loop.  Once the head waiter can't be satisfied
            // we stop — any later waiter that COULD be satisfied has to wait
            // its turn.  This is the fairness guarantee.
            while (_waiters.First is { } node && CanSatisfy(node.Value.Request))
            {
                var w = node.Value;
                _waiters.RemoveFirst();
                Take(w.Request);
                w.Granted = true;
                w.Tcs.TrySetResult(new Releaser(this, w.Request));
            }
        }
    }

    private sealed class Waiter(ResourceRequest request, TaskCompletionSource<IDisposable> tcs)
    {
        public ResourceRequest Request { get; } = request;
        public TaskCompletionSource<IDisposable> Tcs { get; } = tcs;
        public bool Granted { get; set; }
    }

    private sealed class Releaser(ResourcePool pool, ResourceRequest req) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            pool.Release(req);
        }
    }
}
