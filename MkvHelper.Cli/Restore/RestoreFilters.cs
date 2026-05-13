namespace MkvHelper;

/// <summary>
/// Restoration filter graph primitives.  Two chains cover every guide §7.2.3 action:
///
///   IvtcChain        — fieldmatch + bwdif(interlaced) + decimate.
///                      Reverses 3:2 pulldown to recover 24000/1001 progressive.
///                      Used for §7.2.3.2 (Telecined), §7.2.3.4 (Mixed prog+telecine),
///                      and the §7.2.3.5 favor-progressive sub-decision.
///                      Caller MUST also set -r 24000/1001 — this chain decimates
///                      30000/1001 → 24000/1001 and the output rate must be pinned.
///
///   DeinterlaceChain — bwdif=send_frame at the source's native rate.
///                      Used for §7.2.3.3 (Interlaced) and the deinterlace-all
///                      compromise of §7.2.3.5 (Mixed prog+interlaced).
///                      Motion-adaptive: progressive frames in mixed sources
///                      pass through with minimal degradation.
///
/// All filters are stock ffmpeg.
/// </summary>
public static class RestoreFilters
{
    /// <summary>
    /// Output frame rate after the IVTC chain — the canonical 24000/1001 film rate
    /// before telecine was applied.  Set with `-r` and `-fps_mode cfr` to pin output.
    /// </summary>
    public static readonly Fps IvtcOutputFps = Fps.Ntsc24;

    /// <summary>
    /// IVTC chain — equivalent to MPlayer's filmdint (guide §7.2.3.4 method 2):
    /// fieldmatch reconstructs progressive frames from telecined fields,
    /// bwdif=interlaced cleans up frames fieldmatch couldn't reconstruct
    /// (drop-free, unlike pullup), and decimate removes the duplicate
    /// from each 5-frame cycle.
    /// </summary>
    public static string IvtcChain(FieldParity parity)
    {
        string p = ParityStr(parity);
        return $"fieldmatch=order={p}:combmatch=full," +
               $"bwdif=mode=send_frame:parity={p}:deint=interlaced," +
               "decimate";
    }

    /// <summary>
    /// Motion-adaptive deinterlace at native frame rate (one output frame per
    /// input frame).  bwdif is the modern successor to yadif recommended by the
    /// guide §7.2.3.3.
    /// </summary>
    public static string DeinterlaceChain(FieldParity parity) =>
        $"bwdif=mode=send_frame:parity={ParityStr(parity)}";

    internal static string ParityStr(FieldParity parity) => parity switch
    {
        FieldParity.Tff => "tff",
        FieldParity.Bff => "bff",
        _ => "auto"
    };
}
