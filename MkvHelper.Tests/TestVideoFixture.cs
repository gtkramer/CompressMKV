using System.Globalization;

namespace MkvHelper.Tests;

/// <summary>
/// One-time fixture that builds the test clips used by every integration test
/// in this assembly.  All clips are generated on the fly from ffmpeg's lavfi
/// `color` source — there is NO dependency on any external video file, so a
/// fresh `git clone` + `dotnet test` works out of the box.
///
/// The synthesized base clip is a static deep-blue background with a single
/// large frame-number counter overlaid in the middle.  This is the smallest
/// signal that:
///   - Reads as 100% progressive to idet (so the §7.2.2.1 Progressive
///     classification test gets a clean baseline).
///   - Has enough motion that idet recognizes the frames at all (a fully
///     static source flips every frame to "Undetermined").
///   - Survives synthesized interlace + bwdif round-trip cleanly enough to
///     clear the strict ≥99% progressive threshold of <see cref="OutputVerifier"/>.
///   - Survives synthesized telecine + IVTC round-trip with zero residual
///     interlacing (decimate + fieldmatch + bwdif=interlaced reverse the
///     synthesis exactly).
///
/// Real-video sources from ~/Videos were tried and abandoned: their natural
/// motion creates residual artifacts after a synthesized interlace+bwdif
/// round-trip that idet flags as interlaced, which the production 99%
/// threshold rejects.
///
/// All clips use FFV1 lossless encoding so idet sees the actual filter output,
/// not encoder-introduced artifacts.
/// </summary>
[SetUpFixture]
public static class TestVideoFixture
{
    /// <summary>Synthetic 24p progressive clip.  idet classifies this as 100%
    /// progressive, so it's the canonical input for §7.2.2.1 tests.</summary>
    public static string ProgressiveClip { get; private set; } = "";

    /// <summary>Synthesized 30p clip with 3:2 telecine pattern applied.
    /// Round-tripping it through the IVTC chain recovers the progressive
    /// source exactly.</summary>
    public static string TelecinedClip { get; private set; } = "";

    /// <summary>Synthesized 30p interlaced clip.  Round-tripping it through
    /// bwdif produces ≥99% progressive output.</summary>
    public static string InterlacedClip { get; private set; } = "";

    public static string TempDir { get; private set; } = "";

    private const int ClipDurationSec = 15;
    private const string Ffmpeg = "ffmpeg";
    private const string Ffprobe = "ffprobe";

    [OneTimeSetUp]
    public static async Task BuildClips()
    {
        // Route ContainerTools.Run* through the system ffmpeg/ffprobe
        // for the test run.  Production code goes through podman; the
        // integration tests don't need that overhead.
        ContainerTools.ConfigureForTesting();

        TempDir = Path.Combine(Path.GetTempPath(), "mkvhelper-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(TempDir);

        TestContext.Out.WriteLine($"Test temp dir: {TempDir}");

        // 1. Synthetic 24000/1001 progressive source — the truth all others
        //    derive from.  Static background + single drawtext counter gives
        //    idet exactly the right amount of motion (recognizable as
        //    progressive, but small enough that interlace synthesis is
        //    perfectly reversible).
        ProgressiveClip = Path.Combine(TempDir, "progressive.mkv");
        await Run(Ffmpeg,
            "-y", "-hide_banner", "-loglevel", "error",
            "-f", "lavfi",
            "-i", $"color=c=0x000080:size=480x270:rate=24000/1001:duration={ClipDurationSec.ToString(CultureInfo.InvariantCulture)}",
            "-vf", "drawtext=text='%{n}':x=200:y=120:fontsize=80:fontcolor=white,format=yuv420p",
            "-c:v", "ffv1", "-level", "3", "-slicecrc", "1",
            ProgressiveClip);

        // 2. Telecined 30000/1001 clip — 3:2 pulldown applied to the source.
        TelecinedClip = Path.Combine(TempDir, "telecined.mkv");
        await Run(Ffmpeg,
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", ProgressiveClip,
            "-an", "-sn", "-dn",
            "-vf", "telecine=pattern=23",
            "-fps_mode", "cfr",
            "-c:v", "ffv1", "-level", "3", "-slicecrc", "1",
            TelecinedClip);

        // 3. Interlaced 30000/1001 clip — frame-double then interleave fields.
        InterlacedClip = Path.Combine(TempDir, "interlaced.mkv");
        await Run(Ffmpeg,
            "-y", "-hide_banner", "-loglevel", "error",
            "-i", ProgressiveClip,
            "-an", "-sn", "-dn",
            "-vf", "fps=60000/1001,tinterlace=mode=interleave_top:flags=low_pass_filter",
            "-fps_mode", "cfr",
            "-c:v", "ffv1", "-level", "3", "-slicecrc", "1",
            InterlacedClip);

        TestContext.Out.WriteLine($"Built progressive: {ProgressiveClip} ({new FileInfo(ProgressiveClip).Length:N0} bytes)");
        TestContext.Out.WriteLine($"Built telecined:   {TelecinedClip} ({new FileInfo(TelecinedClip).Length:N0} bytes)");
        TestContext.Out.WriteLine($"Built interlaced:  {InterlacedClip} ({new FileInfo(InterlacedClip).Length:N0} bytes)");
    }

    [OneTimeTearDown]
    public static void Cleanup()
    {
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    /// <summary>Builds a default Config for tests.  Production thresholds —
    /// tests must produce clips that satisfy them.  ffmpeg/ffprobe routing
    /// comes from <see cref="ContainerTools"/>, configured in test-bypass
    /// mode by <see cref="BuildClips"/>.</summary>
    public static Config CreateTestConfig() => new();

    private static async Task Run(string exe, params string[] args)
    {
        var (code, _, err) = await Proc.RunAsync(exe, args, CancellationToken.None);
        if (code != 0)
            throw new InvalidOperationException(
                $"{exe} exited with code {code}.\nArgs: {string.Join(" ", args)}\nStderr: {err}");
    }
}
