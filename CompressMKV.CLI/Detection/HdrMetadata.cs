namespace CompressMkv;

/// <summary>
/// HDR mastering / content-light-level metadata extracted from the source.
/// Used to set <c>npl</c> (nominal peak luminance) on the VMAF tonemap chain
/// so highlights don't get crushed by an artificially-low default.
/// </summary>
public sealed class HdrMetadata
{
    /// <summary>
    /// MaxCLL — the highest per-pixel light level observed in the content,
    /// in nits.  This is the most accurate signal for tonemap npl: it tells
    /// us the brightest pixel actually present, regardless of what the
    /// mastering display could theoretically reproduce.
    /// </summary>
    public int? MaxCll { get; set; }

    /// <summary>MaxFALL — the maximum frame-average light level in nits.
    /// Diagnostic only; not used for npl.</summary>
    public int? MaxFall { get; set; }

    /// <summary>
    /// Mastering display max luminance in nits — the cap the colorist saw
    /// on their reference display.  Falls back to this when MaxCLL is absent.
    /// </summary>
    public int? MasteringDisplayMaxLuminance { get; set; }

    /// <summary>
    /// Resolves the nominal peak luminance to use for the VMAF tonemap chain.
    /// Preference order:
    ///   1. MaxCLL (the actual peak in the content — most precise).
    ///   2. Mastering display max luminance (the colorist's working peak).
    ///   3. 1000 nits (HDR10 conventional reference peak).
    ///
    /// Never returns the historical hardcoded 100 — that value is correct only
    /// for SDR-mastered content and crushes any real HDR highlights when used
    /// for tonemap input.
    /// </summary>
    public int ResolveNpl()
    {
        if (MaxCll is { } cll && cll > 0) return cll;
        if (MasteringDisplayMaxLuminance is { } mast && mast > 0) return mast;
        return 1000;
    }
}
