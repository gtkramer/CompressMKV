namespace MkvHelper.Tests;

[TestFixture]
[Category("Unit")]
public class ResourcePoolTests
{
    // ------------------------------------------------------------------
    // Basic correctness
    // ------------------------------------------------------------------

    [Test]
    public async Task SingleAcquireAndRelease_Roundtrips()
    {
        var pool = new ResourcePool(cpu: 8, nvenc: 2, nvdec: 2, cuda: 2);

        using (await pool.AcquireAsync(new(Cpu: 4), CancellationToken.None))
        {
            var (cpu, _, _, _) = pool.Snapshot();
            Assert.That(cpu, Is.EqualTo(4));
        }

        Assert.That(pool.Snapshot().Cpu, Is.EqualTo(8));
    }

    [Test]
    public async Task MultiResource_AcquiresAllAtomically()
    {
        var pool = new ResourcePool(cpu: 8, nvenc: 2, nvdec: 2, cuda: 2);

        using (await pool.AcquireAsync(new(Cpu: 4, Nvenc: 1, Nvdec: 1, Cuda: 1), CancellationToken.None))
        {
            var snap = pool.Snapshot();
            Assert.That(snap.Cpu, Is.EqualTo(4));
            Assert.That(snap.Nvenc, Is.EqualTo(1));
            Assert.That(snap.Nvdec, Is.EqualTo(1));
            Assert.That(snap.Cuda, Is.EqualTo(1));
        }

        Assert.That(pool.Snapshot(), Is.EqualTo(new PoolSnapshot(8, 2, 2, 2)));
    }

    [Test]
    public async Task ConcurrentAcquires_FitWithinCapacity()
    {
        var pool = new ResourcePool(cpu: 16, nvenc: 2, nvdec: 2, cuda: 2);

        // Four 4-CPU ops fit exactly in 16 CPU.
        var tasks = Enumerable.Range(0, 4)
            .Select(_ => pool.AcquireAsync(new(Cpu: 4), CancellationToken.None))
            .ToArray();

        var releasers = await Task.WhenAll(tasks);
        Assert.That(pool.Snapshot().Cpu, Is.EqualTo(0));
        foreach (var r in releasers) r.Dispose();
        Assert.That(pool.Snapshot().Cpu, Is.EqualTo(16));
    }

    [Test]
    public void OverCapacityRequest_Throws()
    {
        var pool = new ResourcePool(cpu: 4, nvenc: 2, nvdec: 2, cuda: 2);

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await pool.AcquireAsync(new(Cpu: 5), CancellationToken.None));

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await pool.AcquireAsync(new(Nvenc: 3), CancellationToken.None));
    }

    [Test]
    public void NegativeRequest_Throws()
    {
        var pool = new ResourcePool(cpu: 4, nvenc: 2, nvdec: 2, cuda: 2);

        Assert.ThrowsAsync<ArgumentException>(async () =>
            await pool.AcquireAsync(new(Cpu: -1), CancellationToken.None));
    }

    [Test]
    public void InvalidPoolSize_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourcePool(cpu: 0, nvenc: 2, nvdec: 2, cuda: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ResourcePool(cpu: 4, nvenc: -1, nvdec: 2, cuda: 2));
    }

    // ------------------------------------------------------------------
    // FIFO ordering + anti-starvation
    // ------------------------------------------------------------------

    [Test]
    public async Task FifoOrder_HeadOfQueueServedFirst()
    {
        var pool = new ResourcePool(cpu: 4, nvenc: 2, nvdec: 2, cuda: 2);

        // Take all 4 CPU.
        var blocker = await pool.AcquireAsync(new(Cpu: 4), CancellationToken.None);

        // Queue waiters in order A, B, C — each needs 4 CPU.
        var taskA = pool.AcquireAsync(new(Cpu: 4), CancellationToken.None);
        var taskB = pool.AcquireAsync(new(Cpu: 4), CancellationToken.None);
        var taskC = pool.AcquireAsync(new(Cpu: 4), CancellationToken.None);

        // None complete while blocker is held.
        await Task.Delay(50);
        Assert.That(taskA.IsCompleted, Is.False);
        Assert.That(taskB.IsCompleted, Is.False);
        Assert.That(taskC.IsCompleted, Is.False);

        // Release blocker.  A should get it first (FIFO).
        blocker.Dispose();
        var rA = await taskA.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(taskB.IsCompleted, Is.False);

        // Release A.  B should be next.
        rA.Dispose();
        var rB = await taskB.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(taskC.IsCompleted, Is.False);

        rB.Dispose();
        var rC = await taskC.WaitAsync(TimeSpan.FromSeconds(1));
        rC.Dispose();
    }

    [Test]
    public async Task HeadSkip_NonConflictingOpsBypassBlockedHead()
    {
        // Head-skip lets ops whose resources don't conflict with the blocked
        // head proceed immediately, instead of stalling in line.  Anti-
        // starvation still holds: the head gets first dibs on every release
        // and runs as soon as ITS resource frees.
        var pool = new ResourcePool(cpu: 4, nvenc: 2, nvdec: 2, cuda: 1);

        // Hold the only CUDA slot.
        var cudaHolder = await pool.AcquireAsync(new(Cuda: 1), CancellationToken.None);

        // Big op needs CUDA — currently can't fit.  Queue it.
        var vmaf = pool.AcquireAsync(new(Cpu: 2, Cuda: 1), CancellationToken.None);
        await Task.Delay(50);
        Assert.That(vmaf.IsCompleted, Is.False);

        // Small op needs only CPU.  Under head-skip, it does NOT have to
        // wait behind the queued VMAF — they don't compete for the same
        // resource.  Fast path admits it immediately.
        var small = await pool.AcquireAsync(new(Cpu: 2), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(vmaf.IsCompleted, Is.False, "VMAF still waits for CUDA");
        small.Dispose();

        // Release CUDA → next release scan grants the head VMAF.
        cudaHolder.Dispose();
        var rVmaf = await vmaf.WaitAsync(TimeSpan.FromSeconds(1));
        rVmaf.Dispose();
    }

    [Test]
    public async Task HeadSkip_GrantsHeadFirstWhenItsResourceFrees()
    {
        // Even with head-skip, a head waiter blocked on resource X gets
        // priority for X as soon as X frees.  No later waiter can hoard
        // X ahead of the head.
        var pool = new ResourcePool(cpu: 8, nvenc: 2, nvdec: 2, cuda: 1);

        var cudaHolder = await pool.AcquireAsync(new(Cuda: 1), CancellationToken.None);

        // Head wants CUDA.
        var head = pool.AcquireAsync(new(Cpu: 2, Cuda: 1), CancellationToken.None);

        // Later waiter also wants CUDA.
        var later = pool.AcquireAsync(new(Cpu: 2, Cuda: 1), CancellationToken.None);

        await Task.Delay(50);
        Assert.That(head.IsCompleted, Is.False);
        Assert.That(later.IsCompleted, Is.False);

        // Release CUDA → head gets it (FIFO order within same resource).
        cudaHolder.Dispose();
        var rHead = await head.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(later.IsCompleted, Is.False, "later must wait its turn");

        rHead.Dispose();
        var rLater = await later.WaitAsync(TimeSpan.FromSeconds(1));
        rLater.Dispose();
    }

    // ------------------------------------------------------------------
    // Multi-alternative requests (AcquireAnyAsync)
    // ------------------------------------------------------------------

    [Test]
    public async Task AcquireAny_GrantsFirstAlternativeThatFits()
    {
        // CPU-preferred ordering: when CPU is free, take CPU.
        var pool = new ResourcePool(cpu: 8, nvenc: 2, nvdec: 2, cuda: 2);

        var cpuAlt = new ResourceRequest(Cpu: 4);
        var gpuAlt = new ResourceRequest(Cpu: 2, Nvdec: 1);

        var result = await pool.AcquireAnyAsync(new[] { cpuAlt, gpuAlt }, CancellationToken.None);
        Assert.That(result.AlternativeIndex, Is.EqualTo(0));
        Assert.That(result.Granted, Is.EqualTo(cpuAlt));
        result.Lease.Dispose();
    }

    [Test]
    public async Task AcquireAny_FallsBackToSecondAlternativeWhenFirstDoesntFit()
    {
        // CPU is mostly consumed (only 1 free) → the cpuAlt (needs 4) won't
        // fit, but the gpuAlt (needs 1 CPU + 1 NVDEC) does.  Fall back to it.
        var pool = new ResourcePool(cpu: 4, nvenc: 2, nvdec: 2, cuda: 2);

        var cpuHolder = await pool.AcquireAsync(new(Cpu: 3), CancellationToken.None);

        var cpuAlt = new ResourceRequest(Cpu: 4);
        var gpuAlt = new ResourceRequest(Cpu: 1, Nvdec: 1);

        var result = await pool.AcquireAnyAsync(new[] { cpuAlt, gpuAlt }, CancellationToken.None);
        Assert.That(result.AlternativeIndex, Is.EqualTo(1));
        Assert.That(result.Granted, Is.EqualTo(gpuAlt));
        result.Lease.Dispose();
        cpuHolder.Dispose();
    }

    [Test]
    public async Task AcquireAny_QueuesWhenNoAlternativeFits()
    {
        // The cpuAlt needs 4 CPU (only 1 free), and the gpuAlt's NVDEC slot
        // is held — neither fits, so the waiter queues.  Freeing NVDEC lets
        // the gpu alternative satisfy the queued waiter.
        var pool = new ResourcePool(cpu: 4, nvenc: 2, nvdec: 1, cuda: 2);

        var cpuHolder = await pool.AcquireAsync(new(Cpu: 3), CancellationToken.None);
        var nvdecHolder = await pool.AcquireAsync(new(Nvdec: 1), CancellationToken.None);

        var cpuAlt = new ResourceRequest(Cpu: 4);
        var gpuAlt = new ResourceRequest(Cpu: 1, Nvdec: 1);

        var task = pool.AcquireAnyAsync(new[] { cpuAlt, gpuAlt }, CancellationToken.None);
        await Task.Delay(50);
        Assert.That(task.IsCompleted, Is.False);

        // Free NVDEC → waiter wakes via the GPU alternative.
        nvdecHolder.Dispose();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(result.AlternativeIndex, Is.EqualTo(1));
        result.Lease.Dispose();
        cpuHolder.Dispose();
    }

    [Test]
    public async Task AcquireAny_QueuedWaiterPicksWhicheverAlternativeBecomesFree()
    {
        // The queued waiter is woken by whichever of its alternatives
        // becomes satisfiable first.  Demonstrates the "either path works"
        // semantics from the queue side.
        var pool = new ResourcePool(cpu: 4, nvenc: 2, nvdec: 1, cuda: 2);

        var cpuHolder = await pool.AcquireAsync(new(Cpu: 4), CancellationToken.None);
        var nvdecHolder = await pool.AcquireAsync(new(Nvdec: 1), CancellationToken.None);

        var cpuAlt = new ResourceRequest(Cpu: 4);
        var gpuAlt = new ResourceRequest(Cpu: 1, Nvdec: 1);

        var task = pool.AcquireAnyAsync(new[] { cpuAlt, gpuAlt }, CancellationToken.None);
        await Task.Delay(50);

        // Free CPU first → the cpu alternative wins.
        cpuHolder.Dispose();
        var result = await task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(result.AlternativeIndex, Is.EqualTo(0));
        result.Lease.Dispose();
        nvdecHolder.Dispose();
    }

    [Test]
    public async Task SingleRelease_GrantsMultipleSmallWaiters()
    {
        // When a release frees enough capacity for several queued waiters,
        // the grant loop should wake all of them in one pass.
        var pool = new ResourcePool(cpu: 8, nvenc: 2, nvdec: 2, cuda: 2);

        var blocker = await pool.AcquireAsync(new(Cpu: 8), CancellationToken.None);

        var w1 = pool.AcquireAsync(new(Cpu: 2), CancellationToken.None);
        var w2 = pool.AcquireAsync(new(Cpu: 2), CancellationToken.None);
        var w3 = pool.AcquireAsync(new(Cpu: 2), CancellationToken.None);
        var w4 = pool.AcquireAsync(new(Cpu: 2), CancellationToken.None);

        await Task.Delay(50);

        blocker.Dispose();

        var releasers = await Task.WhenAll(w1, w2, w3, w4).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.That(pool.Snapshot().Cpu, Is.EqualTo(0));
        foreach (var r in releasers) r.Dispose();
    }

    // ------------------------------------------------------------------
    // Cancellation
    // ------------------------------------------------------------------

    [Test]
    public async Task Cancellation_RemovesWaiterFromQueue()
    {
        var pool = new ResourcePool(cpu: 4, nvenc: 2, nvdec: 2, cuda: 2);

        var blocker = await pool.AcquireAsync(new(Cpu: 4), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var cancelled = pool.AcquireAsync(new(Cpu: 4), cts.Token);

        await Task.Delay(50);
        Assert.That(cancelled.IsCompleted, Is.False);

        cts.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await cancelled);

        // After cancelling the only waiter, releasing the blocker should
        // immediately make the resources available to a fresh acquirer.
        blocker.Dispose();
        var next = await pool.AcquireAsync(new(Cpu: 4), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(1));
        next.Dispose();
    }

    [Test]
    public async Task Cancellation_OfMiddleWaiter_DoesNotBlockOthers()
    {
        var pool = new ResourcePool(cpu: 4, nvenc: 2, nvdec: 2, cuda: 2);

        var blocker = await pool.AcquireAsync(new(Cpu: 4), CancellationToken.None);

        var a = pool.AcquireAsync(new(Cpu: 4), CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var b = pool.AcquireAsync(new(Cpu: 4), cts.Token);

        var c = pool.AcquireAsync(new(Cpu: 4), CancellationToken.None);

        cts.Cancel();
        Assert.ThrowsAsync<TaskCanceledException>(async () => await b);

        // A should still get resources first, then C.
        blocker.Dispose();
        var rA = await a.WaitAsync(TimeSpan.FromSeconds(1));
        rA.Dispose();
        var rC = await c.WaitAsync(TimeSpan.FromSeconds(1));
        rC.Dispose();
    }

    // ------------------------------------------------------------------
    // Stress / no-leak under concurrent load
    // ------------------------------------------------------------------

    [Test]
    public async Task ConcurrentLoad_NoResourceLeak()
    {
        var pool = new ResourcePool(cpu: 16, nvenc: 2, nvdec: 2, cuda: 2);
        var rng = new Random(42);

        const int ops = 500;
        var tasks = Enumerable.Range(0, ops).Select(async _ =>
        {
            // Random op shape, all within capacity.
            int cpu = rng.Next(1, 5);
            int nvenc = rng.Next(0, 2);
            int cuda = rng.Next(0, 2);

            using (await pool.AcquireAsync(new(Cpu: cpu, Nvenc: nvenc, Cuda: cuda), CancellationToken.None))
            {
                // Hold briefly to force overlap.
                await Task.Delay(rng.Next(0, 5));
            }
        }).ToArray();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));

        // After all ops complete, the pool must be fully restored.
        Assert.That(pool.Snapshot(), Is.EqualTo(new PoolSnapshot(16, 2, 2, 2)));
    }
}
