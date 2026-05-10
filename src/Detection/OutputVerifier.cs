using System.Globalization;

namespace CompressMkv;

// =========================================================================
// Trust-but-verify pass on the final encoded output.
//
// After FinalEncoder writes the AV1 file, decode it end-to-end with idet and
// confirm that the chosen restoration action produced the structurally-correct
// output:
//
//   - For IVTC runs: output should be ≥ 99% progressive with no residual
//     3:2 cadence, and the rate should match the IVTC target (24000/1001).
//   - For Deinterlace runs: output should be ≥ 99% progressive (bwdif's job
//     was to remove combing) at the source's native rate.
//   - For pass-through runs: no expectations — we explicitly didn't restore,
//     so verification is skipped.
//
// What this catches:
//   ✓ Residual combing (IVTC didn't fully reverse 3:2, or bwdif missed frames)
//   ✓ Persistent cadence pattern in the output (IVTC partially failed)
//   ✓ Wrong output frame rate (rate-pin disagreement with what was written)
//
// What this does NOT catch (acknowledged limitation):
//   ✗ Silent IVTC frame drops on misclassified 60i content.  When `decimate`
//     drops 1-in-5 frames from genuinely-60i content, the remaining frames
//     are progressive and idet sees clean output — the dropped frames are
//     simply gone.  The defense for that case is upstream: the NTSC-thirty
//     fps guard and CFR check in RestoreStrategyMapper prevent IVTC from
//     firing on non-NTSC-thirty sources.
//
// The verification is full-file (not sample-based) because the user wants
// definitive coverage on the actual deliverable.  Cost is bounded: AV1
// decode under NVDEC runs at multiple times realtime even for 4K HDR.
// =========================================================================
public static class OutputVerifier
{
    /// <summary>
    /// Minimum progressive fraction of the output for verification to pass on
    /// any restored run.  Below this, idet is detecting residual interlacing.
    /// </summary>
    const double MinOutputProgFrac = 0.99;

    /// <summary>
    /// Maximum residual cadence rate allowed in the output of an IVTC run.
    /// A successful IVTC eliminates the 3:2 pattern; any persistent cadence
    /// indicates fieldmatch+decimate didn't fully reverse it.
    /// </summary>
    const double MaxResidualCadenceRate = 0.05;

    public static async Task<OutputVerificationResult> VerifyAsync(
        Config cfg, string outputPath, RestoreDecision restore, CancellationToken ct)
    {
        var result = new OutputVerificationResult
        {
            OutputPath = outputPath,
            RestoreModeApplied = restore.Mode,
        };

        // Pass-through: nothing to verify — we explicitly didn't restore.
        if (restore.Mode == RestoreMode.None)
        {
            result.Passed = true;
            result.Skipped = true;
            result.Notes = "Verification skipped: pass-through (no restoration applied).";
            return result;
        }

        Console.WriteLine($"  Verification: decoding final output with idet (full file)...");

        // Probe output for stream metadata (so DetectAsync can read field_order, fps, etc.).
        FfprobeRoot probe;
        try
        {
            probe = await Ffprobe.RunAsync(cfg, outputPath, ct);
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Notes = $"ffprobe failed on output: {ex.Message}";
            return result;
        }

        var vstream = probe.Streams?.FirstOrDefault(s => s.CodecType == "video");
        if (vstream is null)
        {
            result.Passed = false;
            result.Notes = "No video stream found in output.";
            return result;
        }

        // Reuse the same idet pipeline used during initial detection.  This
        // gives us prog/int counts, cadence rate, parity, and the aggregate
        // cross-check — all of which are exactly what we want to inspect.
        ContentDetectionResult detection;
        try
        {
            detection = await ContentDetector.DetectAsync(cfg, outputPath, vstream, ct);
        }
        catch (Exception ex)
        {
            result.Passed = false;
            result.Notes = $"idet pass on output failed: {ex.Message}";
            return result;
        }

        result.OutputDetection = detection;
        result.OutputFps = vstream.ResolveFps();

        // Progressive cleanliness check.
        bool progressiveOk = detection.GlobalProgressiveFraction >= MinOutputProgFrac;
        if (!progressiveOk)
        {
            result.Warnings.Add(
                $"Output progressive fraction {detection.GlobalProgressiveFraction:P2} " +
                $"below required {MinOutputProgFrac:P0} — residual interlacing detected.");
        }

        // Residual-cadence check (only meaningful for IVTC runs).
        if (restore.Mode == RestoreMode.Ivtc)
        {
            bool cadenceOk = detection.TelecineCadenceMatchRate <= MaxResidualCadenceRate;
            if (!cadenceOk)
            {
                result.Warnings.Add(
                    $"Output cadence rate {detection.TelecineCadenceMatchRate:P2} " +
                    $"above {MaxResidualCadenceRate:P0} — IVTC may not have fully reversed " +
                    "the 3:2 pattern.");
            }
        }

        // Output rate check.
        if (restore.OutputFps.HasValue && result.OutputFps.HasValue)
        {
            var expected = restore.OutputFps.Value;
            var actual = result.OutputFps.Value;
            if (!actual.IsApproximately(expected))
            {
                result.Warnings.Add(
                    $"Output rate {actual} does not match the IVTC chain's expected " +
                    $"output rate {expected}.");
            }
        }

        result.Passed = result.Warnings.Count == 0;
        result.Notes = result.Passed
            ? $"Output verification passed: {detection.GlobalProgressiveFraction:P2} progressive, " +
              $"cadence {detection.TelecineCadenceMatchRate:P2}, rate {result.OutputFps?.ToString() ?? "?"}."
            : $"Output verification FAILED: {result.Warnings.Count} issue(s) — review warnings.";

        Console.WriteLine($"  {result.Notes}");
        foreach (var w in result.Warnings)
            Console.WriteLine($"    ! {w}");

        return result;
    }
}

/// <summary>
/// Result of the trust-but-verify pass on the final encoded output.
/// </summary>
public sealed class OutputVerificationResult
{
    public string OutputPath { get; set; } = "";
    public RestoreMode RestoreModeApplied { get; set; }
    public bool Skipped { get; set; }
    public bool Passed { get; set; }
    public string Notes { get; set; } = "";
    public List<string> Warnings { get; set; } = new();

    /// <summary>idet detection result on the output file.  Null when verification
    /// was skipped (pass-through mode) or failed before detection ran.</summary>
    public ContentDetectionResult? OutputDetection { get; set; }

    /// <summary>Frame rate of the encoded output, as parsed from ffprobe.</summary>
    public Fps? OutputFps { get; set; }
}
