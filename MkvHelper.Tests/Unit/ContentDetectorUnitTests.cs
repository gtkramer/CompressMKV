namespace MkvHelper.Tests.Unit;

/// <summary>
/// Pure-logic tests for ContentDetector — no ffmpeg invocations.
/// These exercise the pieces that consume ffmpeg's output (idet aggregate
/// parser, cadence math, classifier decision tree, parity logic).
/// </summary>
[TestFixture]
public class ContentDetectorUnitTests
{
    // ===================================================================
    // ComputeCadenceMatchRate: fraction of 5-frame sliding windows
    // containing exactly 3P + 2I (the 3:2 pulldown signature).
    // ===================================================================

    [Test]
    public void CadenceRate_PerfectTelecineCycle_ApproachesOne()
    {
        // PPPII repeated 100x = 500 frames.  Every 5-frame window matches.
        List<FrameFlag> frames = TimesPattern(
        [
            FrameFlag.Progressive, FrameFlag.Progressive, FrameFlag.Progressive,
            FrameFlag.Interlaced,  FrameFlag.Interlaced
        ], 100);

        double rate = ContentDetector.ComputeCadenceMatchRate(frames);
        Assert.That(rate, Is.GreaterThanOrEqualTo(0.99));
    }

    [Test]
    public void CadenceRate_AllProgressive_IsZero()
    {
        List<FrameFlag> frames = Enumerable.Repeat(FrameFlag.Progressive, 500).ToList();
        Assert.That(ContentDetector.ComputeCadenceMatchRate(frames), Is.EqualTo(0.0));
    }

    [Test]
    public void CadenceRate_AllInterlaced_IsZero()
    {
        List<FrameFlag> frames = Enumerable.Repeat(FrameFlag.Interlaced, 500).ToList();
        Assert.That(ContentDetector.ComputeCadenceMatchRate(frames), Is.EqualTo(0.0));
    }

    [Test]
    public void CadenceRate_TooFewFrames_IsZero()
    {
        List<FrameFlag> frames =
        [
            FrameFlag.Progressive, FrameFlag.Progressive, FrameFlag.Interlaced
        ];
        Assert.That(ContentDetector.ComputeCadenceMatchRate(frames), Is.EqualTo(0.0));
    }

    // ===================================================================
    // ComputeIFramesInCadenceRatio: fraction of I frames whose centered
    // 5-frame neighborhood is exactly 3P + 2I (the §7.2.2.4↔§7.2.2.5
    // discriminator).
    // ===================================================================

    [Test]
    public void IFramesInCadence_PureTelecine_ApproachesOne()
    {
        List<FrameFlag> frames = TimesPattern(
        [
            FrameFlag.Progressive, FrameFlag.Progressive, FrameFlag.Progressive,
            FrameFlag.Interlaced,  FrameFlag.Interlaced
        ], 100);

        double ratio = ContentDetector.ComputeIFramesInCadenceRatio(frames);
        Assert.That(ratio, Is.GreaterThanOrEqualTo(0.95));
    }

    [Test]
    public void IFramesInCadence_NativeInterlaceCluster_NearZero()
    {
        // 100 progressive frames followed by a long run of interlaced frames.
        // No I frame sits inside a 3P+2I window — they're all surrounded by I.
        List<FrameFlag> frames = Enumerable.Repeat(FrameFlag.Progressive, 100)
            .Concat(Enumerable.Repeat(FrameFlag.Interlaced, 400))
            .ToList();

        double ratio = ContentDetector.ComputeIFramesInCadenceRatio(frames);
        Assert.That(ratio, Is.LessThan(0.05));
    }

    [Test]
    public void IFramesInCadence_NoInterlacedFrames_IsZero()
    {
        List<FrameFlag> frames = Enumerable.Repeat(FrameFlag.Progressive, 500).ToList();
        Assert.That(ContentDetector.ComputeIFramesInCadenceRatio(frames), Is.EqualTo(0.0));
    }

    // ===================================================================
    // DetectParity: TFF/BFF dominance with NTSC tiebreaker.
    // ===================================================================

    [Test]
    public void DetectParity_StrongTff_ReturnsTff()
    {
        (FieldParity parity, bool ntscFallback) = ContentDetector.DetectParity(
            rawTff: 9500, rawBff: 500, isNtsc: true, ffprobeParity: FieldParity.Auto);
        Assert.That(parity, Is.EqualTo(FieldParity.Tff));
        Assert.That(ntscFallback, Is.False);
    }

    [Test]
    public void DetectParity_StrongBff_ReturnsBff()
    {
        (FieldParity parity, bool ntscFallback) = ContentDetector.DetectParity(
            rawTff: 500, rawBff: 9500, isNtsc: true, ffprobeParity: FieldParity.Auto);
        Assert.That(parity, Is.EqualTo(FieldParity.Bff));
        Assert.That(ntscFallback, Is.False);
    }

    [Test]
    public void DetectParity_NoInterlacedFrames_NtscFallsBackToTff()
    {
        (FieldParity parity, bool ntscFallback) = ContentDetector.DetectParity(
            rawTff: 0, rawBff: 0, isNtsc: true, ffprobeParity: FieldParity.Auto);
        Assert.That(parity, Is.EqualTo(FieldParity.Tff));
        Assert.That(ntscFallback, Is.True);
    }

    [Test]
    public void DetectParity_AmbiguousIdet_DefersToConfidentFfprobe()
    {
        (FieldParity parity, bool ntscFallback) = ContentDetector.DetectParity(
            rawTff: 600, rawBff: 400, isNtsc: false, ffprobeParity: FieldParity.Bff);
        Assert.That(parity, Is.EqualTo(FieldParity.Bff));
        Assert.That(ntscFallback, Is.False);
    }

    [Test]
    public void DetectParity_AmbiguousIdet_NonNtscNoFfprobe_ReturnsAuto()
    {
        (FieldParity parity, bool ntscFallback) = ContentDetector.DetectParity(
            rawTff: 600, rawBff: 400, isNtsc: false, ffprobeParity: FieldParity.Auto);
        Assert.That(parity, Is.EqualTo(FieldParity.Auto));
        Assert.That(ntscFallback, Is.False);
    }

    // ===================================================================
    // Classify: maps three metrics onto one of five §7.2.2 categories.
    // ===================================================================

    [Test]
    public void Classify_ZeroInterlaced_IsProgressive()
    {
        (ContentType type, double conf, string _, string _) = ContentDetector.Classify(
            intCount: 0, totalClassified: 24000, progFrac: 1.0, cadenceRate: 0.0,
            iInCadence: 0.0, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.Progressive));
        Assert.That(conf, Is.GreaterThan(0.95));
    }

    [Test]
    public void Classify_AllInterlaced_IsInterlaced()
    {
        (ContentType type, double _, string _, string _) = ContentDetector.Classify(
            intCount: 5000, totalClassified: 5100, progFrac: 0.02, cadenceRate: 0.0,
            iInCadence: 0.0, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.Interlaced));
    }

    [Test]
    public void Classify_HighCadence_IsTelecined()
    {
        (ContentType type, double _, string _, string _) = ContentDetector.Classify(
            intCount: 2000, totalClassified: 5000, progFrac: 0.60, cadenceRate: 0.95,
            iInCadence: 0.95, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.Telecined));
    }

    [Test]
    public void Classify_IFramesInCadence_IsMixedProgressiveTelecine()
    {
        (ContentType type, double _, string _, string _) = ContentDetector.Classify(
            intCount: 200, totalClassified: 1333, progFrac: 0.85, cadenceRate: 0.50,
            iInCadence: 0.80, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.MixedProgressiveTelecine));
    }

    [Test]
    public void Classify_LowIInCadenceWithMixedProgInt_IsMixedProgressiveInterlaced()
    {
        (ContentType type, double _, string _, string _) = ContentDetector.Classify(
            intCount: 1000, totalClassified: 2000, progFrac: 0.50, cadenceRate: 0.10,
            iInCadence: 0.05, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.MixedProgressiveInterlaced));
    }

    [Test]
    public void Classify_ParityMismatch_LowersConfidence()
    {
        (ContentType _, double withMismatch, string _, string _) = ContentDetector.Classify(
            intCount: 5000, totalClassified: 5100, progFrac: 0.02, cadenceRate: 0.0,
            iInCadence: 0.0, parityMismatch: true);
        (ContentType _, double withoutMismatch, string _, string _) = ContentDetector.Classify(
            intCount: 5000, totalClassified: 5100, progFrac: 0.02, cadenceRate: 0.0,
            iInCadence: 0.0, parityMismatch: false);
        Assert.That(withMismatch, Is.LessThan(withoutMismatch));
    }

    // ===================================================================
    // §7.2.2.1 idet noise floor: unstructured trace I-frames are absorbed
    // into Progressive (with reduced confidence) when they fall below
    // idet's known false-positive rate and carry no cadence or cluster
    // signal.  Mirrors what a human watcher would dismiss as imperceptible.
    // ===================================================================

    [Test]
    public void Classify_TraceInterlacedNoise_AbsorbedIntoProgressive()
    {
        // The Prince of Egypt UHD split-006 case: 5 I-frames in 23,690 classified
        // (0.021%), no cadence, no cluster.  Strictly the guide says "never see
        // any interlacing", but idet has known false-positive modes the guide
        // doesn't account for — this is exactly that pattern.
        (ContentType type, double conf, string reason, string _) = ContentDetector.Classify(
            intCount: 5, totalClassified: 23690, progFrac: 23685.0 / 23690.0,
            cadenceRate: 0.0, iInCadence: 0.0, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.Progressive));
        Assert.That(conf, Is.InRange(0.90, 0.96));
        Assert.That(reason, Does.Contain("noise floor"));
    }

    [Test]
    public void Classify_AbsoluteFloorAppliesToShortClips()
    {
        // Short clip (<8000 frames): the fractional floor would be <8, so the
        // absolute floor of 8 governs.  7 I-frames in a 660-frame clip is past
        // the fractional floor but within the absolute floor.
        (ContentType type, _, _, _) = ContentDetector.Classify(
            intCount: 7, totalClassified: 660, progFrac: 653.0 / 660.0,
            cadenceRate: 0.0, iInCadence: 0.0, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.Progressive));
    }

    [Test]
    public void Classify_NoiseFloorRefusesAbsorptionWhenIFramesShowCadence()
    {
        // Even a tiny intCount must not be absorbed if those frames sit in 3:2
        // cycles — that's a telecine signal, not idet noise.  iInCadence above
        // the noise ceiling forces us past the §7.2.2.1 relaxation branch.
        (ContentType type, _, _, _) = ContentDetector.Classify(
            intCount: 5, totalClassified: 23690, progFrac: 23685.0 / 23690.0,
            cadenceRate: 0.0, iInCadence: 0.80, parityMismatch: false);
        Assert.That(type, Is.Not.EqualTo(ContentType.Progressive));
    }

    [Test]
    public void Classify_NoiseFloorRefusesAbsorptionWhenCadenceRateNonzero()
    {
        (ContentType type, _, _, _) = ContentDetector.Classify(
            intCount: 5, totalClassified: 23690, progFrac: 23685.0 / 23690.0,
            cadenceRate: 0.20, iInCadence: 0.0, parityMismatch: false);
        Assert.That(type, Is.Not.EqualTo(ContentType.Progressive));
    }

    // ===================================================================
    // §7.2.2.5 positive-evidence requirement: trace I-frames past the noise
    // floor but below the verification threshold default to §7.2.2.4 per
    // §7.2.3.1 ("Unless you are sure, it is safest to treat the video as
    // mixed progressive and telecine") and footnote [3].  §7.2.2.5 only
    // fires when there are enough I-frames for iInCadence to be meaningful.
    // ===================================================================

    [Test]
    public void Classify_PastNoiseFloorButBelowVerification_DefaultsToSafeMixed()
    {
        // 50 I-frames in 24,000 classified (0.21%) — past the 0.1% noise floor
        // but below the 0.5% / 100-frame §7.2.2.5 verification threshold.  No
        // cadence, no clustering signal worth trusting.  Guide's safe default
        // is §7.2.2.4 (pullup).
        (ContentType type, double conf, string reason, string _) = ContentDetector.Classify(
            intCount: 50, totalClassified: 24000, progFrac: 23950.0 / 24000.0,
            cadenceRate: 0.02, iInCadence: 0.10, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.MixedProgressiveTelecine));
        Assert.That(conf, Is.LessThan(0.75));
        Assert.That(reason, Does.Contain("§7.2.3.1"));
    }

    [Test]
    public void Classify_SubstantialIFramesNoCadence_IsMixedProgressiveInterlaced()
    {
        // 500 I-frames clear of the verification threshold, none in cadence —
        // positive evidence for §7.2.2.5 (genuine interlaced sections).
        (ContentType type, _, _, _) = ContentDetector.Classify(
            intCount: 500, totalClassified: 24000, progFrac: 23500.0 / 24000.0,
            cadenceRate: 0.02, iInCadence: 0.05, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.MixedProgressiveInterlaced));
    }

    [Test]
    public void Classify_AbsoluteIFrameFloorTriggersVerification()
    {
        // Short clip: 100 I-frames hits the absolute floor (intCount >=
        // MixedInterlaceMinIFrames) even though it's only ~17% of the file,
        // which is on the fractional boundary.  Verification fires.
        (ContentType type, _, _, _) = ContentDetector.Classify(
            intCount: 100, totalClassified: 600, progFrac: 500.0 / 600.0,
            cadenceRate: 0.05, iInCadence: 0.10, parityMismatch: false);
        Assert.That(type, Is.EqualTo(ContentType.MixedProgressiveInterlaced));
    }

    // ===================================================================
    // ParseIdetAggregate: pulls "Multi frame detection: ..." from stderr.
    // ===================================================================

    [Test]
    public void ParseIdetAggregate_MatchesRealisticStderr()
    {
        // Sample stderr line shape emitted by ffmpeg's idet filter at end-of-stream.
        const string stderr = """
            [Parsed_idet_0 @ 0x55a1234] Repeated Fields: Neither: 248594 Top: 1 Bottom: 0
            [Parsed_idet_0 @ 0x55a1234] Single frame detection: TFF: 1 BFF: 0 Progressive: 248594 Undetermined: 0
            [Parsed_idet_0 @ 0x55a1234] Multi frame detection: TFF: 0 BFF: 0 Progressive: 248594 Undetermined: 1
            """;

        (long? prog, long? tff, long? bff, long? undet) = ContentDetector.ParseIdetAggregate(stderr);

        Assert.That(prog,  Is.EqualTo(248594));
        Assert.That(tff,   Is.EqualTo(0));
        Assert.That(bff,   Is.EqualTo(0));
        Assert.That(undet, Is.EqualTo(1));
    }

    [Test]
    public void ParseIdetAggregate_AllInterlacedTff_ParsesCounts()
    {
        const string stderr =
            "[Parsed_idet_0 @ 0x...] Multi frame detection: TFF: 1500 BFF: 50 Progressive: 0 Undetermined: 10";
        (long? prog, long? tff, long? bff, long? undet) = ContentDetector.ParseIdetAggregate(stderr);
        Assert.That(tff,   Is.EqualTo(1500));
        Assert.That(bff,   Is.EqualTo(50));
        Assert.That(prog,  Is.EqualTo(0));
        Assert.That(undet, Is.EqualTo(10));
    }

    [Test]
    public void ParseIdetAggregate_NoAggregateLine_ReturnsAllNulls()
    {
        (long? prog, long? tff, long? bff, long? undet) = ContentDetector.ParseIdetAggregate(
            "some unrelated ffmpeg output here\nwith no idet aggregate present");
        Assert.That(prog, Is.Null);
        Assert.That(tff, Is.Null);
        Assert.That(bff, Is.Null);
        Assert.That(undet, Is.Null);
    }

    [Test]
    public void ParseIdetAggregate_EmptyOrNullStderr_ReturnsAllNulls()
    {
        (long? p1, long? t1, long? b1, long? u1) = ContentDetector.ParseIdetAggregate("");
        Assert.That(p1, Is.Null); Assert.That(t1, Is.Null); Assert.That(b1, Is.Null); Assert.That(u1, Is.Null);

        (long? p2, long? t2, long? b2, long? u2) = ContentDetector.ParseIdetAggregate(null);
        Assert.That(p2, Is.Null); Assert.That(t2, Is.Null); Assert.That(b2, Is.Null); Assert.That(u2, Is.Null);
    }

    // ===================================================================
    // CheckAggregateAgreement: cross-checks our streamed counts against
    // idet's own end-of-stream aggregate.
    // ===================================================================

    [Test]
    public void CheckAgreement_ExactMatch_ReturnsTrue()
    {
        bool ok = ContentDetector.CheckAggregateAgreement(
            aggProg: 1000, aggTff: 50, aggBff: 0, aggUndet: 5,
            progCount: 1000, tffCount: 50, bffCount: 0, undetCount: 5);
        Assert.That(ok, Is.True);
    }

    [Test]
    public void CheckAgreement_OneFrameOff_ReturnsTrue()
    {
        // idet's lookahead can produce a single-frame difference at end-of-stream.
        bool ok = ContentDetector.CheckAggregateAgreement(
            aggProg: 1000, aggTff: 50, aggBff: 0, aggUndet: 5,
            progCount: 1001, tffCount: 50, bffCount: 0, undetCount: 4);
        Assert.That(ok, Is.True);
    }

    [Test]
    public void CheckAgreement_LargeDiscrepancy_ReturnsFalse()
    {
        bool ok = ContentDetector.CheckAggregateAgreement(
            aggProg: 1000, aggTff: 50, aggBff: 0, aggUndet: 5,
            progCount: 500, tffCount: 50, bffCount: 0, undetCount: 5);
        Assert.That(ok, Is.False);
    }

    [Test]
    public void CheckAgreement_NoAggregate_SoftPasses()
    {
        // When idet's aggregate line was missing, we shouldn't fail-loud.
        bool ok = ContentDetector.CheckAggregateAgreement(
            aggProg: null, aggTff: null, aggBff: null, aggUndet: null,
            progCount: 1000, tffCount: 50, bffCount: 0, undetCount: 5);
        Assert.That(ok, Is.True);
    }

    // ---- helpers ----

    private static List<FrameFlag> TimesPattern(FrameFlag[] pattern, int times)
    {
        List<FrameFlag> list = new(pattern.Length * times);
        for (int i = 0; i < times; i++) list.AddRange(pattern);
        return list;
    }
}
