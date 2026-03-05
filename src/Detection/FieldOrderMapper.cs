namespace CompressMkv;

/// <summary>
/// Maps ffprobe field_order values to <see cref="FieldParity"/>.
/// </summary>
public static class FieldOrderMapper
{
    // ffprobe field_order values vary. Common ones:
    // progressive, tt, bb, tb, bt, tff, bff, unknown
    // We'll map:
    //  - tt / tff => Tff
    //  - bb / bff => Bff
    // Others => Auto (unknown/mixed)
    public static FieldParity MapToParity(string fieldOrderLower)
    {
        if (string.IsNullOrWhiteSpace(fieldOrderLower)) return FieldParity.Auto;

        if (fieldOrderLower.Contains("tt") || fieldOrderLower.Contains("tff"))
            return FieldParity.Tff;

        if (fieldOrderLower.Contains("bb") || fieldOrderLower.Contains("bff"))
            return FieldParity.Bff;

        // progressive/unknown/tb/bt -> not a clean parity commitment
        return FieldParity.Auto;
    }
}
