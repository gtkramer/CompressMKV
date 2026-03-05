namespace CompressMkv;

/// <summary>
/// GPU gate — manages NVENC/NVDEC session slot concurrency via semaphores.
/// </summary>
public sealed class GpuGate
{
    private readonly SemaphoreSlim _nvenc;
    private readonly SemaphoreSlim _nvdec;

    public GpuGate(int nvencSlots, int nvdecSlots)
    {
        _nvenc = new SemaphoreSlim(nvencSlots, nvencSlots);
        _nvdec = new SemaphoreSlim(nvdecSlots, nvdecSlots);
    }

    public async Task<IDisposable> AcquireAsync(int nvenc, int nvdec, CancellationToken ct)
    {
        for (int i = 0; i < nvdec; i++) await _nvdec.WaitAsync(ct);
        try
        {
            for (int i = 0; i < nvenc; i++) await _nvenc.WaitAsync(ct);
        }
        catch
        {
            for (int i = 0; i < nvdec; i++) _nvdec.Release();
            throw;
        }
        return new Releaser(_nvenc, _nvdec, nvenc, nvdec);
    }

    private sealed class Releaser : IDisposable
    {
        private readonly SemaphoreSlim _nvenc, _nvdec;
        private readonly int _enc, _dec;
        private bool _done;
        public Releaser(SemaphoreSlim nvenc, SemaphoreSlim nvdec, int enc, int dec)
        { _nvenc = nvenc; _nvdec = nvdec; _enc = enc; _dec = dec; }
        public void Dispose()
        {
            if (_done) return;
            for (int i = 0; i < _enc; i++) _nvenc.Release();
            for (int i = 0; i < _dec; i++) _nvdec.Release();
            _done = true;
        }
    }
}
