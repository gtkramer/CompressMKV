namespace MkvHelper.Tests.Integration;

/// <summary>
/// Integration tests that drive ContentDetector.DetectAsync against real
/// ffmpeg/idet on synthesized test clips.  These tests verify that:
///
///   - ffmpeg's idet output is parsed correctly off stdout.
///   - The §7.2.2 classifier produces a sensible category for each clip.
///   - Progressive content classifies as Progressive (strict).
///   - Non-progressive content (telecine, interlaced) classifies as one
///     of the four non-Progressive categories.
///   - The aggregate cross-check from idet's stderr matches our streamed
///     per-frame counts.
///   - Source-fps detection works against ffprobe.
///
/// The classifier's specific bucketing of synthesized telecine is not asserted:
/// idet's multi-frame detector over-flags ffmpeg-synthesized telecine output as
/// pure interlaced (compared to real-world NTSC DVD content where it would
/// detect cadence cleanly).  The round-trip tests in
/// <see cref="RestoreRoundtripIntegrationTests"/> are the authoritative check
/// that the §7.2.3 chains actually work; here we only verify the classifier
/// distinguishes "needs filtering" from "doesn't need filtering".
/// </summary>
[TestFixture]
[Category("Integration")]
public class ContentDetectorIntegrationTests
{
    [Test]
    public async Task DetectAsync_OnProgressiveClip_ClassifiesAsProgressive()
    {
        ContentDetectionResult detection = await Detect(TestVideoFixture.ProgressiveClip);

        Assert.That(detection.ContentType, Is.EqualTo(ContentType.Progressive),
            $"Expected Progressive, got {detection.ContentType}. {detection.Reason}");
        Assert.That(detection.InterlacedFrameCount, Is.EqualTo(0),
            "Progressive source should produce zero interlaced flags from idet.");
        Assert.That(detection.GlobalProgressiveFraction, Is.EqualTo(1.0));
    }

    [Test]
    public async Task DetectAsync_OnTelecinedClip_ClassifiesAsNonProgressive()
    {
        ContentDetectionResult detection = await Detect(TestVideoFixture.TelecinedClip);

        Assert.That(detection.ContentType, Is.Not.EqualTo(ContentType.Progressive),
            $"Synthesized telecine source should not classify as Progressive. " +
            $"progFrac={detection.GlobalProgressiveFraction:P2}, " +
            $"cadence={detection.TelecineCadenceMatchRate:P2}, " +
            $"iInCadence={detection.InterlacedFramesInCadenceRatio:P2}");
        Assert.That(detection.InterlacedFrameCount, Is.GreaterThan(0),
            "Synthesized telecine should produce some interlaced flags.");
    }

    [Test]
    public async Task DetectAsync_OnInterlacedClip_ClassifiesAsInterlacedFamily()
    {
        ContentDetectionResult detection = await Detect(TestVideoFixture.InterlacedClip);

        // tinterlace produces nearly-pure interlaced; accept Interlaced or
        // MixedProgressiveInterlaced (both lead to bwdif at native rate).
        Assert.That(detection.ContentType,
            Is.EqualTo(ContentType.Interlaced).Or.EqualTo(ContentType.MixedProgressiveInterlaced),
            $"Expected Interlaced family, got {detection.ContentType}. " +
            $"progFrac={detection.GlobalProgressiveFraction:P2}, " +
            $"iInCadence={detection.InterlacedFramesInCadenceRatio:P2}");

        Assert.That(detection.InterlacedFramesInCadenceRatio, Is.LessThan(0.30),
            "Synthesized native-interlace should have very few I frames in 3:2 cycles.");
    }

    [Test]
    public async Task DetectAsync_AlwaysProducesSomeFrameCount()
    {
        // Smoke check: idet output parsing produces a non-zero count for any clip.
        ContentDetectionResult detection = await Detect(TestVideoFixture.ProgressiveClip);
        Assert.That(detection.TotalFramesAnalyzed, Is.GreaterThan(0),
            "idet stdout parser produced zero frames — regex or stream wiring is broken.");
    }

    [Test]
    public async Task DetectAsync_PopulatesIdetAggregateAndAgrees()
    {
        // Cross-check (B): our streamed per-frame counts must match idet's own
        // end-of-stream aggregate within a one-frame tolerance.
        ContentDetectionResult detection = await Detect(TestVideoFixture.ProgressiveClip);

        Assert.That(detection.IdetAggregateProgressive, Is.Not.Null,
            "idet aggregate stderr line was not parsed — regex or stderr capture is broken.");
        Assert.That(detection.IdetAggregateAgrees, Is.True,
            "Per-frame stream count disagrees with idet's aggregate — parser bug. " +
            $"Per-frame={detection.ProgressiveFrameCount}, " +
            $"aggregate={detection.IdetAggregateProgressive}");
    }

    [Test]
    public async Task DetectAsync_PicksUpProgressiveSourceFps()
    {
        ContentDetectionResult detection = await Detect(TestVideoFixture.ProgressiveClip);

        Assert.That(detection.SourceFps, Is.Not.Null,
            "Source fps not parsed from ffprobe.");
        Assert.That(detection.SourceFps!.Value.IsApproximately(Fps.Ntsc24), Is.True,
            $"Expected source fps ≈ 24000/1001, got {detection.SourceFps}");
    }

    [Test]
    public async Task DetectAsync_PicksUpTelecinedSourceFps()
    {
        ContentDetectionResult detection = await Detect(TestVideoFixture.TelecinedClip);

        Assert.That(detection.SourceFps, Is.Not.Null);
        Assert.That(detection.SourceFps!.Value.IsApproximately(Fps.Ntsc30), Is.True,
            $"Expected telecined output to be 30000/1001, got {detection.SourceFps}");
    }

    [Test]
    public async Task DetectAsync_PicksUpInterlacedSourceFps()
    {
        ContentDetectionResult detection = await Detect(TestVideoFixture.InterlacedClip);

        Assert.That(detection.SourceFps, Is.Not.Null);
        Assert.That(detection.SourceFps!.Value.IsApproximately(Fps.Ntsc30), Is.True,
            $"Expected interlaced output to be 30000/1001, got {detection.SourceFps}");
    }

    private static async Task<ContentDetectionResult> Detect(string clipPath)
    {
        Config cfg = TestVideoFixture.CreateTestConfig();
        FfprobeRoot probe = await Ffprobe.RunAsync(cfg, clipPath, CancellationToken.None);
        FfprobeStream vstream = probe.Streams?.FirstOrDefault(s => s.CodecType == "video")
            ?? throw new InvalidOperationException($"No video stream in test clip {clipPath}");
        return await ContentDetector.DetectAsync(cfg, clipPath, vstream, useHwaccel: false, CancellationToken.None);
    }
}
