namespace CompressMkv;

/// <summary>
/// Restore strategy mapper: ContentType → RestoreDecision.
/// Maps each of the five MPlayer guide §7.2 categories to the appropriate
/// ffmpeg filter chain following the guide's encoding recommendations.
/// </summary>
public static class RestoreStrategyMapper
{
    /// <summary>
    /// Converts a ContentDetectionResult into a RestoreDecision with the appropriate
    /// filter graph and output framerate per MPlayer guide §7.2.3.
    /// </summary>
    public static RestoreDecision MapToRestore(ContentDetectionResult detection)
    {
        var parity = detection.DetectedParity;
        var (filter, fps) = RestoreFilters.For(detection.ContentType, parity);

        string notes = detection.ContentType switch
        {
            // §7.2.3.1: Progressive content needs no special filtering.
            // Just encode at native frame rate (24000/1001 if stored as such).
            ContentType.Progressive =>
                "Progressive: no video filter needed. " +
                "Per MPlayer guide §7.2.3.1: encode at native frame rate.",

            // §7.2.3.2: Telecine can be reversed via inverse-telecine.
            // fieldmatch (ffmpeg's pullup equivalent) + decimate → 24000/1001 fps.
            ContentType.Telecined =>
                "Hard telecine: IVTC via fieldmatch+decimate to recover original 24p. " +
                "Per MPlayer guide §7.2.3.2: inverse telecine recovers 24000/1001.",

            // §7.2.3.3: Interlaced content — deinterlace with bwdif (motion-adaptive).
            // bwdif is the modern successor to yadif recommended by the guide.
            ContentType.Interlaced =>
                "Natively interlaced: deinterlace via bwdif (send_frame mode). " +
                "Per MPlayer guide §7.2.3.3: deinterlace before scaling.",

            // §7.2.3.4: Mixed progressive+telecine — pullup/fieldmatch handles both.
            // Progressive frames pass through unmodified; telecined sections get matched.
            ContentType.MixedProgressiveTelecine =>
                "Mixed progressive+telecine: fieldmatch+decimate handles both. " +
                "Per MPlayer guide §7.2.3.4: pullup/fieldmatch leaves progressive data " +
                "alone and inverse-telecines the telecined sections. Output 24000/1001.",

            // §7.2.3.5: Mixed progressive+interlaced — compromise.
            // Guide says: either treat as progressive (losing interlaced quality) or as
            // interlaced (duplicating some progressive frames). We deinterlace all.
            ContentType.MixedProgressiveInterlaced =>
                "Mixed progressive+interlaced: deinterlace all (compromise). " +
                "Per MPlayer guide §7.2.3.5: treating as interlaced preserves all " +
                "interlaced content while mildly duplicating some progressive frames.",

            _ => "Unknown content type."
        };

        return new RestoreDecision
        {
            Mode = ContentTypeToMode(detection.ContentType),
            Confidence = detection.Confidence,
            FilterGraph = filter,
            OutputFps = fps,
            Notes = notes,
            DecisionReason = detection.Reason,
            ContentDetection = detection,
        };
    }

    private static RestoreMode ContentTypeToMode(ContentType type) => type switch
    {
        ContentType.Progressive => RestoreMode.None,
        ContentType.Telecined => RestoreMode.Ivtc,
        ContentType.Interlaced => RestoreMode.Deinterlace,
        ContentType.MixedProgressiveTelecine => RestoreMode.Ivtc,
        ContentType.MixedProgressiveInterlaced => RestoreMode.Deinterlace,
        _ => RestoreMode.None
    };
}
