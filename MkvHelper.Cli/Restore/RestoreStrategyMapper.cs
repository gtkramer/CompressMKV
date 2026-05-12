namespace MkvHelper;

// =========================================================================
// Maps a §7.2.2 ContentType + source-rate context onto a §7.2.3 filter chain.
//
// The classifier in ContentDetector performs pure §7.2.2 identification.  All
// §7.2.3 *action* choices live here so that each branch reads as a direct
// reflection of one guide sub-section.
//
// The IVTC chain is gated on TWO source-rate conditions:
//
//   1. Source rate matches NTSC-thirty (30000/1001) exactly enough.
//      The IVTC chain decimates 30000/1001 → 24000/1001 and pins the output
//      with -r 24000/1001.  Applied to a 30/1 source (true 30p web/screen
//      content), a 25/1 PAL source, or a 60p source, ffmpeg would drop frames
//      to satisfy the rate pin and damage the content.
//
//   2. Source is constant frame rate (CFR).  VFR sources (screen recordings,
//      mobile captures with varying timestamps) make idet's per-frame cadence
//      metrics unreliable, and the rate pin would itself impose a fixed rate
//      on what should be a variable-rate stream.
//
// Off-script categories (e.g. Telecined classification on a 24p Blu-ray) are
// treated as detection anomalies — passed through unmodified rather than fed
// into a chain that would damage them.
//
// Coverage of the five §7.2.3 sub-sections:
//
//   §7.2.3.1 Progressive       → no filter, native fps
//   §7.2.3.2 Telecined         → IvtcChain @ 24000/1001  (gated on NTSC-thirty + CFR)
//   §7.2.3.3 Interlaced        → DeinterlaceChain @ native fps
//   §7.2.3.4 Mixed prog+TC     → IvtcChain @ 24000/1001  (gated on NTSC-thirty + CFR)
//   §7.2.3.5 Mixed prog+intl   → IvtcChain when ≥90% prog (favor-progressive
//                                + footnote [3] safe pullup default), else
//                                DeinterlaceChain @ native fps
// =========================================================================
public static class RestoreStrategyMapper
{
    /// <summary>
    /// Per guide §7.2.3.5: "If your video is 90% progressive and you never intend
    /// to show it on a TV, you should favor a progressive approach."  Same threshold
    /// also covers footnote [3]'s advice to default to pullup unless the source has
    /// been definitively verified to be entirely progressive.
    /// </summary>
    const double FavorProgressiveProgFracMin = 0.90;

    public static RestoreDecision MapToRestore(ContentDetectionResult detection)
    {
        FieldParity parity = detection.DetectedParity;
        ContentType contentType = detection.ContentType;

        // Guard: the IVTC chain only applies to a 30000/1001 CFR source.  Both
        // conditions must hold — a true-30p source (30/1) at any cadence rate
        // must NOT enter IVTC, and a VFR source's "rate" is meaningless.
        bool canApplyIvtc =
            (detection.SourceFps?.IsNtscThirty() ?? false) &&
            detection.SourceIsLikelyCfr;

        bool favorProgressive =
            detection.GlobalProgressiveFraction >= FavorProgressiveProgFracMin;

        (RestoreMode mode, string filter, Fps? fps, string notes) = contentType switch
        {
            // -----------------------------------------------------------
            // §7.2.3.1 Progressive (verified — intCount == 0).
            // "Progressive video requires no special filtering to encode."
            // -----------------------------------------------------------
            ContentType.Progressive =>
                NoFilter("Progressive (§7.2.3.1): verified pure progressive — no filter " +
                    $"(interlaced frame count = 0; {IvtcGuardsState(detection)})."),

            // -----------------------------------------------------------
            // §7.2.3.3 Interlaced.  Always safe at native rate, regardless of source fps.
            // "Use a deinterlacing filter before encoding."
            // -----------------------------------------------------------
            ContentType.Interlaced =>
                Deinterlace(parity,
                    "Interlaced (§7.2.3.3): bwdif (motion-adaptive deinterlacer) " +
                    "at native frame rate " +
                    $"(progFrac={detection.GlobalProgressiveFraction:P1} ≤ 5.0%; " +
                    $"{IvtcGuardsState(detection)})."),

            // -----------------------------------------------------------
            // §7.2.3.2 Telecined — only valid on a 30000/1001 CFR source.
            // -----------------------------------------------------------
            ContentType.Telecined when canApplyIvtc =>
                Ivtc(parity,
                    "Telecined (§7.2.3.2): IVTC via fieldmatch + bwdif(interlaced) + " +
                    "decimate to recover 24000/1001 " +
                    $"(cadence={detection.TelecineCadenceMatchRate:P1} ≥ 80.0%; " +
                    $"{IvtcGuardsState(detection)})."),

            // Cadence detected but the source rate or CFR guard fails: anomaly.
            // Pass through rather than damage the rate.
            ContentType.Telecined =>
                NoFilter(IvtcSkipReason("Telecine pattern detected", detection)),

            // -----------------------------------------------------------
            // §7.2.3.4 Mixed progressive+telecine — only valid on a 30000/1001 CFR source.
            // "pullup is designed to inverse-telecine telecined material while
            //  leaving progressive data alone."
            // -----------------------------------------------------------
            ContentType.MixedProgressiveTelecine when canApplyIvtc =>
                Ivtc(parity,
                    "Mixed prog+telecine (§7.2.3.4): fieldmatch leaves progressive " +
                    "data alone and inverse-telecines the telecined sections; " +
                    "decimate restores 24000/1001 " +
                    $"(i_in_cadence={detection.InterlacedFramesInCadenceRatio:P1} ≥ 60.0%; " +
                    $"{IvtcGuardsState(detection)})."),

            ContentType.MixedProgressiveTelecine =>
                NoFilter(IvtcSkipReason("Cadence-pattern I frames detected", detection)),

            // -----------------------------------------------------------
            // §7.2.3.5 Mixed progressive+interlaced — guide gives a tradeoff.
            //
            // (a) Favor progressive when ≥90% prog AND source is NTSC-thirty + CFR.
            //     This is the §7.2.3.5 dominance rule plus footnote [3]'s safe
            //     pullup default.
            //
            // (b) ≥90% prog but source is non-NTSC-thirty or VFR: idet noise on a
            //     24p/25p/60p disc or screen recording.  Pass through.
            //
            // (c) <90% prog: deinterlace all.
            // -----------------------------------------------------------
            ContentType.MixedProgressiveInterlaced when favorProgressive && canApplyIvtc =>
                Ivtc(parity,
                    $"Mixed prog+interlaced ({detection.GlobalProgressiveFraction:P1} ≥ 90.0% " +
                    "progressive): IVTC chain per guide §7.2.3.5 favor-progressive + " +
                    "footnote [3] safe pullup default " +
                    $"({IvtcGuardsState(detection)})."),

            ContentType.MixedProgressiveInterlaced when favorProgressive =>
                NoFilter(IvtcSkipReason(
                    $"Mostly progressive ({detection.GlobalProgressiveFraction:P1})", detection)),

            ContentType.MixedProgressiveInterlaced =>
                Deinterlace(parity,
                    $"Mixed prog+interlaced ({detection.GlobalProgressiveFraction:P1} < 90.0% " +
                    "progressive): deinterlace all per guide §7.2.3.5 — " +
                    "\"if it is only half progressive, you probably want to encode it " +
                    $"as if it is all interlaced.\" ({IvtcGuardsState(detection)})."),

            _ => NoFilter("Unknown content type."),
        };

        return new RestoreDecision
        {
            Mode = mode,
            Confidence = detection.Confidence,
            FilterGraph = filter,
            OutputFps = fps,
            Notes = notes,
            DecisionReason = detection.Reason,
            ContentDetection = detection,
        };
    }

    // ---- Action constructors (one per §7.2.3 chain choice) ----

    private static (RestoreMode, string, Fps?, string) NoFilter(string notes) =>
        (RestoreMode.None, "", null, notes);

    private static (RestoreMode, string, Fps?, string) Ivtc(FieldParity parity, string notes) =>
        (RestoreMode.Ivtc, RestoreFilters.IvtcChain(parity), RestoreFilters.IvtcOutputFps, notes);

    private static (RestoreMode, string, Fps?, string) Deinterlace(FieldParity parity, string notes) =>
        (RestoreMode.Deinterlace, RestoreFilters.DeinterlaceChain(parity), null, notes);

    // ---- Diagnostics ----

    /// <summary>
    /// Renders the source-rate / CFR guard state in a uniform parenthetical
    /// form so every branch's notes can echo what the guards actually saw.
    /// Symmetric to <see cref="IvtcSkipReason"/>'s explanation of failure —
    /// every pass-path note includes the same data so cross-branch comparison
    /// is grep-able.
    /// </summary>
    private static string IvtcGuardsState(ContentDetectionResult detection)
    {
        string fpsStr = detection.SourceFps?.ToString() ?? "?";
        bool ntsc = detection.SourceFps?.IsNtscThirty() ?? false;
        return $"fps={fpsStr} {(ntsc ? "NTSC-thirty" : "non-NTSC-thirty")}, " +
               $"{(detection.SourceIsLikelyCfr ? "CFR" : "VFR")}";
    }

    /// <summary>
    /// Builds the explanation string for an IVTC-skip pass-through, naming the
    /// specific guard(s) that failed.  Distinguishes "wrong source rate" from
    /// "VFR source" so the log makes it clear why the chain didn't fire.
    /// </summary>
    private static string IvtcSkipReason(string trigger, ContentDetectionResult detection)
    {
        string fpsStr = detection.SourceFps?.ToString() ?? "?";
        List<string> reasons = [];
        if (!(detection.SourceFps?.IsNtscThirty() ?? false))
            reasons.Add($"source is {fpsStr} fps (not 30000/1001)");
        if (!detection.SourceIsLikelyCfr)
            reasons.Add("source is variable frame rate");

        string why = reasons.Count > 0 ? string.Join(" and ", reasons) : "IVTC guard failed";
        return $"{trigger} but {why} — guide §7.2 IVTC chain doesn't apply. Passing through.";
    }
}
