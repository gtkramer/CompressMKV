using System.Globalization;
using System.Text.RegularExpressions;

namespace CompressMkv;

// =========================
// Content type detection per MPlayer guide §7.2.2 — five categories
// =========================
//
// Single-pass approach: one ffmpeg call decodes the entire file with the idet
// filter and metadata=mode=print.  Per-frame classification (progressive, tff,
// bff, or undetermined) is streamed from stdout and stored as a compact
// FrameFlag list — about 200 KB for a typical 2-hour movie, trivially small
// even with gigabytes of available RAM.
//
// All five §7.2.2 content types are identified using two whole-file metrics
// computed directly from the per-frame sequence:
//
//   1. Global progressive fraction
//      The ratio of progressive frames to all classified (P+I) frames.
//      §7.2.2.1 Progressive:  ≥ 95% progressive
//      §7.2.2.3 Interlaced:   ≤ 5%  progressive
//
//   2. Telecine cadence match rate
//      A 5-frame sliding window scans the entire frame sequence.  Each window
//      that contains exactly 3 progressive and 2 interlaced frames matches the
//      3:2 pulldown signature (PPPII) described in §7.2.2.2.  The fraction of
//      matching windows is the cadence match rate.
//
//      §7.2.2.2 Telecined:               cadence rate ≥ 80%  (uniform cadence)
//      §7.2.2.4 Mixed prog+telecine:     cadence rate 10–80% (some telecined sections)
//      §7.2.2.5 Mixed prog+interlaced:   cadence rate < 10%  (no periodic pattern)
//
// The cadence match rate is the key discriminator.  Telecined content has a
// deterministic repeating 5-frame cycle, so the rate is near 100%.  Contiguous
// mixed progressive+interlaced content has a rate near 0% because progressive
// regions (all P) and interlaced regions (all I) never produce 3P+2I windows.
//
// No chunks.  No sampling.  No IVTC verification.  No configurable thresholds.
// All detection constants are derived from 3:2 pulldown signal physics and are
// internal to this class.
//
// Parity (TFF vs BFF) is determined from full-file frame counts — with hundreds
// of thousands of frames the result is definitive.
//
// Hardware-accelerated decoding (NVDEC) is used when configured, offloading the
// decode to one of the RTX 5080's two hardware decode streams.
// =========================
public static partial class ContentDetector
{
    // ---- Detection constants (derived from signal physics, not tunable) ----

    /// <summary>§7.2.2.1: "you should never see any interlacing."</summary>
    const double PureProgressiveThreshold = 0.95;

    /// <summary>§7.2.2.3: "every single frame is interlaced."</summary>
    const double PureInterlacedThreshold = 0.05;

    /// <summary>§7.2.2.2: 3:2 cadence present uniformly throughout the file.</summary>
    const double CadenceHighThreshold = 0.80;

    /// <summary>§7.2.2.4: cadence present in some regions → mixed prog+telecine.</summary>
    const double CadencePresentThreshold = 0.10;

    /// <summary>TFF vs BFF dominance for parity detection.</summary>
    const double ParityMinDominance = 0.60;

    // ---- Regex for per-frame idet metadata ----

    [GeneratedRegex(
        @"lavfi\.idet\.multiple\.current_frame=(\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex IdetFrameRegex();

    /// <summary>
    /// Single-pass content detection.  Decodes the entire file once with idet,
    /// stores every frame's classification, and determines the content type from
    /// global progressive fraction and telecine cadence match rate.
    /// Source-agnostic: works on DVDs, Blu-rays, or any other container/codec.
    /// </summary>
    public static async Task<ContentDetectionResult> DetectAsync(
        Config cfg, string input, FfprobeStream vstream, CancellationToken ct)
    {
        // ---- Build ffmpeg args: full decode → idet → metadata print to stdout ----
        var argList = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats"
        };

        if (cfg.UseHwaccelForDetection)
        {
            argList.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "nv12"]);
        }

        argList.AddRange([
            "-i", input,
            "-an", "-sn", "-dn",
            "-vf", "idet,metadata=mode=print:file=-:direct=1",
            "-f", "null", "-"
        ]);

        var args = argList.ToArray();

        // ---- Per-frame accumulation (trivial memory: ~1 byte per frame) ----
        var frames = new List<FrameFlag>();
        long totalTff = 0, totalBff = 0;

        var regex = IdetFrameRegex();

        Console.WriteLine("  Detection: decoding full file with idet (single pass)...");

        void ProcessLine(string line)
        {
            var match = regex.Match(line);
            if (!match.Success) return;

            string value = match.Groups[1].Value.ToLowerInvariant();
            switch (value)
            {
                case "progressive":
                    frames.Add(FrameFlag.Progressive);
                    break;
                case "tff":
                    frames.Add(FrameFlag.Interlaced);
                    totalTff++;
                    break;
                case "bff":
                    frames.Add(FrameFlag.Interlaced);
                    totalBff++;
                    break;
                default:
                    frames.Add(FrameFlag.Undetermined);
                    break;
            }
        }

        var (exitCode, _) = await Proc.RunStreamingAsync(cfg.Ffmpeg, args, ProcessLine, ct);

        Console.WriteLine($"  Detection: decoded {frames.Count:N0} frames.");
        if (exitCode != 0)
            Console.WriteLine($"  Warning: ffmpeg exited with code {exitCode}");

        // ---- Compute frame statistics ----
        int progCount = 0, intCount = 0, undetCount = 0;
        foreach (var f in frames)
        {
            switch (f)
            {
                case FrameFlag.Progressive: progCount++; break;
                case FrameFlag.Interlaced:  intCount++;  break;
                default:                    undetCount++; break;
            }
        }

        int classified = progCount + intCount;
        double globalProgFrac = classified > 0 ? progCount / (double)classified : 1.0;

        // ---- Telecine cadence detection (sliding 5-frame window) ----
        double cadenceRate = ComputeCadenceMatchRate(frames);

        // ---- Parity detection from full-file TFF/BFF counts ----
        var parity = DetectParity(totalTff, totalBff);

        // Cross-check with ffprobe field_order.
        var ffprobeParity = FieldOrderMapper.MapToParity(
            vstream.FieldOrder?.Trim().ToLowerInvariant() ?? "");
        bool parityMismatch =
            ffprobeParity != FieldParity.Auto &&
            parity != FieldParity.Auto &&
            ffprobeParity != parity;

        // ---- Classify into one of the five §7.2.2 categories ----
        var (contentType, confidence, reason) = Classify(
            globalProgFrac, cadenceRate, parityMismatch);

        Console.WriteLine($"  Detection complete: {contentType} (confidence={confidence:P0})");
        Console.WriteLine($"    prog={globalProgFrac:P1}, cadence={cadenceRate:P1}, " +
            $"parity={parity}, frames={frames.Count:N0} (P={progCount:N0} I={intCount:N0} U={undetCount:N0})");

        return new ContentDetectionResult
        {
            ContentType = contentType,
            Confidence = confidence,
            Reason = reason,
            TotalFramesAnalyzed = frames.Count,
            ProgressiveFrameCount = progCount,
            InterlacedFrameCount = intCount,
            UndeterminedFrameCount = undetCount,
            GlobalProgressiveFraction = globalProgFrac,
            TelecineCadenceMatchRate = cadenceRate,
            DetectedParity = parity,
            RawIdetTffCount = totalTff,
            RawIdetBffCount = totalBff,
            FfprobeFieldOrder = vstream.FieldOrder,
            FfprobeMappedParity = ffprobeParity,
            ParityMismatch = parityMismatch,
        };
    }

    // ------------------------------------------------------------------
    // Telecine cadence detection
    //
    // Scans the full frame sequence with a sliding 5-frame window.
    // Each window matching exactly 3 progressive + 2 interlaced frames
    // corresponds to one position of the 3:2 pulldown cycle (PPPII).
    //
    // O(n) via incremental count maintenance.
    // ------------------------------------------------------------------
    internal static double ComputeCadenceMatchRate(List<FrameFlag> frames)
    {
        if (frames.Count < 5) return 0;

        // Initialize counts for the first window [0..4].
        int p = 0, intl = 0;
        for (int j = 0; j < 5; j++)
        {
            if (frames[j] == FrameFlag.Progressive) p++;
            else if (frames[j] == FrameFlag.Interlaced) intl++;
        }

        int windows = frames.Count - 4;
        int matches = (p == 3 && intl == 2) ? 1 : 0;

        // Slide the window one frame at a time.
        for (int i = 1; i < windows; i++)
        {
            // Remove the frame leaving the window (position i-1).
            var leaving = frames[i - 1];
            if (leaving == FrameFlag.Progressive) p--;
            else if (leaving == FrameFlag.Interlaced) intl--;

            // Add the frame entering the window (position i+4).
            var entering = frames[i + 4];
            if (entering == FrameFlag.Progressive) p++;
            else if (entering == FrameFlag.Interlaced) intl++;

            if (p == 3 && intl == 2) matches++;
        }

        return matches / (double)windows;
    }

    // ------------------------------------------------------------------
    // Parity detection from full-file idet TFF/BFF counts.
    // With 100K+ frames, the dominance is unambiguous.
    // ------------------------------------------------------------------
    internal static FieldParity DetectParity(long rawTff, long rawBff)
    {
        long denom = rawTff + rawBff;
        if (denom <= 0) return FieldParity.Auto;

        double tffFrac = rawTff / (double)denom;
        if (tffFrac >= ParityMinDominance) return FieldParity.Tff;
        if (tffFrac <= (1.0 - ParityMinDominance)) return FieldParity.Bff;
        return FieldParity.Auto;
    }

    // ------------------------------------------------------------------
    // File-level classification: global progressive fraction + cadence
    // match rate → one of the five §7.2.2 content types.
    //
    // Decision space (two axes, five regions):
    //
    //   progFrac ≥ 0.95                         → Progressive    (§7.2.2.1)
    //   progFrac ≤ 0.05                         → Interlaced     (§7.2.2.3)
    //   cadenceRate ≥ 0.80                      → Telecined      (§7.2.2.2)
    //   cadenceRate ≥ 0.10                      → Mixed P+TC     (§7.2.2.4)
    //   cadenceRate < 0.10                      → Mixed P+I      (§7.2.2.5)
    // ------------------------------------------------------------------
    internal static (ContentType Type, double Confidence, string Reason) Classify(
        double progFrac, double cadenceRate, bool parityMismatch)
    {
        double Penalize(double c) => parityMismatch ? Math.Max(0.20, c - 0.10) : c;

        // 1. Pure progressive (§7.2.2.1)
        //    "When you watch progressive video, you should never see any interlacing."
        if (progFrac >= PureProgressiveThreshold)
        {
            double conf = Math.Clamp(0.85 + (progFrac - PureProgressiveThreshold) * 3.0, 0.85, 0.99);
            return (ContentType.Progressive, Penalize(conf),
                $"Progressive (§7.2.2.1): {progFrac:P2} of classified frames are progressive.");
        }

        // 2. Pure interlaced (§7.2.2.3)
        //    "Every single frame is interlaced."
        if (progFrac <= PureInterlacedThreshold)
        {
            double conf = Math.Clamp(0.85 + (PureInterlacedThreshold - progFrac) * 3.0, 0.85, 0.99);
            return (ContentType.Interlaced, Penalize(conf),
                $"Interlaced (§7.2.2.3): {1.0 - progFrac:P2} of classified frames are interlaced.");
        }

        // 3. Pure telecined (§7.2.2.2)
        //    "The pattern you see is PPPII, PPPII, PPPII, ..."
        //    Uniform 3:2 cadence throughout the file → high cadence match rate.
        if (cadenceRate >= CadenceHighThreshold)
        {
            double conf = Math.Clamp(0.80 + (cadenceRate - CadenceHighThreshold) * 2.0, 0.80, 0.99);
            return (ContentType.Telecined, Penalize(conf),
                $"Telecined (§7.2.2.2): cadence match rate = {cadenceRate:P2} — " +
                $"3:2 pulldown pattern (PPPII) detected uniformly across the file.");
        }

        // 4. Mixed progressive + telecine (§7.2.2.4)
        //    "MPlayer will (often repeatedly) switch back and forth between
        //     30000/1001 fps NTSC and 24000/1001 fps progressive NTSC."
        //    Cadence is present in telecined sections but absent in progressive sections.
        if (cadenceRate >= CadencePresentThreshold)
        {
            double conf = Math.Clamp(0.60 + cadenceRate * 0.3, 0.50, 0.90);
            return (ContentType.MixedProgressiveTelecine, Penalize(conf),
                $"Mixed progressive+telecine (§7.2.2.4): cadence match rate = {cadenceRate:P2}, " +
                $"prog fraction = {progFrac:P2}. " +
                $"Telecine in some sections, progressive in others.");
        }

        // 5. Mixed progressive + interlaced (§7.2.2.5)
        //    "Progressive and interlaced video have been spliced together."
        //    No telecine cadence detected — contiguous progressive and interlaced regions.
        {
            double conf = Math.Clamp(0.60 + Math.Min(progFrac, 1.0 - progFrac) * 0.5, 0.45, 0.85);
            return (ContentType.MixedProgressiveInterlaced, Penalize(conf),
                $"Mixed progressive+interlaced (§7.2.2.5): prog fraction = {progFrac:P2}, " +
                $"cadence rate = {cadenceRate:P2}. No telecine pattern detected.");
        }
    }

    internal static string ParityStr(FieldParity parity) => parity switch
    {
        FieldParity.Tff => "tff",
        FieldParity.Bff => "bff",
        _ => "auto"
    };
}
