namespace MkvHelper.Tests;

[TestFixture]
public class SamplerTests
{
    private const int TestSeed = 12345;
    private const int TargetCount = 16;
    private const int WindowSec = 12;

    // ---- Long content (≥ count × windowSec): standard sampling ----

    [Test]
    public void LongVideo_GetsRequestedSampleCount()
    {
        var s = new Sampler(TestSeed);
        var windows = s.StratifiedRandomWindows(durationSeconds: 7200, TargetCount, WindowSec);

        Assert.That(windows, Has.Count.EqualTo(TargetCount));
    }

    [Test]
    public void LongVideo_AllWindowsAreFullLength()
    {
        var s = new Sampler(TestSeed);
        var windows = s.StratifiedRandomWindows(7200, TargetCount, WindowSec);

        Assert.That(windows.All(w => w.LengthSeconds == WindowSec), Is.True);
    }

    [Test]
    public void LongVideo_NoWindowsOverlap()
    {
        var s = new Sampler(TestSeed);
        var windows = s.StratifiedRandomWindows(7200, TargetCount, WindowSec)
                       .OrderBy(w => w.StartSeconds).ToList();

        for (int i = 1; i < windows.Count; i++)
        {
            double prevEnd = windows[i - 1].StartSeconds + windows[i - 1].LengthSeconds;
            Assert.That(windows[i].StartSeconds, Is.GreaterThanOrEqualTo(prevEnd),
                $"Window {i} starts at {windows[i].StartSeconds}s, " +
                $"previous ends at {prevEnd}s — overlap detected.");
        }
    }

    [Test]
    public void LongVideo_AllWindowsFitInsideSource()
    {
        var s = new Sampler(TestSeed);
        var windows = s.StratifiedRandomWindows(7200, TargetCount, WindowSec);

        Assert.That(windows.All(w => w.StartSeconds >= 0 && w.StartSeconds + w.LengthSeconds <= 7200), Is.True);
    }

    // ---- Medium content (2×windowSec ≤ D < count×windowSec): scaled count ----

    [TestCase(192, 16)]   // exactly count × windowSec — full count, no slack
    [TestCase(180, 15)]   // just under threshold — count drops by 1
    [TestCase(120, 10)]   // half the threshold — half the windows
    [TestCase( 60,  5)]   // a quarter — quarter the windows
    [TestCase( 36,  3)]   // 3 windows of 12s each fit
    [TestCase( 24,  2)]   // 2 windows back-to-back
    public void MediumVideo_ScalesCountToFit(double duration, int expectedCount)
    {
        var s = new Sampler(TestSeed);
        var windows = s.StratifiedRandomWindows(duration, TargetCount, WindowSec);

        Assert.That(windows, Has.Count.EqualTo(expectedCount));
        Assert.That(windows.All(w => w.LengthSeconds == WindowSec), Is.True,
            "Medium-length sources should keep window length fixed; only count scales.");
    }

    [Test]
    public void MediumVideo_NoWindowsOverlap()
    {
        var s = new Sampler(TestSeed);
        var windows = s.StratifiedRandomWindows(60, TargetCount, WindowSec)
                       .OrderBy(w => w.StartSeconds).ToList();

        for (int i = 1; i < windows.Count; i++)
        {
            double prevEnd = windows[i - 1].StartSeconds + windows[i - 1].LengthSeconds;
            Assert.That(windows[i].StartSeconds, Is.GreaterThanOrEqualTo(prevEnd));
        }
    }

    // ---- Short content (D < 2×windowSec): single full-content window ----

    [TestCase(20)]   // < 24
    [TestCase(15)]
    [TestCase( 8)]
    [TestCase( 1)]
    public void ShortVideo_ReturnsOneFullContentWindow(double duration)
    {
        var s = new Sampler(TestSeed);
        var windows = s.StratifiedRandomWindows(duration, TargetCount, WindowSec);

        Assert.That(windows, Has.Count.EqualTo(1));
        Assert.That(windows[0].StartSeconds, Is.EqualTo(0));
        Assert.That(windows[0].LengthSeconds, Is.EqualTo(duration),
            "Short content should be measured in full, not sampled.");
    }

    // ---- Determinism: same seed → same windows ----

    [Test]
    public void SameSeed_ProducesSameWindows()
    {
        var s1 = new Sampler(TestSeed);
        var s2 = new Sampler(TestSeed);

        var w1 = s1.StratifiedRandomWindows(7200, TargetCount, WindowSec);
        var w2 = s2.StratifiedRandomWindows(7200, TargetCount, WindowSec);

        Assert.That(w1.Count, Is.EqualTo(w2.Count));
        for (int i = 0; i < w1.Count; i++)
        {
            Assert.That(w1[i].StartSeconds, Is.EqualTo(w2[i].StartSeconds));
            Assert.That(w1[i].LengthSeconds, Is.EqualTo(w2[i].LengthSeconds));
        }
    }

    [Test]
    public void DifferentSeeds_ProduceDifferentWindows()
    {
        var s1 = new Sampler(seed: 1);
        var s2 = new Sampler(seed: 2);

        var w1 = s1.StratifiedRandomWindows(7200, TargetCount, WindowSec);
        var w2 = s2.StratifiedRandomWindows(7200, TargetCount, WindowSec);

        // At least one window should differ in start time.
        bool anyDiffer = w1.Zip(w2, (a, b) => a.StartSeconds != b.StartSeconds).Any(x => x);
        Assert.That(anyDiffer, Is.True);
    }

    // ---- Stratification: each segment gets exactly one window ----

    [Test]
    public void LongVideo_OneWindowPerSegment()
    {
        var s = new Sampler(TestSeed);
        var windows = s.StratifiedRandomWindows(7200, TargetCount, WindowSec);

        double segLen = 7200.0 / TargetCount;
        for (int i = 0; i < TargetCount; i++)
        {
            double segStart = i * segLen;
            double segEnd = segStart + segLen;
            int inSegment = windows.Count(w => w.StartSeconds >= segStart && w.StartSeconds < segEnd);
            Assert.That(inSegment, Is.EqualTo(1),
                $"Segment {i} ({segStart}–{segEnd}s) should have exactly 1 window, has {inSegment}.");
        }
    }

    // ---- Defensive: invalid arguments ----

    [Test]
    public void ZeroDuration_Throws()
    {
        var s = new Sampler(TestSeed);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            s.StratifiedRandomWindows(0, TargetCount, WindowSec));
    }

    [Test]
    public void ZeroCount_Throws()
    {
        var s = new Sampler(TestSeed);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            s.StratifiedRandomWindows(7200, 0, WindowSec));
    }

    [Test]
    public void ZeroWindowSeconds_Throws()
    {
        var s = new Sampler(TestSeed);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            s.StratifiedRandomWindows(7200, TargetCount, 0));
    }
}
