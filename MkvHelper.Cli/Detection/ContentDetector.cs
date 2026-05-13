using System.Text.RegularExpressions;

namespace MkvHelper;

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
//   intCount == 0                          → §7.2.2.1 Progressive (verified —
//                                              "you should never see any interlacing")
//   intCount within idet noise floor       → §7.2.2.1 Progressive (absorbed —
//                                              see "idet noise floor" below)
//   progFrac ≤ 5%                          → §7.2.2.3 Interlaced ("every single
//                                              frame is interlaced")
//   cadenceRate ≥ 80%                      → §7.2.2.2 Telecined (uniform PPPII)
//   iInCadence ≥ 60%                       → §7.2.2.4 Mixed prog+telecine
//                                              (I frames sit inside 3:2 cycles)
//   intCount large enough to verify        → §7.2.2.5 Mixed prog+interlaced
//                                              (I frames are clusters)
//   else                                   → §7.2.2.4 Mixed prog+telecine (the
//                                              guide's §7.2.3.1 safe default for
//                                              trace I frames without cadence)
//
// idet noise floor (§7.2.2.1 relaxation):
//   The MPlayer guide says "you should never see any interlacing" in a
//   progressive source — but it was written for human visual inspection
//   (frame-stepping with the `.` key), not for an automated detector. idet
//   is a statistical heuristic: it compares inter-line correlation against
//   fixed thresholds and produces false positives on content with high
//   vertical frequencies (animation ink lines, scaled UHD detail), on
//   grain in low-contrast regions, on compression artefacts near row
//   boundaries, and at scene cuts where its 5-frame context spans
//   unrelated images. A human watcher would dismiss these as imperceptible;
//   the strict gate cannot.  We therefore allow a small unstructured-noise
//   tolerance — capped at ~0.1% of classified frames (min 8) and only when
//   the trace I-frames carry no cadence/cluster signal — to absorb idet's
//   known false-positive rate without leaking real signal into Progressive.
//
// §7.2.2.5 positive-evidence requirement:
//   The guide describes §7.2.2.5 as a category you reach by *verification*:
//   "This category looks just like 'mixed progressive and telecine', until
//    you examine the 30000/1001 fps sections and see that they do not have
//    the telecine pattern."  That phrasing requires enough interlaced
//   content to examine.  Reaching §7.2.2.5 by simple by-elimination on
//   trace I-frames contradicts both that wording and §7.2.3.1's explicit
//   "Unless you are sure, it is safest to treat the video as mixed
//   progressive and telecine" (with footnote [3] reinforcing pullup as
//   the safe default).  We therefore require positive evidence — enough
//   I-frames for iInCadence to be statistically informative — before
//   choosing §7.2.2.5, and fall back to §7.2.2.4 otherwise.
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

    // ---- idet noise floor (§7.2.2.1 relaxation) ----

    /// <summary>
    /// Absolute floor on the number of false-positive I-frames absorbed into
    /// Progressive.  Anchors the threshold for short clips where the
    /// fractional floor would be negligible.
    /// </summary>
    const int IdetNoiseAbsFloor = 8;

    /// <summary>
    /// Fractional floor: I-frames counting as idet noise must not exceed this
    /// share of classified frames.  0.1% is well above idet's published
    /// false-positive rate on clean progressive content but well below the
    /// I-frame density of any genuinely mixed source.
    /// </summary>
    const double IdetNoiseFracFloor = 0.001;

    /// <summary>
    /// Cadence-match rate ceiling for the noise-floor branch.  Trace I-frames
    /// that happen to sit in 3:2 windows are not noise — they are evidence of
    /// telecine — so we refuse to absorb them.
    /// </summary>
    const double IdetNoiseMaxCadenceRate = 0.05;

    /// <summary>
    /// iInCadence ceiling for the noise-floor branch.  With very few I-frames
    /// the metric is statistically noisy; this is a safety net against
    /// absorbing a handful of in-cadence frames.
    /// </summary>
    const double IdetNoiseMaxIInCadence = 0.10;

    // ---- §7.2.2.5 positive-evidence thresholds ----

    /// <summary>
    /// Minimum absolute I-frame count required before iInCadence is considered
    /// statistically meaningful enough to choose §7.2.2.5 over §7.2.2.4.  At
    /// 100 I-frames the standard error on a 60% threshold is ~5 percentage
    /// points — small enough to trust the verdict.
    /// </summary>
    const int MixedInterlaceMinIFrames = 100;

    /// <summary>
    /// Alternative fractional floor: 0.5% interlaced content is well clear of
    /// idet's noise floor and large enough to verify lack of cadence.
    /// </summary>
    const double MixedInterlaceMinIFraction = 0.005;

    // ---- Regex for per-frame idet metadata ----

    [GeneratedRegex(
        @"lavfi\.idet\.multiple\.current_frame=(\w+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex IdetFrameRegex();

    /// <summary>
    /// Matches idet's end-of-stream "Multi frame detection" aggregate line.
    /// Example: "[Parsed_idet_0 @ 0x...] Multi frame detection: TFF: 0 BFF: 0 Progressive: 248594 Undetermined: 1"
    /// </summary>
    [GeneratedRegex(
        @"Multi frame detection:\s*TFF:\s*(\d+)\s*BFF:\s*(\d+)\s*Progressive:\s*(\d+)\s*Undetermined:\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex IdetAggregateRegex();

    /// <summary>
    /// Single-pass content detection.  Decodes the entire file once with ffmpeg's
    /// idet filter, computes whole-file metrics from the per-frame flag stream,
    /// and classifies into one of the five §7.2.2 categories.
    /// Source-agnostic: works on DVDs, Blu-rays, or any other container/codec.
    ///
    /// When <paramref name="useHwaccel"/> is true the source is decoded on
    /// NVDEC and downloaded to system memory for the (CPU-only) idet filter;
    /// otherwise decode runs on the CPU.  ResourcePool picks the path based
    /// on which resources are free at admission time — see
    /// <see cref="Config.DetectionAlternatives"/>.
    /// </summary>
    public static async Task<ContentDetectionResult> DetectAsync(
        Config cfg, string input, FfprobeStream vstream, bool useHwaccel,
        CancellationToken ct, IPipelineLogger? logger = null)
    {
        logger ??= NullLogger.Instance;
        // ---- Build ffmpeg args ----
        // Loglevel "info" is required so idet's end-of-stream aggregate ("Multi
        // frame detection: ...") reaches stderr — that line is logged at info
        // level, not warning or error.  The aggregate cross-check in
        // CheckAggregateAgreement depends on this.  -nostats suppresses the
        // periodic decoder progress chatter that would otherwise fill stderr
        // at info level.
        int threads = useHwaccel ? cfg.DetectionGpuThreads : cfg.DetectionCpuThreads;
        List<string> argList =
        [
            "-hide_banner", "-loglevel", "info", "-nostats",
            "-threads", threads.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ];

        if (useHwaccel)
        {
            // Decode on NVDEC and have ffmpeg auto-download to system memory
            // in the source's native bit depth, so idet sees the same sw
            // frames it would have on the CPU path.  -hwaccel_output_format
            // is what triggers the auto-download — without it the decoder
            // produces hwframes that idet can't consume.
            PipelineFormat format = PipelineFormat.FromStream(vstream);
            argList.AddRange(["-hwaccel", "cuda", "-hwaccel_output_format", format.HwaccelOutputFormat]);
        }

        argList.AddRange([
            "-i", input,
            "-an", "-sn", "-dn",
            "-vf", "idet,metadata=mode=print:file=-:direct=1",
            "-f", "null", "-"
        ]);

        // ---- Per-frame accumulation ----
        List<FrameFlag> frames = [];
        long totalTff = 0, totalBff = 0;

        Regex regex = IdetFrameRegex();

        logger.SetStage("Detect", "decoding full file with idet");
        logger.LogInfo("Detection: decoding full file with idet (single pass).");

        void ProcessLine(string line)
        {
            Match match = regex.Match(line);
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

        (int exitCode, string stderr) = await ContainerTools.RunFfmpegStreamingAsync(argList.ToArray(), ProcessLine, ct);

        logger.LogInfo($"Detection: decoded {frames.Count:N0} frames.");
        if (exitCode != 0)
            logger.LogWarning($"ffmpeg exited with code {exitCode}");

        // Parse idet's end-of-stream aggregate line ("Multi frame detection:...") from stderr.
        // This is ffmpeg's own claim about the file using the same detector we sampled per-frame.
        // Cross-checking validates our parser; significant divergence indicates a bug.
        (long? aggProg, long? aggTff, long? aggBff, long? aggUndet) = ParseIdetAggregate(stderr);

        // ---- Per-frame counts ----
        int progCount = 0, intCount = 0, undetCount = 0;
        foreach (FrameFlag f in frames)
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
        FieldParity ffprobeParity = FieldOrderMapper.MapToParity(
            vstream.FieldOrder?.Trim().ToLowerInvariant());

        (FieldParity parity, bool parityFromNtsc) = DetectParity(totalTff, totalBff, isNtsc, ffprobeParity);

        bool parityMismatch =
            ffprobeParity != FieldParity.Auto &&
            parity != FieldParity.Auto &&
            ffprobeParity != parity;

        // ---- Cross-check our per-frame counts against idet's aggregate (sanity check) ----
        bool aggregateAgrees = CheckAggregateAgreement(
            aggProg, aggTff, aggBff, aggUndet,
            progCount, totalTff, totalBff, undetCount);

        // ---- Classify ----
        (ContentType contentType, double confidence, string reason) = Classify(
            intCount, classified, progFrac, cadenceRate, iInCadence, parityMismatch);

        logger.LogInfo($"Detection complete: {contentType} (confidence={confidence:P0})");
        logger.LogInfo(
            $"  progFrac={progFrac:P1}, cadence={cadenceRate:P1}, " +
            $"i_in_cadence={iInCadence:P1}, parity={parity}" +
            (parityFromNtsc ? " (NTSC fallback)" : "") +
            $", source={sourceFps?.ToString() ?? "?"}" +
            (isLikelyCfr ? "" : " (VFR)") +
            $", frames={frames.Count:N0} (P={progCount:N0} I={intCount:N0} U={undetCount:N0})");
        if (parityMismatch)
            logger.LogWarning(
                $"PARITY MISMATCH: idet={parity} vs ffprobe={ffprobeParity} " +
                $"(ffprobe field_order={vstream.FieldOrder})");
        if (!aggregateAgrees)
            logger.LogWarning(
                "idet aggregate disagrees with per-frame stream — likely parser bug.");

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

            IdetAggregateAgrees = aggregateAgrees,
            IdetAggregateProgressive = aggProg,
            IdetAggregateTff = aggTff,
            IdetAggregateBff = aggBff,
            IdetAggregateUndetermined = aggUndet,
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
            FrameFlag leaving = frames[i - 1];
            if (leaving == FrameFlag.Progressive) p--;
            else if (leaving == FrameFlag.Interlaced) intl--;

            FrameFlag entering = frames[i + 4];
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

        // Last resort: NTSC standards convention is TFF.  This is wrong for
        // genuinely BFF NTSC content where idet's dominance fell in the 80–90%
        // band AND ffprobe reported "unknown" — but BFF NTSC discs are
        // vanishingly rare (the standard mandates TFF and ~99%+ of NTSC content
        // follows it), so the bias is acceptable.  If a BFF NTSC source is
        // misidentified, the visible symptom is shimmering or reversed motion
        // in the deinterlace output — visible enough that the user will catch
        // it on first playback.
        if (isNtsc) return (FieldParity.Tff, true);

        return (FieldParity.Auto, false);
    }

    // -------------------------------------------------------------------
    // Decision tree mapping the three metrics onto the five §7.2.2 categories.
    // Order matters: each branch is the strictest qualifier for that category.
    // -------------------------------------------------------------------
    internal static (ContentType Type, double Confidence, string Reason) Classify(
        int intCount, int totalClassified, double progFrac, double cadenceRate, double iInCadence,
        bool parityMismatch)
    {
        double Penalize(double c) => parityMismatch ? Math.Max(0.20, c - 0.10) : c;

        // §7.2.2.1 (strict): zero interlaced frames — verified pure progressive,
        // matching the guide's literal "you should never see any interlacing."
        if (intCount == 0)
        {
            return (ContentType.Progressive, Penalize(0.99),
                "Progressive (§7.2.2.1): zero interlaced frames — verified pure progressive.");
        }

        // §7.2.2.1 (noise-floor relaxation): the guide's strict wording was
        // written for human visual inspection, which is immune to idet's known
        // false-positive modes (high-frequency vertical detail, grain in
        // low-contrast regions, compression artefacts, scene cuts).  Absorb a
        // bounded amount of unstructured trace I-frames into Progressive — but
        // only when they carry no cadence or cluster signal, so we never mask
        // genuine §7.2.2.2/4/5 content.
        int noiseCap = Math.Max(IdetNoiseAbsFloor,
            (int)Math.Round(IdetNoiseFracFloor * totalClassified));
        if (intCount <= noiseCap &&
            cadenceRate < IdetNoiseMaxCadenceRate &&
            iInCadence  < IdetNoiseMaxIInCadence)
        {
            return (ContentType.Progressive, Penalize(0.95),
                $"Progressive (§7.2.2.1, idet noise floor): {intCount:N0} interlaced frame(s) " +
                $"in {totalClassified:N0} classified ({1.0 - progFrac:P3}) — within idet's " +
                $"false-positive floor ({noiseCap:N0}), no cadence (rate={cadenceRate:P2}) " +
                $"or cluster signal (iInCadence={iInCadence:P1}).");
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
        // The §7.2.3 mapper gates the IVTC chain on source fps so that non-NTSC-thirty
        // sources (24p Blu-rays, 25p PAL, 60p sports) aren't pushed through a
        // 24000/1001-pinning IVTC chain even when this branch fires.
        if (iInCadence >= TelecineIRatioMin)
        {
            double conf = Math.Clamp(0.60 + iInCadence * 0.3, 0.55, 0.90);
            return (ContentType.MixedProgressiveTelecine, Penalize(conf),
                $"Mixed progressive+telecine (§7.2.2.4): {iInCadence:P1} of interlaced frames " +
                $"sit inside 3:2 windows, cadence rate = {cadenceRate:P2}, progFrac = {progFrac:P2}.");
        }

        // §7.2.2.5: I frames are clustered (not in cadence) → genuine interlace mixed in.
        // The guide treats this category as a verification step on top of §7.2.2.4
        // ("looks just like §7.2.2.4, until you examine the 30000/1001 fps sections
        // and see that they do not have the telecine pattern").  That examination
        // is only meaningful with enough I-frames for iInCadence to be statistically
        // informative — below the threshold the verdict is dominated by sampling
        // noise.  Require positive evidence; otherwise fall through to §7.2.2.4.
        bool enoughIFramesToVerify =
            intCount >= MixedInterlaceMinIFrames ||
            (totalClassified > 0 && intCount >= MixedInterlaceMinIFraction * totalClassified);

        if (enoughIFramesToVerify)
        {
            // Confidence dips toward the §7.2.2.4 boundary (iInCadence ≈ 0.60).
            double margin = TelecineIRatioMin - iInCadence;
            double conf = Math.Clamp(0.50 + margin * 0.5, 0.45, 0.85);
            return (ContentType.MixedProgressiveInterlaced, Penalize(conf),
                $"Mixed progressive+interlaced (§7.2.2.5): only {iInCadence:P1} of interlaced " +
                $"frames are in 3:2 cycles, progFrac = {progFrac:P2}, cadence = {cadenceRate:P2}.");
        }

        // Trace I-frames past the noise floor but below §7.2.2.5's positive-evidence
        // threshold: per §7.2.3.1, "Unless you are sure, it is safest to treat the
        // video as mixed progressive and telecine."  Footnote [3] reinforces this —
        // pullup is the conservative default unless the source is definitively
        // verified to be entirely progressive.  Confidence is modest because we are
        // explicitly in the guide's "not sure" regime.
        {
            double conf = Math.Clamp(0.55 + progFrac * 0.15, 0.50, 0.70);
            return (ContentType.MixedProgressiveTelecine, Penalize(conf),
                $"Mixed progressive+telecine (§7.2.2.4, §7.2.3.1 safe default): " +
                $"{intCount:N0} interlaced frame(s) in {totalClassified:N0} classified " +
                $"({1.0 - progFrac:P3}) — past idet noise floor but below §7.2.2.5 " +
                $"verification threshold (iInCadence={iInCadence:P1}, cadence={cadenceRate:P2}). " +
                "Defaulting to pullup per §7.2.3.1.");
        }
    }

    internal static string ParityStr(FieldParity parity) => parity switch
    {
        FieldParity.Tff => "tff",
        FieldParity.Bff => "bff",
        _ => "auto"
    };

    // -------------------------------------------------------------------
    // idet aggregate parsing (cross-check).
    // -------------------------------------------------------------------

    internal static (long? Prog, long? Tff, long? Bff, long? Undet) ParseIdetAggregate(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return (null, null, null, null);

        // idet emits "Multi frame detection:" TWICE — once at filter init (all
        // zeros), once at end-of-stream with the real counts.  Take the LAST
        // match to get the meaningful aggregate.
        MatchCollection matches = IdetAggregateRegex().Matches(stderr);
        if (matches.Count == 0) return (null, null, null, null);
        Match match = matches[^1];

        return (
            long.Parse(match.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture),
            long.Parse(match.Groups[4].Value, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Compares idet's own aggregate counts to our per-frame totals.  Returns true
    /// if they agree (or if idet didn't emit an aggregate, which is a soft pass).
    /// On disagreement, logs a warning so a parser bug is visible to the user.
    /// </summary>
    /// <remarks>
    /// Tolerance: 1 frame difference is normal (idet's aggregate is computed
    /// after the multi-frame detector's lookahead, while our stream parse may
    /// see one fewer frame at end-of-stream).  Anything larger is a bug.
    /// </remarks>
    internal static bool CheckAggregateAgreement(
        long? aggProg, long? aggTff, long? aggBff, long? aggUndet,
        int progCount, long tffCount, long bffCount, int undetCount)
    {
        // No aggregate → can't check; treat as soft pass.
        if (aggProg is null || aggTff is null || aggBff is null || aggUndet is null)
            return true;

        const long Tolerance = 1;
        bool ok =
            Math.Abs(aggProg.Value - progCount) <= Tolerance &&
            Math.Abs(aggTff.Value - tffCount)   <= Tolerance &&
            Math.Abs(aggBff.Value - bffCount)   <= Tolerance &&
            Math.Abs(aggUndet.Value - undetCount) <= Tolerance;

        // Disagreement is logged at the call site via the logger now (this
        // helper is pure-function so it can be unit-tested without a logger).

        return ok;
    }
}
