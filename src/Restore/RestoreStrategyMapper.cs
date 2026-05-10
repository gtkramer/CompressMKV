using System.Globalization;

namespace CompressMkv;

// =========================================================================
// Maps a §7.2.2 ContentType + source-rate context onto a §7.2.3 filter chain.
//
// The classifier in ContentDetector performs pure §7.2.2 identification.  All
// §7.2.3 *action* choices live here so that each branch reads as a direct
// reflection of one guide sub-section.
//
// Source-rate awareness is required because the IVTC chain pins output to
// 24000/1001 fps — applying it to anything other than a 30000/1001 source
// (the canonical NTSC telecine/interlace storage rate) damages the content:
// a 24p Blu-ray with a few idet false positives, a 25p PAL disc, or a 60p
// sports Blu-ray would all see frames dropped to hit 24p.  The guide is
// scoped to NTSC DVDs precisely because IVTC only makes sense for that source.
//
// Coverage of the five §7.2.3 sub-sections:
//
//   §7.2.3.1 Progressive       → no filter, native fps
//   §7.2.3.2 Telecined         → IvtcChain @ 24000/1001  (NTSC-thirty source only)
//   §7.2.3.3 Interlaced        → DeinterlaceChain @ native fps
//   §7.2.3.4 Mixed prog+TC     → IvtcChain @ 24000/1001  (NTSC-thirty source only)
//   §7.2.3.5 Mixed prog+intl   → IvtcChain when ≥90% prog (favor-progressive
//                                + footnote [3] safe pullup default), else
//                                DeinterlaceChain @ native fps
//
// Off-script categories (e.g. Telecined classification on a 24p Blu-ray) are
// treated as detection anomalies — passed through unmodified rather than fed
// into a chain that would damage them.
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

    /// <summary>
    /// The IVTC chain decimates 30000/1001 → 24000/1001.  Applying it to a source
    /// at any other rate forces ffmpeg to drop frames after the filter to satisfy
    /// `-r 24000/1001 -fps_mode cfr`, damaging the content.  The guide is explicitly
    /// scoped to NTSC DVDs (30000/1001 storage); this guard enforces that scope.
    /// </summary>
    const double NtscThirtyFps = 30000.0 / 1001.0;
    const double NtscThirtyFpsTolerance = 0.05;

    public static RestoreDecision MapToRestore(ContentDetectionResult detection)
    {
        var parity = detection.DetectedParity;
        var contentType = detection.ContentType;
        bool sourceIsNtscThirty = IsNtscThirtyFps(detection.SourceFps);
        bool favorProgressive =
            detection.GlobalProgressiveFraction >= FavorProgressiveProgFracMin;

        var (mode, filter, fps, notes) = contentType switch
        {
            // -----------------------------------------------------------
            // §7.2.3.1 Progressive (verified — intCount == 0).
            // "Progressive video requires no special filtering to encode."
            // -----------------------------------------------------------
            ContentType.Progressive =>
                NoFilter("Progressive (§7.2.3.1): verified pure progressive — no filter."),

            // -----------------------------------------------------------
            // §7.2.3.3 Interlaced.  Always safe at native rate.
            // "Use a deinterlacing filter before encoding."
            // -----------------------------------------------------------
            ContentType.Interlaced =>
                Deinterlace(parity,
                    "Interlaced (§7.2.3.3): bwdif (motion-adaptive deinterlacer) " +
                    "at native frame rate."),

            // -----------------------------------------------------------
            // §7.2.3.2 Telecined — only valid for NTSC-thirty source.
            // "the best filter, pullup, is described in the mixed progressive
            //  and telecine section."  -ofps 24000/1001 is required.
            // -----------------------------------------------------------
            ContentType.Telecined when sourceIsNtscThirty =>
                Ivtc(parity,
                    "Telecined (§7.2.3.2): IVTC via fieldmatch + bwdif(interlaced) + " +
                    "decimate to recover 24000/1001."),

            // Telecine pattern detected on a non-NTSC-thirty source: anomaly.
            // Pass through rather than damage the rate.
            ContentType.Telecined =>
                NoFilter(
                    $"Telecine pattern detected but source is " +
                    $"{Fps(detection.SourceFps)} fps (not 30000/1001) — guide §7.2 " +
                    "IVTC chain doesn't apply. Passing through."),

            // -----------------------------------------------------------
            // §7.2.3.4 Mixed progressive+telecine — only valid for NTSC-thirty.
            // "pullup is designed to inverse-telecine telecined material while
            //  leaving progressive data alone."  -ofps 24000/1001 is required.
            // -----------------------------------------------------------
            ContentType.MixedProgressiveTelecine when sourceIsNtscThirty =>
                Ivtc(parity,
                    "Mixed prog+telecine (§7.2.3.4): fieldmatch leaves progressive " +
                    "data alone and inverse-telecines the telecined sections; " +
                    "decimate restores 24000/1001."),

            // Cadence-pattern I frames on a non-NTSC-thirty source: idet noise on
            // essentially-progressive content.  Pass through.
            ContentType.MixedProgressiveTelecine =>
                NoFilter(
                    $"Cadence-pattern I frames detected on a {Fps(detection.SourceFps)} " +
                    "fps source — IVTC chain only applies to 30000/1001 storage. " +
                    "Treating as progressive noise. Passing through."),

            // -----------------------------------------------------------
            // §7.2.3.5 Mixed progressive+interlaced — guide gives a tradeoff.
            //
            // (a) Favor progressive when ≥90% prog AND source is NTSC-thirty.
            //     This is the §7.2.3.5 dominance rule plus footnote [3]'s safe
            //     pullup default.  IVTC chain preserves progressive bulk and
            //     cleans up sparse interlaced frames via bwdif=interlaced.
            //
            // (b) ≥90% prog but non-NTSC-thirty source: idet noise on a 24p/25p/60p
            //     disc.  IVTC would damage the rate; pass through.
            //
            // (c) <90% prog: deinterlace all.  "If it is only half progressive,
            //     you probably want to encode it as if it is all interlaced."
            // -----------------------------------------------------------
            ContentType.MixedProgressiveInterlaced when favorProgressive && sourceIsNtscThirty =>
                Ivtc(parity,
                    $"Mixed prog+interlaced ({detection.GlobalProgressiveFraction:P1} " +
                    "progressive, NTSC-thirty source): IVTC chain per guide §7.2.3.5 " +
                    "favor-progressive + footnote [3] safe pullup default."),

            ContentType.MixedProgressiveInterlaced when favorProgressive =>
                NoFilter(
                    $"Mixed prog+interlaced ({detection.GlobalProgressiveFraction:P1} " +
                    $"progressive) on a {Fps(detection.SourceFps)} fps source — " +
                    "interlaced flags are likely idet noise on a non-telecine-format " +
                    "source. Passing through."),

            ContentType.MixedProgressiveInterlaced =>
                Deinterlace(parity,
                    $"Mixed prog+interlaced ({detection.GlobalProgressiveFraction:P1} " +
                    "progressive): deinterlace all per guide §7.2.3.5 — " +
                    "\"if it is only half progressive, you probably want to encode it " +
                    "as if it is all interlaced.\""),

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

    private static (RestoreMode, string, string?, string) NoFilter(string notes) =>
        (RestoreMode.None, "", null, notes);

    private static (RestoreMode, string, string?, string) Ivtc(FieldParity parity, string notes) =>
        (RestoreMode.Ivtc, RestoreFilters.IvtcChain(parity), RestoreFilters.IvtcOutputFps, notes);

    private static (RestoreMode, string, string?, string) Deinterlace(FieldParity parity, string notes) =>
        (RestoreMode.Deinterlace, RestoreFilters.DeinterlaceChain(parity), null, notes);

    // ---- Helpers ----

    private static bool IsNtscThirtyFps(double? fps) =>
        fps.HasValue && Math.Abs(fps.Value - NtscThirtyFps) < NtscThirtyFpsTolerance;

    private static string Fps(double? fps) =>
        fps.HasValue ? fps.Value.ToString("F3", CultureInfo.InvariantCulture) : "?";
}
