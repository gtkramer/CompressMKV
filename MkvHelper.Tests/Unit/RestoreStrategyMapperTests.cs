namespace MkvHelper.Tests.Unit;

/// <summary>
/// Decision-table tests for §7.2.3 action selection.  Each test corresponds
/// to one row in the mapper's switch expression.
/// </summary>
[TestFixture]
public class RestoreStrategyMapperTests
{
    // ---- §7.2.3.1 Progressive ----

    [Test]
    public void Progressive_AnySource_NoFilter()
    {
        ContentDetectionResult d = MakeDetection(ContentType.Progressive, sourceFps: Fps.Ntsc30);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode,        Is.EqualTo(RestoreMode.None));
        Assert.That(r.FilterGraph, Is.Empty);
        Assert.That(r.OutputFps,   Is.Null);
    }

    // ---- §7.2.3.3 Interlaced ----

    [Test]
    public void Interlaced_AnySource_DeinterlaceAtNativeRate()
    {
        ContentDetectionResult d = MakeDetection(ContentType.Interlaced, sourceFps: Fps.Ntsc30,
                              parity: FieldParity.Tff);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.Deinterlace));
        Assert.That(r.FilterGraph, Does.Contain("bwdif"));
        Assert.That(r.OutputFps, Is.Null);
    }

    [Test]
    public void Interlaced_PalSource_StillDeinterlacesAtNativeRate()
    {
        // §7.2.3.3 doesn't require NTSC — bwdif at native rate works for PAL too.
        ContentDetectionResult d = MakeDetection(ContentType.Interlaced, sourceFps: Fps.Pal25,
                              parity: FieldParity.Tff);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.Deinterlace));
        Assert.That(r.OutputFps, Is.Null);
    }

    // ---- §7.2.3.2 Telecined ----

    [Test]
    public void Telecined_NtscThirtyCfrSource_AppliesIvtc()
    {
        ContentDetectionResult d = MakeDetection(ContentType.Telecined, sourceFps: Fps.Ntsc30, isCfr: true);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.Ivtc));
        Assert.That(r.OutputFps, Is.EqualTo(Fps.Ntsc24));
        Assert.That(r.FilterGraph, Does.Contain("fieldmatch"));
        Assert.That(r.FilterGraph, Does.Contain("decimate"));
    }

    [Test]
    public void Telecined_TrueThirtySource_PassesThrough()
    {
        // The damaging case: cadence detected but source is true 30p — IVTC's
        // -r 24000/1001 would drop frames.  Mapper must skip IVTC.
        ContentDetectionResult d = MakeDetection(ContentType.Telecined, sourceFps: Fps.Flat30, isCfr: true);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.None));
        Assert.That(r.FilterGraph, Is.Empty);
        Assert.That(r.OutputFps, Is.Null);
    }

    [Test]
    public void Telecined_VfrSource_PassesThrough()
    {
        ContentDetectionResult d = MakeDetection(ContentType.Telecined, sourceFps: Fps.Ntsc30, isCfr: false);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.None));
        Assert.That(r.Notes, Does.Contain("variable frame rate"));
    }

    [Test]
    public void Telecined_PalSource_PassesThrough()
    {
        ContentDetectionResult d = MakeDetection(ContentType.Telecined, sourceFps: Fps.Pal25, isCfr: true);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.None));
    }

    // ---- §7.2.3.4 MixedProgressiveTelecine ----

    [Test]
    public void MixedProgressiveTelecine_NtscThirtyCfr_AppliesIvtc()
    {
        ContentDetectionResult d = MakeDetection(ContentType.MixedProgressiveTelecine,
                              sourceFps: Fps.Ntsc30, isCfr: true);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.Ivtc));
        Assert.That(r.OutputFps, Is.EqualTo(Fps.Ntsc24));
    }

    [Test]
    public void MixedProgressiveTelecine_NonNtscSource_PassesThrough()
    {
        ContentDetectionResult d = MakeDetection(ContentType.MixedProgressiveTelecine,
                              sourceFps: Fps.Film24, isCfr: true);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.None));
    }

    // ---- §7.2.3.5 MixedProgressiveInterlaced ----

    [Test]
    public void MixedProgInt_HighProgFracOnNtscThirty_FavorsIvtc()
    {
        // Per guide §7.2.3.5: ≥90% prog → favor progressive (IVTC chain).
        ContentDetectionResult d = MakeDetection(ContentType.MixedProgressiveInterlaced,
                              sourceFps: Fps.Ntsc30, isCfr: true, progFrac: 0.95);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.Ivtc));
        Assert.That(r.OutputFps, Is.EqualTo(Fps.Ntsc24));
    }

    [Test]
    public void MixedProgInt_LowProgFracOnNtscThirty_DeinterlacesAll()
    {
        // <90% prog → deinterlace all (compromise option of §7.2.3.5).
        ContentDetectionResult d = MakeDetection(ContentType.MixedProgressiveInterlaced,
                              sourceFps: Fps.Ntsc30, isCfr: true, progFrac: 0.50);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.Deinterlace));
        Assert.That(r.OutputFps, Is.Null);
    }

    [Test]
    public void MixedProgInt_HighProgFracOnTrueThirty_PassesThrough()
    {
        // High progFrac with sparse idet noise on a non-NTSC source: pass through
        // rather than damage the rate via IVTC.
        ContentDetectionResult d = MakeDetection(ContentType.MixedProgressiveInterlaced,
                              sourceFps: Fps.Flat30, isCfr: true, progFrac: 0.95);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.None));
        Assert.That(r.Notes, Does.Contain("Mostly progressive"));
        Assert.That(r.Notes, Does.Contain("Passing through"));
    }

    [Test]
    public void MixedProgInt_LowProgFracOnTrueThirty_DeinterlacesAtNativeRate()
    {
        // Real interlaced content at true 30p: deinterlace at native rate is safe.
        ContentDetectionResult d = MakeDetection(ContentType.MixedProgressiveInterlaced,
                              sourceFps: Fps.Flat30, isCfr: true, progFrac: 0.50);
        RestoreDecision r = RestoreStrategyMapper.MapToRestore(d);

        Assert.That(r.Mode, Is.EqualTo(RestoreMode.Deinterlace));
        Assert.That(r.OutputFps, Is.Null);
    }

    // ---- helpers ----

    private static ContentDetectionResult MakeDetection(
        ContentType type,
        Fps? sourceFps = null,
        FieldParity parity = FieldParity.Tff,
        bool isCfr = true,
        double progFrac = 0.50)
    => new()
    {
        ContentType = type,
        Confidence = 0.85,
        Reason = "test",
        DetectedParity = parity,
        SourceFps = sourceFps,
        IsNtscFamilyFps = sourceFps?.IsNtscFamily() ?? false,
        SourceIsLikelyCfr = isCfr,
        GlobalProgressiveFraction = progFrac,
    };
}
