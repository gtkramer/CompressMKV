namespace MkvHelper.Tests;

[TestFixture]
[Category("Unit")]
public class CqBinarySearchTests
{
    /// <summary>
    /// Helper: monotonic threshold predicate.  passes(cq) is true when
    /// cq &lt;= <paramref name="threshold"/>.  Mirrors the real-world
    /// "lower CQ = better quality, easier to pass VMAF gates" relation.
    /// </summary>
    private static Func<int, Task<bool>> Threshold(int threshold, List<int>? log = null)
        => cq =>
        {
            log?.Add(cq);
            return Task.FromResult(cq <= threshold);
        };

    // ------------------------------------------------------------------
    // Termination + correctness across the threshold space
    // ------------------------------------------------------------------

    [Test]
    public async Task FindsHighestPassingForEveryThresholdInRange()
    {
        // For every possible threshold in [8..55], the search should
        // converge on exactly that threshold — the highest passing CQ.
        for (int threshold = 8; threshold <= 55; threshold++)
        {
            int? result = await CqBinarySearch.FindHighestPassingAsync(
                minCq: 8, maxCq: 55, probe: Threshold(threshold));
            Assert.That(result, Is.EqualTo(threshold), $"threshold={threshold}");
        }
    }

    [Test]
    public async Task ReturnsNullWhenNoCqPasses()
    {
        // Threshold below MinCq → predicate returns false for every probe.
        int? result = await CqBinarySearch.FindHighestPassingAsync(
            minCq: 8, maxCq: 55, probe: Threshold(threshold: 7));
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task ReturnsMaxCqWhenEveryCqPasses()
    {
        // Threshold above MaxCq → predicate returns true for every probe.
        int? result = await CqBinarySearch.FindHighestPassingAsync(
            minCq: 8, maxCq: 55, probe: Threshold(threshold: 100));
        Assert.That(result, Is.EqualTo(55));
    }

    // ------------------------------------------------------------------
    // Specific arithmetic checks for the user-visible "first probe = 32"
    // promise on the default range.
    // ------------------------------------------------------------------

    [Test]
    public async Task FirstProbeIsMidpointOfRange()
    {
        // Default [8, 55] — the upper-biased midpoint should land on 32.
        List<int> probes = [];
        await CqBinarySearch.FindHighestPassingAsync(
            minCq: 8, maxCq: 55, probe: Threshold(threshold: 24, log: probes));
        Assert.That(probes[0], Is.EqualTo(32),
            "Default [8, 55] range must start probing at CQ=32 (the search midpoint).");
    }

    [Test]
    public async Task FirstProbeIsUpperMidWhenOddSpan()
    {
        // [10, 13]: span 4, midpoint biased UP rounds to 12.
        List<int> probes = [];
        await CqBinarySearch.FindHighestPassingAsync(
            minCq: 10, maxCq: 13, probe: Threshold(threshold: 100, log: probes));
        Assert.That(probes[0], Is.EqualTo(12));
    }

    // ------------------------------------------------------------------
    // Convergence bound — never more than ceil(log2(N+1)) probes
    // ------------------------------------------------------------------

    [Test]
    public async Task ProbeCountIsLogarithmic()
    {
        // [8, 55] is a span of 48 values; log2(48) ≈ 5.58 → at most 6 probes.
        for (int threshold = 8; threshold <= 55; threshold++)
        {
            List<int> probes = [];
            await CqBinarySearch.FindHighestPassingAsync(
                minCq: 8, maxCq: 55, probe: Threshold(threshold, probes));
            Assert.That(probes.Count, Is.LessThanOrEqualTo(6),
                $"threshold={threshold}: expected ≤6 probes, got {probes.Count} ({string.Join(",", probes)})");
        }
    }

    // ------------------------------------------------------------------
    // No CQ is ever probed twice
    // ------------------------------------------------------------------

    [Test]
    public async Task NoCqIsProbedTwice()
    {
        for (int threshold = 8; threshold <= 55; threshold++)
        {
            List<int> probes = [];
            await CqBinarySearch.FindHighestPassingAsync(
                minCq: 8, maxCq: 55, probe: Threshold(threshold, probes));
            Assert.That(probes.Distinct().Count(), Is.EqualTo(probes.Count),
                $"threshold={threshold}: duplicate probe in {string.Join(",", probes)}");
        }
    }

    // ------------------------------------------------------------------
    // Edge cases: single-value range, full NVENC range, identical bounds
    // ------------------------------------------------------------------

    [Test]
    public async Task SingleValueRangePasses()
    {
        int? result = await CqBinarySearch.FindHighestPassingAsync(
            minCq: 30, maxCq: 30, probe: Threshold(threshold: 50));
        Assert.That(result, Is.EqualTo(30));
    }

    [Test]
    public async Task SingleValueRangeFails()
    {
        int? result = await CqBinarySearch.FindHighestPassingAsync(
            minCq: 30, maxCq: 30, probe: Threshold(threshold: 20));
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task FullNvencRange()
    {
        // 0..63 is the full NVENC AV1 range.  log2(64) = 6 → at most 6 probes.
        List<int> probes = [];
        int? result = await CqBinarySearch.FindHighestPassingAsync(
            minCq: 0, maxCq: 63, probe: Threshold(threshold: 47, log: probes));
        Assert.That(result, Is.EqualTo(47));
        Assert.That(probes.Count, Is.LessThanOrEqualTo(6));
    }

    [Test]
    public void RejectsInvertedRange()
    {
        Assert.ThrowsAsync<ArgumentException>(async () =>
            await CqBinarySearch.FindHighestPassingAsync(
                minCq: 50, maxCq: 10, probe: _ => Task.FromResult(true)));
    }

    [Test]
    public void HonoursCancellation()
    {
        CancellationTokenSource cts = new();
        cts.Cancel();
        Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await CqBinarySearch.FindHighestPassingAsync(
                minCq: 8, maxCq: 55, probe: _ => Task.FromResult(true), ct: cts.Token));
    }
}
