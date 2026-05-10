namespace CompressMkv.Tests;

[TestFixture]
public class FpsTests
{
    // ---- Parsing ----

    [TestCase("30000/1001", 30000, 1001)]
    [TestCase("24000/1001", 24000, 1001)]
    [TestCase("60000/1001", 60000, 1001)]
    [TestCase("30/1",        30,    1)]
    [TestCase("25/1",        25,    1)]
    [TestCase("60/1",        60,    1)]
    [TestCase("60/2",        30,    1)]   // canonical reduction (60/2 → 30/1)
    public void TryParse_AcceptsCanonicalFractions(string input, int expectedNum, int expectedDen)
    {
        Assert.That(Fps.TryParse(input, out var fps), Is.True);
        Assert.Multiple(() =>
        {
            Assert.That(fps.Numerator, Is.EqualTo(expectedNum));
            Assert.That(fps.Denominator, Is.EqualTo(expectedDen));
        });
    }

    [TestCase("29.97")]
    [TestCase("23.976")]
    [TestCase("59.94")]
    public void TryParse_AcceptsBareFloats(string input)
    {
        Assert.That(Fps.TryParse(input, out var fps), Is.True);
        Assert.That(fps.AsDouble, Is.GreaterThan(0));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("0/0")]
    [TestCase("nope")]
    [TestCase("30/0")]
    [TestCase("0/1")]
    [TestCase("-30/1")]
    public void TryParse_RejectsInvalidInputs(string? input)
    {
        Assert.That(Fps.TryParse(input, out _), Is.False);
    }

    // ---- Equality (strict canonical fraction match) ----

    [Test]
    public void Equals_TreatsReducedAndUnreducedFractionsAsEqual()
    {
        // The constructor reduces inputs, so 60/2 and 30/1 are the same value.
        Assert.That(Fps.FromRatio(60, 2), Is.EqualTo(Fps.FromRatio(30, 1)));
    }

    [Test]
    public void Equals_DistinguishesNtscThirtyFromTrueThirty()
    {
        // The whole point of the strict-fraction comparison: 30000/1001 ≠ 30/1.
        Assert.That(Fps.Ntsc30, Is.Not.EqualTo(Fps.Flat30));
    }

    // ---- Approximate equality (used by the IVTC source-rate guard) ----

    [Test]
    public void IsApproximately_AcceptsFloatRoundedNtscAtDefaultTolerance()
    {
        // ffprobe sometimes reports "29.97" instead of "30000/1001".
        // 29.97 rounds to 2997/100 internally; AsDouble = 29.970 vs 29.97003 (NTSC).
        Assert.That(Fps.TryParse("29.97", out var rounded), Is.True);
        Assert.That(rounded.IsApproximately(Fps.Ntsc30), Is.True);
    }

    [Test]
    public void IsApproximately_RejectsTrueThirtyAtDefaultTolerance()
    {
        // The damaging case: a true-30p web/screen source must NOT match NTSC-thirty,
        // or the IVTC chain would fire and drop frames.
        Assert.That(Fps.Flat30.IsApproximately(Fps.Ntsc30), Is.False);
    }

    [Test]
    public void IsApproximately_RejectsTrueTwentyFourAtDefaultTolerance()
    {
        Assert.That(Fps.Film24.IsApproximately(Fps.Ntsc24), Is.False);
    }

    [Test]
    public void IsApproximately_RejectsTrueSixtyAtDefaultTolerance()
    {
        Assert.That(Fps.Flat60.IsApproximately(Fps.Ntsc60), Is.False);
    }

    [Test]
    public void IsApproximately_RejectsPalAtNtscRates()
    {
        Assert.That(Fps.Pal25.IsApproximately(Fps.Ntsc30), Is.False);
        Assert.That(Fps.Pal50.IsApproximately(Fps.Ntsc60), Is.False);
    }

    // ---- Domain helpers used by the source-rate guard ----

    [Test]
    public void IsNtscThirty_AcceptsExactNtscFractionAndRoundedFloat()
    {
        Assert.That(Fps.Ntsc30.IsNtscThirty(), Is.True);
        Assert.That(Fps.TryParse("29.97", out var rounded), Is.True);
        Assert.That(rounded.IsNtscThirty(), Is.True);
    }

    [Test]
    public void IsNtscThirty_RejectsFlatThirtyAndPal()
    {
        Assert.That(Fps.Flat30.IsNtscThirty(), Is.False);
        Assert.That(Fps.Pal25.IsNtscThirty(), Is.False);
        Assert.That(Fps.Pal50.IsNtscThirty(), Is.False);
        Assert.That(Fps.Film24.IsNtscThirty(), Is.False);
        Assert.That(Fps.Ntsc24.IsNtscThirty(), Is.False);  // 23.976 ≠ 29.97
    }

    [Test]
    public void IsNtscFamily_AcceptsAllThreeNtscRates()
    {
        Assert.That(Fps.Ntsc30.IsNtscFamily(), Is.True);
        Assert.That(Fps.Ntsc24.IsNtscFamily(), Is.True);
        Assert.That(Fps.Ntsc60.IsNtscFamily(), Is.True);
    }

    [Test]
    public void IsNtscFamily_RejectsFlatRatesAndPal()
    {
        Assert.That(Fps.Flat30.IsNtscFamily(), Is.False);
        Assert.That(Fps.Flat60.IsNtscFamily(), Is.False);
        Assert.That(Fps.Film24.IsNtscFamily(), Is.False);
        Assert.That(Fps.Pal25.IsNtscFamily(), Is.False);
        Assert.That(Fps.Pal50.IsNtscFamily(), Is.False);
    }

    // ---- ffmpeg-format string output ----

    [Test]
    public void ToString_ProducesFractionForNtscRates()
    {
        Assert.That(Fps.Ntsc30.ToString(), Is.EqualTo("30000/1001"));
        Assert.That(Fps.Ntsc24.ToString(), Is.EqualTo("24000/1001"));
        Assert.That(Fps.Ntsc60.ToString(), Is.EqualTo("60000/1001"));
    }

    [Test]
    public void ToString_ProducesIntegerForWholeRates()
    {
        Assert.That(Fps.Flat30.ToString(), Is.EqualTo("30"));
        Assert.That(Fps.Pal25.ToString(), Is.EqualTo("25"));
        Assert.That(Fps.Pal50.ToString(), Is.EqualTo("50"));
    }
}
