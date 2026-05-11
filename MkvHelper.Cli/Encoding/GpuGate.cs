namespace MkvHelper;

/// <summary>
/// GPU gate — manages NVENC, NVDEC, and CUDA-VMAF slot concurrency.
///
/// On Blackwell/RTX 5080 the dedicated NVENC/NVDEC engines are the
/// hard-capped resources (2 of each).  CUDA-VMAF (libvmaf_cuda) shares
/// the general-purpose CUDA cores with NVENC's AQ helpers; we cap its
/// concurrency separately so a flood of in-flight VMAFs doesn't OOM the
/// GPU or crowd out NVENC encodes that need CUDA cycles too.
///
/// Acquisition is partial-rollback safe: if the second/third semaphore
/// can't be obtained, anything already taken is released before throwing.
/// </summary>
public sealed class GpuGate
{
    private readonly SemaphoreSlim _nvenc;
    private readonly SemaphoreSlim _nvdec;
    private readonly SemaphoreSlim _cuda;

    public GpuGate(int nvencSlots, int nvdecSlots, int cudaSlots)
    {
        _nvenc = new SemaphoreSlim(nvencSlots, nvencSlots);
        _nvdec = new SemaphoreSlim(nvdecSlots, nvdecSlots);
        _cuda  = new SemaphoreSlim(cudaSlots,  cudaSlots);
    }

    public async Task<IDisposable> AcquireAsync(int nvenc, int nvdec, int cuda, CancellationToken ct)
    {
        int decTaken = 0, encTaken = 0, cudaTaken = 0;
        try
        {
            for (int i = 0; i < nvdec; i++) { await _nvdec.WaitAsync(ct); decTaken++; }
            for (int i = 0; i < nvenc; i++) { await _nvenc.WaitAsync(ct); encTaken++; }
            for (int i = 0; i < cuda;  i++) { await _cuda .WaitAsync(ct); cudaTaken++; }
        }
        catch
        {
            for (int i = 0; i < cudaTaken; i++) _cuda.Release();
            for (int i = 0; i < encTaken;  i++) _nvenc.Release();
            for (int i = 0; i < decTaken;  i++) _nvdec.Release();
            throw;
        }
        return new Releaser(_nvenc, _nvdec, _cuda, nvenc, nvdec, cuda);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _nvenc, _nvdec, _cuda;
        private readonly int _enc, _dec, _cu;
        private bool _done;
        public Releaser(SemaphoreSlim nvenc, SemaphoreSlim nvdec, SemaphoreSlim cuda,
                        int enc, int dec, int cu)
        { _nvenc = nvenc; _nvdec = nvdec; _cuda = cuda; _enc = enc; _dec = dec; _cu = cu; }
        public void Dispose()
        {
            if (_done) return;
            for (int i = 0; i < _cu;  i++) _cuda.Release();
            for (int i = 0; i < _enc; i++) _nvenc.Release();
            for (int i = 0; i < _dec; i++) _nvdec.Release();
            _done = true;
        }
    }
}
