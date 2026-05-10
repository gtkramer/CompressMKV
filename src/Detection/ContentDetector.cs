using System.Text.RegularExpressions;

namespace CompressMkv;

// =========================================================================
// Content type detection per MPlayer guide §7.2.2 — five categories.
//
// Single-pass, ffmpeg-only:
//   ffmpeg → idet → metadata=mode=print streams a per-frame interlace flag
//   (progressive | tff | bff | undetermined) for every frame in the file.
//
// Three whole-file metrics decide which of the five §7.2.2 categories apply:
//
//   1. progFrac   — progressive / (progressive + interlaced)
//                   Excludes undetermined frames so the value reflects the
//                   true content mix rather than idet's blind spots.
//
//   2. cadenceRate — fraction of 5-frame sliding windows containing exactly
//                    3 progressive + 2 interlaced frames (the 3:2 pulldown
//                    PPPII signature, in any of its 5 cyclic phases).
//
//   3. iInCadence — fraction of *interlaced* frames whose centered 5-frame
//                   neighborhood is exactly 3P + 2I.  This is the §7.2.2.4
//                   vs §7.2.2.5 discriminator: telecined I frames sit inside
//                   3:2 cycles (high ratio); native interlace produces clusters
//                   of consecutive I frames (low ratio).
//
// Classification is pure §7.2.2 identification — one branch per category, no
// action choices.  The §7.2.3 action mapping (including the §7.2.3.5 "favor
// what dominates" sub-decision and footnote [3]'s safe-pullup default) lives
// in RestoreStrategyMapper, which has access to source rate and can guard
// against applying the IVTC chain to non-NTSC-thirty sources.
//
// Evaluation order (most specific first):
//
//   intCount == 0       → §7.2.2.1 Progressive (verified — "you should never
//                          see any interlacing")
//   progFrac ≤ 5%       → §7.2.2.3 Interlaced ("every single frame is interlaced")
//   cadenceRate ≥ 80%   → §7.2.2.2 Telecined  (uniform PPPII pattern)
//   iInCadence ≥ 60%    → §7.2.2.4 Mixed prog+telecine (the I frames sit inside
//                          3:2 cycles — guide's verification step)
//   else                → §7.2.2.5 Mixed prog+interlaced (I frames are clusters)
//
// Parity (TFF vs BFF):
//   Strict 90% dominance from idet's interlaced-frame TFF/BFF counts.  When
//   ambiguous and the source is NTSC family (30000/1001, 24000/1001, 60000/1001
//   fps), default to TFF — the NTSC standards convention.  ffprobe's field_order
//   is consulted as a secondary tiebreaker.
//
// All thresholds are constants — they're chosen from the guide's qualitative
// descriptions and the 3:2 pulldown structure, not user-tunable.
// =========================================================================
public static partial class ContentDetector
{
    // ---- Classification thresholds ----

    /// <summary>§7.2.2.3: "every single frame is interlaced".</summary>
    const double PureInterlacedProgFracMax = 0.05;

    /// <summary>§7.2.2.2: 3:2 cadence present uniformly throughout the file.</summary>
    const double PureTelecineCadenceMin = 0.80;

    /// <summary>
    /// Fraction of I frames sitting in 3:2 windows that confirms §7.2.2.4 (telecine
    /// in the interlaced regions) vs §7.2.2.5 (genuine 60i in those regions).
    /// This is the guide's explicit verification step for §7.2.2.4: "check the
    /// 30000/1001 fps NTSC sections to make sure they are actually telecine, and
    /// not just interlaced."
    /// </summary>
    const double TelecineIRatioMin = 0.60;

    // ---- Parity thresholds ----

    /// <summary>
    /// TFF/BFF dominance required for confident parity assignment from idet.
    /// Real interlaced sources are 99%+ on one side; 90% leaves room for the
    /// minority of frames idet flips.
    /// </summary>
    const double ParityStrictDominance = 0.90;

    // ---- Regex for per-frame idet metadata ----

    [GeneratedRegex(
        @"lavfi\.idet\.multiple\.current_frame=(\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex IdetFrameRegex();

    /// <summary>
    /// Single-pass content detection.  Decodes the entire file once with ffmpeg's
    /// idet filter, computes whole-file metrics from the per-frame flag stream,
    /// and classifies into one of the five §7.2.2 categories.
    /// Source-agnostic: works on DVDs, Blu-rays, or any other container/codec.
    /// </summary>
    public static async Task<ContentDetectionResult> DetectAsync(
        Config cfg, string input, FfprobeStream vstream, CancellationToken ct)
    {
        // ---- Build ffmpeg args ----
        var argList = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostats"
        };

        if (cfg.UseHwaccelForDetection)
            argList.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", "nv12"]);

        argList.AddRange([
            "-i", input,
            "-an", "-sn", "-dn",
            "-vf", "idet,metadata=mode=print:file=-:direct=1",
            "-f", "null", "-"
        ]);

        // ---- Per-frame accumulation ----
        var frames = new List<FrameFlag>();
        long totalTff = 0, totalBff = 0;

        var regex = IdetFrameRegex();

        Console.WriteLine("  Detection: decoding full file with idet (single pass)...");

        void ProcessLine(string line)
        {
            var match = regex.Match(line);
            if (!match.Success) return;

            switch (match.Groups[1].Value.ToLowerInvariant())
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

        var (exitCode, _) = await Proc.RunStreamingAsync(cfg.Ffmpeg, argList.ToArray(), ProcessLine, ct);

        Console.WriteLine($"  Detection: decoded {frames.Count:N0} frames.");
        if (exitCode != 0)
            Console.WriteLine($"  Warning: ffmpeg exited with code {exitCode}");

        // ---- Per-frame counts ----
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
        double progFrac = classified > 0 ? progCount / (double)classified : 1.0;

        // ---- Cadence metrics ----
        double cadenceRate = ComputeCadenceMatchRate(frames);
        double iInCadence  = ComputeIFramesInCadenceRatio(frames);

        // ---- Source fps and CFR signal (used by parity tiebreaker + IVTC gating) ----
        Fps? sourceFps = vstream.ResolveFps();
        bool isNtsc = sourceFps?.IsNtscFamily() ?? false;
        bool isLikelyCfr = vstream.IsLikelyCfr();

        // ---- Parity ----
        var ffprobeParity = FieldOrderMapper.MapToParity(
            vstream.FieldOrder?.Trim().ToLowerInvariant() ?? "");

        var (parity, parityFromNtsc) = DetectParity(totalTff, totalBff, isNtsc, ffprobeParity);

        bool parityMismatch =
            ffprobeParity != FieldParity.Auto &&
            parity != FieldParity.Auto &&
            ffprobeParity != parity;

        // ---- Classify ----
        var (contentType, confidence, reason) = Classify(
            intCount, progFrac, cadenceRate, iInCadence, parityMismatch);

        Console.WriteLine($"  Detection complete: {contentType} (confidence={confidence:P0})");
        Console.WriteLine($"    progFrac={progFrac:P1}, cadence={cadenceRate:P1}, " +
            $"i_in_cadence={iInCadence:P1}, parity={parity}" +
            (parityFromNtsc ? " (NTSC fallback)" : "") +
            $", source={sourceFps?.ToString() ?? "?"}" +
            (isLikelyCfr ? "" : " (VFR)") +
            $", frames={frames.Count:N0} (P={progCount:N0} I={intCount:N0} U={undetCount:N0})");

        return new ContentDetectionResult
        {
            ContentType = contentType,
            Confidence = confidence,
            Reason = reason,

            TotalFramesAnalyzed = frames.Count,
            ProgressiveFrameCount = progCount,
            InterlacedFrameCount = intCount,
            UndeterminedFrameCount = undetCount,

            GlobalProgressiveFraction = progFrac,
            TelecineCadenceMatchRate = cadenceRate,
            InterlacedFramesInCadenceRatio = iInCadence,

            DetectedParity = parity,
            RawIdetTffCount = totalTff,
            RawIdetBffCount = totalBff,
            ParityFromNtscFallback = parityFromNtsc,

            FfprobeFieldOrder = vstream.FieldOrder,
            FfprobeMappedParity = ffprobeParity,
            ParityMismatch = parityMismatch,

            SourceFps = sourceFps,
            IsNtscFamilyFps = isNtsc,
            SourceIsLikelyCfr = isLikelyCfr,
        };
    }

    // -------------------------------------------------------------------
    // Cadence match rate: fraction of all 5-frame windows containing exactly
    // 3P + 2I.  All five cyclic phases of PPPII (PPPII, PPIIP, PIIPP, IIPPP,
    // IPPPI) satisfy this count, so a perfectly telecined file scores ~100%.
    // O(n) via incremental counts.
    // -------------------------------------------------------------------
    internal static double ComputeCadenceMatchRate(List<FrameFlag> frames)
    {
        if (frames.Count < 5) return 0;

        int p = 0, intl = 0;
        for (int j = 0; j < 5; j++)
        {
            if (frames[j] == FrameFlag.Progressive) p++;
            else if (frames[j] == FrameFlag.Interlaced) intl++;
        }

        int windows = frames.Count - 4;
        int matches = (p == 3 && intl == 2) ? 1 : 0;

        for (int i = 1; i < windows; i++)
        {
            var leaving = frames[i - 1];
            if (leaving == FrameFlag.Progressive) p--;
            else if (leaving == FrameFlag.Interlaced) intl--;

            var entering = frames[i + 4];
            if (entering == FrameFlag.Progressive) p++;
            else if (entering == FrameFlag.Interlaced) intl++;

            if (p == 3 && intl == 2) matches++;
        }

        return matches / (double)windows;
    }

    // -------------------------------------------------------------------
    // Of all I frames in the file, the fraction whose centered 5-frame
    // neighborhood [i-2 .. i+2] is exactly 3P + 2I.  Used to distinguish
    // §7.2.2.4 (telecine: I frames embedded in 3:2 cycles, ratio high)
    // from §7.2.2.5 (native interlace: I frames in clusters, ratio low).
    // -------------------------------------------------------------------
    internal static double ComputeIFramesInCadenceRatio(List<FrameFlag> frames)
    {
        if (frames.Count < 5) return 0;

        long evaluated = 0, inCycle = 0;

        for (int i = 2; i < frames.Count - 2; i++)
        {
            if (frames[i] != FrameFlag.Interlaced) continue;
            evaluated++;

            int p = 0, intl = 0;
            for (int j = i - 2; j <= i + 2; j++)
            {
                if (frames[j] == FrameFlag.Progressive) p++;
                else if (frames[j] == FrameFlag.Interlaced) intl++;
            }

            if (p == 3 && intl == 2) inCycle++;
        }

        return evaluated > 0 ? inCycle / (double)evaluated : 0;
    }

    // -------------------------------------------------------------------
    // Parity from idet's full-file TFF/BFF counts, with NTSC fallback.
    //
    //   tffFrac ≥ 0.90  → TFF
    //   tffFrac ≤ 0.10  → BFF
    //   ambiguous + ffprobe gives a clear answer → use ffprobe
    //   ambiguous + NTSC fps → TFF (standards convention for NTSC interlace)
    //   else → Auto
    // -------------------------------------------------------------------
    internal static (FieldParity Parity, bool FromNtscFallback) DetectParity(
        long rawTff, long rawBff, bool isNtsc, FieldParity ffprobeParity)
    {
        long denom = rawTff + rawBff;
        if (denom <= 0)
        {
            // No interlaced frames detected at all.  Trust ffprobe if it has an opinion;
            // otherwise leave Auto and let downstream filters figure it out (only matters
            // when sparse interlaced frames slip into a mostly-progressive IVTC pass).
            if (ffprobeParity != FieldParity.Auto) return (ffprobeParity, false);
            if (isNtsc) return (FieldParity.Tff, true);
            return (FieldParity.Auto, false);
        }

        double tffFrac = rawTff / (double)denom;

        if (tffFrac >= ParityStrictDominance) return (FieldParity.Tff, false);
        if (tffFrac <= 1.0 - ParityStrictDominance) return (FieldParity.Bff, false);

        // Ambiguous idet result — defer to ffprobe if it's confident.
        if (ffprobeParity != FieldParity.Auto) return (ffprobeParity, false);

        // Last resort: NTSC standards convention is TFF.
        if (isNtsc) return (FieldParity.Tff, true);

        return (FieldParity.Auto, false);
    }

    // -------------------------------------------------------------------
    // Decision tree mapping the three metrics onto the five §7.2.2 categories.
    // Order matters: each branch is the strictest qualifier for that category.
    // -------------------------------------------------------------------
    internal static (ContentType Type, double Confidence, string Reason) Classify(
        int intCount, double progFrac, double cadenceRate, double iInCadence, bool parityMismatch)
    {
        double Penalize(double c) => parityMismatch ? Math.Max(0.20, c - 0.10) : c;

        // §7.2.2.1: Progressive (verified).  Strict — only when zero interlaced frames.
        // Per guide footnote [3], anything else gets the safe IVTC chain.
        if (intCount == 0)
        {
            return (ContentType.Progressive, Penalize(0.99),
                "Progressive (§7.2.2.1): zero interlaced frames — verified pure progressive.");
        }

        // §7.2.2.3: Interlaced.  "Every single frame is interlaced."
        if (progFrac <= PureInterlacedProgFracMax)
        {
            double conf = Math.Clamp(0.85 + (PureInterlacedProgFracMax - progFrac) * 3.0, 0.85, 0.99);
            return (ContentType.Interlaced, Penalize(conf),
                $"Interlaced (§7.2.2.3): {1.0 - progFrac:P2} of classified frames are interlaced.");
        }

        // §7.2.2.2: Telecined.  Uniform 3:2 cadence across the entire file.
        if (cadenceRate >= PureTelecineCadenceMin)
        {
            double conf = Math.Clamp(0.80 + (cadenceRate - PureTelecineCadenceMin) * 2.0, 0.80, 0.99);
            return (ContentType.Telecined, Penalize(conf),
                $"Telecined (§7.2.2.2): cadence match rate = {cadenceRate:P2} — " +
                "uniform 3:2 pulldown (PPPII) detected across the file.");
        }

        // §7.2.2.4: I frames embedded in 3:2 cycles → telecine in the interlaced regions.
        // Implements the guide's explicit verification step:
        //   "You should check the '30000/1001 fps NTSC' sections to make sure they
        //    are actually telecine, and not just interlaced."
        // Footnote [3]'s "safe pullup default for mostly-progressive content" is NOT
        // implemented here — it lives in RestoreStrategyMapper, which gates on source
        // fps so that non-NTSC-thirty sources (24p Blu-rays, 25p PAL, 60p sports)
        // aren't pushed through a 24000/1001-pinning IVTC chain.
        if (iInCadence >= TelecineIRatioMin)
        {
            double conf = Math.Clamp(0.60 + iInCadence * 0.3, 0.55, 0.90);
            return (ContentType.MixedProgressiveTelecine, Penalize(conf),
                $"Mixed progressive+telecine (§7.2.2.4): {iInCadence:P1} of interlaced frames " +
                $"sit inside 3:2 windows, cadence rate = {cadenceRate:P2}, progFrac = {progFrac:P2}.");
        }

        // §7.2.2.5: I frames are clustered (not in cadence) → genuine interlace mixed in.
        {
            // Confidence dips toward the §7.2.2.4 boundary (iInCadence ≈ 0.60).
            double margin = TelecineIRatioMin - iInCadence;
            double conf = Math.Clamp(0.50 + margin * 0.5, 0.45, 0.85);
            return (ContentType.MixedProgressiveInterlaced, Penalize(conf),
                $"Mixed progressive+interlaced (§7.2.2.5): only {iInCadence:P1} of interlaced " +
                $"frames are in 3:2 cycles, progFrac = {progFrac:P2}, cadence = {cadenceRate:P2}.");
        }
    }

    internal static string ParityStr(FieldParity parity) => parity switch
    {
        FieldParity.Tff => "tff",
        FieldParity.Bff => "bff",
        _ => "auto"
    };
}
