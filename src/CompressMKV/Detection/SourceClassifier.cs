namespace CompressMkv;

public static class SourceClassifier
{
    public static bool IsHdr(FfprobeStream v)
    {
        if (v.ColorTransfer == null) return false;
        return v.ColorTransfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) ||
               v.ColorTransfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
    }
}
