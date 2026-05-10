using System.Globalization;

namespace CompressMkv.Tests;

/// <summary>
/// End-to-end §7.2.3 verification: apply the chosen restoration filter chain
/// to a synthesized input, then run idet on the output and confirm the chain
/// produced a clean progressive result.  Also exercises the
/// <see cref="OutputVerifier"/> trust-but-verify pipeline.
///
/// These are the most important tests for the trust-but-verify guarantee:
/// they round-trip a real video through the actual ffmpeg filter graph we
/// hand to production encodes.
/// </summary>
[TestFixture]
[Category("Integration")]
public class RestoreRoundtripIntegrationTests
{
    [Test]
    public async Task IvtcChain_OnTelecinedSource_ProducesCleanProgressiveAt24p()
    {
        var output = Path.Combine(TestVideoFixture.TempDir, "ivtc_output.mkv");

        await ApplyFilterChain(
            TestVideoFixture.TelecinedClip,
            output,
            filterGraph: RestoreFilters.IvtcChain(FieldParity.Tff),
            outputFps: RestoreFilters.IvtcOutputFps);

        // After IVTC, idet on the output should see almost-pure progressive frames
        // with no remaining 3:2 cadence pattern.
        var detection = await DetectOnFile(output);

        Assert.That(detection.GlobalProgressiveFraction, Is.GreaterThanOrEqualTo(0.99),
            $"IVTC output should be ≥99% progressive; got {detection.GlobalProgressiveFraction:P2}.");
        Assert.That(detection.TelecineCadenceMatchRate, Is.LessThanOrEqualTo(0.05),
            $"IVTC output should have no residual cadence; got {detection.TelecineCadenceMatchRate:P2}.");
        Assert.That(detection.SourceFps!.Value.IsApproximately(Fps.Ntsc24), Is.True,
            $"IVTC output rate should be ≈ 24000/1001; got {detection.SourceFps}.");
    }

    [Test]
    public async Task DeinterlaceChain_OnInterlacedSource_ProducesCleanProgressiveAtNativeRate()
    {
        var output = Path.Combine(TestVideoFixture.TempDir, "deint_output.mkv");

        await ApplyFilterChain(
            TestVideoFixture.InterlacedClip,
            output,
            filterGraph: RestoreFilters.DeinterlaceChain(FieldParity.Tff),
            outputFps: null);

        var detection = await DetectOnFile(output);

        Assert.That(detection.GlobalProgressiveFraction, Is.GreaterThanOrEqualTo(0.99),
            $"bwdif output should be ≥99% progressive; got {detection.GlobalProgressiveFraction:P2}.");
        // Native rate for the interlaced clip is 30000/1001 (bwdif preserves it).
        Assert.That(detection.SourceFps!.Value.IsApproximately(Fps.Ntsc30), Is.True,
            $"Deinterlace should preserve source rate; got {detection.SourceFps}.");
    }

    [Test]
    public async Task OutputVerifier_OnSuccessfulIvtc_Passes()
    {
        var output = Path.Combine(TestVideoFixture.TempDir, "ivtc_for_verify.mkv");
        await ApplyFilterChain(
            TestVideoFixture.TelecinedClip,
            output,
            filterGraph: RestoreFilters.IvtcChain(FieldParity.Tff),
            outputFps: RestoreFilters.IvtcOutputFps);

        var restore = new RestoreDecision
        {
            Mode = RestoreMode.Ivtc,
            FilterGraph = RestoreFilters.IvtcChain(FieldParity.Tff),
            OutputFps = RestoreFilters.IvtcOutputFps,
        };

        var result = await OutputVerifier.VerifyAsync(
            TestVideoFixture.CreateTestConfig(), output, restore, CancellationToken.None);

        Assert.That(result.Passed, Is.True,
            $"IVTC verification should pass on a successful round-trip. " +
            $"Notes: {result.Notes}; warnings: {string.Join("; ", result.Warnings)}");
        Assert.That(result.Skipped, Is.False);
    }

    [Test]
    public async Task OutputVerifier_OnSuccessfulDeinterlace_Passes()
    {
        var output = Path.Combine(TestVideoFixture.TempDir, "deint_for_verify.mkv");
        await ApplyFilterChain(
            TestVideoFixture.InterlacedClip,
            output,
            filterGraph: RestoreFilters.DeinterlaceChain(FieldParity.Tff),
            outputFps: null);

        var restore = new RestoreDecision
        {
            Mode = RestoreMode.Deinterlace,
            FilterGraph = RestoreFilters.DeinterlaceChain(FieldParity.Tff),
            OutputFps = null,
        };

        var result = await OutputVerifier.VerifyAsync(
            TestVideoFixture.CreateTestConfig(), output, restore, CancellationToken.None);

        Assert.That(result.Passed, Is.True,
            $"Deinterlace verification should pass. " +
            $"Notes: {result.Notes}; warnings: {string.Join("; ", result.Warnings)}");
    }

    [Test]
    public async Task OutputVerifier_OnInterlacedFileMislabeledAsIvtcOutput_Fails()
    {
        // Negative case: if we pretend the unrestored interlaced clip is an IVTC
        // output, verification must catch the residual interlacing.
        var restore = new RestoreDecision
        {
            Mode = RestoreMode.Ivtc,
            FilterGraph = RestoreFilters.IvtcChain(FieldParity.Tff),
            OutputFps = RestoreFilters.IvtcOutputFps,
        };

        var result = await OutputVerifier.VerifyAsync(
            TestVideoFixture.CreateTestConfig(),
            TestVideoFixture.InterlacedClip,
            restore,
            CancellationToken.None);

        Assert.That(result.Passed, Is.False,
            "Verification must reject an interlaced file masquerading as an IVTC output.");
        Assert.That(result.Warnings, Is.Not.Empty);
    }

    [Test]
    public async Task OutputVerifier_OnPassThroughMode_SkipsAndPasses()
    {
        var restore = new RestoreDecision
        {
            Mode = RestoreMode.None,
            FilterGraph = "",
            OutputFps = null,
        };

        var result = await OutputVerifier.VerifyAsync(
            TestVideoFixture.CreateTestConfig(),
            TestVideoFixture.ProgressiveClip,
            restore,
            CancellationToken.None);

        Assert.That(result.Skipped, Is.True);
        Assert.That(result.Passed, Is.True);
    }

    // ---- helpers ----

    /// <summary>
    /// Applies a filter chain to an input and writes the result as FFV1 (lossless,
    /// so the output is purely a function of the input + chain — no encoder noise
    /// confusing later idet checks).
    /// </summary>
    private static async Task ApplyFilterChain(
        string input, string output, string filterGraph, Fps? outputFps)
    {
        if (File.Exists(output)) File.Delete(output);

        var args = new List<string>
        {
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", input,
            "-map", "0:v:0", "-an", "-sn", "-dn",
        };

        if (!string.IsNullOrWhiteSpace(filterGraph))
            args.AddRange(["-vf", filterGraph]);

        if (outputFps.HasValue)
            args.AddRange(["-r", outputFps.Value.ToString()]);

        args.AddRange([
            "-fps_mode", "cfr",
            "-c:v", "ffv1", "-level", "3", "-slicecrc", "1",
            output
        ]);

        var (code, _, err) = await Proc.RunAsync("ffmpeg", args.ToArray(), CancellationToken.None);
        if (code != 0)
            throw new InvalidOperationException(
                $"Filter-chain apply failed.\nFilter: {filterGraph}\nStderr: {err}");
    }

    private static async Task<ContentDetectionResult> DetectOnFile(string path)
    {
        var cfg = TestVideoFixture.CreateTestConfig();
        var probe = await Ffprobe.RunAsync(cfg, path, CancellationToken.None);
        var vstream = probe.Streams!.First(s => s.CodecType == "video");
        return await ContentDetector.DetectAsync(cfg, path, vstream, CancellationToken.None);
    }
}
