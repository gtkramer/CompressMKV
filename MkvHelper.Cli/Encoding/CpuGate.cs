namespace MkvHelper;

/// <summary>
/// CPU resource gate — a single global semaphore around the heavy CPU ffmpeg
/// operations (Phase 1 reference extraction, Phase 2 VMAF, detection,
/// verification, final-encode restoration).
///
/// Why a single global pool: when one file is finishing Phase 1 and another
/// is starting Phase 2, the operations are interchangeable from a CPU-load
/// perspective.  Sharing one pool lets work flow naturally between files
/// without per-file slot accounting.  Anyone who wants the CPU calls
/// <see cref="AcquireAsync"/>, runs their ffmpeg, disposes.
///
/// Sized to <see cref="Config.MaxConcurrentCpuFfmpegOps"/> on construction
/// (default 4 on a 20-core CPU).  Combined with each ffmpeg using
/// <see cref="Config.FfmpegCpuThreads"/> and libvmaf using
/// <see cref="Config.LibvmafThreads"/>, total in-flight thread count
/// stays close to (but slightly under) the logical core count, which is
/// what saturated-but-not-thrashing looks like.
/// </summary>
public sealed class CpuGate
{
    private readonly SemaphoreSlim _slots;

    public CpuGate(int maxConcurrent)
    {
        if (maxConcurrent <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxConcurrent));
        _slots = new SemaphoreSlim(maxConcurrent, maxConcurrent);
    }

    public async Task<IDisposable> AcquireAsync(CancellationToken ct)
    {
        await _slots.WaitAsync(ct);
        return new Releaser(_slots);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _slots;
        private bool _done;
        public Releaser(SemaphoreSlim slots) => _slots = slots;
        public void Dispose()
        {
            if (_done) return;
            _slots.Release();
            _done = true;
        }
    }
}
