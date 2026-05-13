namespace MkvHelper.Tests.Unit;

[TestFixture]
[Category("Unit")]
public class ConfigCqRangeTests
{
    private static Config NewConfig() => new();

    // ------------------------------------------------------------------
    // Tier mapping at the boundary pixels.  Each boundary is OR'd on
    // width vs height, so a stream that meets either dimension threshold
    // bumps up a tier.
    // ------------------------------------------------------------------

    [TestCase(3840, 2160, "UHD")]
    [TestCase(3840, 1080, "UHD")]
    [TestCase(1920, 2160, "UHD")]
    [TestCase(7680, 4320, "UHD")]
    [TestCase(1920, 1080, "1080p")]
    [TestCase(1920,  720, "1080p")]
    [TestCase(1280, 1080, "1080p")]
    [TestCase(3839, 2159, "1080p")]
    [TestCase(1280,  720, "720p")]
    [TestCase(1280,  480, "720p")]
    [TestCase( 720,  720, "720p")]
    [TestCase(1919, 1079, "720p")]
    [TestCase( 720,  480, "SD")]
    [TestCase( 720,  576, "SD")]
    [TestCase(1279,  719, "SD")]
    [TestCase(   1,    1, "SD")]
    public void TierMappingByResolution(int width, int height, string expectedTier)
    {
        Config cfg = NewConfig();
        (int _, int _, string tier) = cfg.ResolveCqRange(width, height);
        Assert.That(tier, Is.EqualTo(expectedTier),
            $"resolution {width}×{height} should map to {expectedTier} tier");
    }

    // ------------------------------------------------------------------
    // Null/zero dimensions — falls into SD (the safest default, since SD
    // bounds are valid for any source: a degenerate stream still produces
    // a valid encode at higher CQ).
    // ------------------------------------------------------------------

    [Test]
    public void NullDimensionsFallToSd()
    {
        Config cfg = NewConfig();
        (int min, int max, string tier) = cfg.ResolveCqRange(null, null);
        Assert.That(tier, Is.EqualTo("SD"));
        Assert.That(min, Is.EqualTo(cfg.MinCqSd));
        Assert.That(max, Is.EqualTo(cfg.MaxCqSd));
    }

    [Test]
    public void ZeroDimensionsFallToSd()
    {
        Config cfg = NewConfig();
        (int _, int _, string tier) = cfg.ResolveCqRange(0, 0);
        Assert.That(tier, Is.EqualTo("SD"));
    }

    // ------------------------------------------------------------------
    // Bounds round-trip — resolver returns the configured properties for
    // each tier exactly, so callers can rely on round-trip equality.
    // ------------------------------------------------------------------

    [Test]
    public void BoundsRoundTripUhd()
    {
        Config cfg = NewConfig();
        (int min, int max, _) = cfg.ResolveCqRange(3840, 2160);
        Assert.That(min, Is.EqualTo(cfg.MinCqUhd));
        Assert.That(max, Is.EqualTo(cfg.MaxCqUhd));
    }

    [Test]
    public void BoundsRoundTripFhd()
    {
        Config cfg = NewConfig();
        (int min, int max, _) = cfg.ResolveCqRange(1920, 1080);
        Assert.That(min, Is.EqualTo(cfg.MinCqFhd));
        Assert.That(max, Is.EqualTo(cfg.MaxCqFhd));
    }

    [Test]
    public void BoundsRoundTripHd()
    {
        Config cfg = NewConfig();
        (int min, int max, _) = cfg.ResolveCqRange(1280, 720);
        Assert.That(min, Is.EqualTo(cfg.MinCqHd));
        Assert.That(max, Is.EqualTo(cfg.MaxCqHd));
    }

    [Test]
    public void BoundsRoundTripSd()
    {
        Config cfg = NewConfig();
        (int min, int max, _) = cfg.ResolveCqRange(720, 480);
        Assert.That(min, Is.EqualTo(cfg.MinCqSd));
        Assert.That(max, Is.EqualTo(cfg.MaxCqSd));
    }

    // ------------------------------------------------------------------
    // Probe-budget invariant: every default tier must fit in ≤ 5 probes.
    // This invariant is load-bearing — the whole point of resolution-
    // tiered ranges is to keep the per-tier window inside the 5-probe
    // budget that bounds Phase 2 cost.  A regression here would silently
    // re-introduce 6-probe searches on some content types.
    // ------------------------------------------------------------------

    [TestCase(3840, 2160)]   // UHD
    [TestCase(1920, 1080)]   // 1080p
    [TestCase(1280,  720)]   // 720p
    [TestCase( 720,  480)]   // SD
    public void TierFitsInFiveProbes(int width, int height)
    {
        Config cfg = NewConfig();
        (int min, int max, string tier) = cfg.ResolveCqRange(width, height);

        // Convergence bound: ceil(log2(max - min + 2)) probes worst case.
        int worstCase = (int)Math.Ceiling(Math.Log2(max - min + 2));
        Assert.That(worstCase, Is.LessThanOrEqualTo(5),
            $"{tier} range [{min}, {max}] needs {worstCase} probes worst case " +
            "— must be ≤ 5 to honour the configured probe budget");
    }

    // ------------------------------------------------------------------
    // First-probe sanity — every tier's first probe should land in a
    // sensible region (not at the bounds, not zero).  The exact value is
    // up-to-the-mid formula `(lo + hi + 1) / 2`, so this just sanity-
    // checks the docstring claims that drove the tier sizing.
    // ------------------------------------------------------------------

    [TestCase(3840, 2160, 40)]
    [TestCase(1920, 1080, 43)]
    [TestCase(1280,  720, 47)]
    [TestCase( 720,  480, 51)]
    public void FirstProbeLandsAtDocumentedValue(int width, int height, int expectedFirstProbe)
    {
        Config cfg = NewConfig();
        (int min, int max, _) = cfg.ResolveCqRange(width, height);
        int firstProbe = (min + max + 1) / 2;
        Assert.That(firstProbe, Is.EqualTo(expectedFirstProbe),
            $"first probe for [{min}, {max}] expected {expectedFirstProbe}, got {firstProbe}");
    }
}
