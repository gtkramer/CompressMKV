namespace CompressMkv;

/// <summary>
/// Restore filter graph generation for each content type and restore mode.
/// </summary>
public static class RestoreFilters
{
    /// <summary>
    /// Returns the appropriate filter graph and output fps for a ContentType + parity.
    /// </summary>
    public static (string Filter, string? OutputFps) For(ContentType type, FieldParity parity)
    {
        string p = ParityStr(parity);

        return type switch
        {
            // §7.2.3.1: No filter needed.
            ContentType.Progressive => ("", null),

            // §7.2.3.2: fieldmatch is ffmpeg's equivalent of MPlayer's pullup.
            // combmatch=full checks every frame for residual combing (default sc only
            // checks at scene changes).  An intermediate bwdif with deint=interlaced
            // cleans up any frames fieldmatch couldn't cleanly match — progressive
            // frames pass through untouched.  decimate drops the duplicate frame from
            // each 5-frame cycle → 24000/1001.
            ContentType.Telecined =>
                ($"fieldmatch=order={p}:combmatch=full,bwdif=mode=send_frame:parity={p}:deint=interlaced,decimate",
                 "24000/1001"),

            // §7.2.3.3: bwdif is a motion-adaptive deinterlacer (successor to yadif).
            // send_frame mode: one output frame per input frame, preserving native fps.
            ContentType.Interlaced => ($"bwdif=mode=send_frame:parity={p}", null),

            // §7.2.3.4: fieldmatch handles both progressive and telecined frames.
            // Progressive frames pass through; telecined frames get field-matched.
            // Same IVTC chain as §7.2.3.2 — bwdif cleanup + combmatch=full.
            ContentType.MixedProgressiveTelecine =>
                ($"fieldmatch=order={p}:combmatch=full,bwdif=mode=send_frame:parity={p}:deint=interlaced,decimate",
                 "24000/1001"),

            // §7.2.3.5: Deinterlace everything. Progressive frames pass through bwdif
            // with minimal degradation (motion-adaptive → no combing detected → passthrough).
            ContentType.MixedProgressiveInterlaced => ($"bwdif=mode=send_frame:parity={p}", null),

            _ => ("", null),
        };
    }

    /// <summary>
    /// Legacy overload for PreviewGenerator and other code that uses RestoreMode + parity.
    /// </summary>
    public static (string Filter, string? OutputFps) For(RestoreMode mode, FieldParity parity)
    {
        string p = ParityStr(parity);

        return mode switch
        {
            RestoreMode.Ivtc =>
                ($"fieldmatch=order={p}:combmatch=full,bwdif=mode=send_frame:parity={p}:deint=interlaced,decimate",
                 "24000/1001"),
            RestoreMode.Deinterlace => ($"bwdif=mode=send_frame:parity={p}", null),
            _ => ("", null),
        };
    }

    internal static string ParityStr(FieldParity parity) => parity switch
    {
        FieldParity.Tff => "tff",
        FieldParity.Bff => "bff",
        _ => "auto"
    };
}
