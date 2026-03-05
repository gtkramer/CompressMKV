namespace CompressMkv;

public static class SourceClassifier
{
    public static SourceType Classify(FfprobeStream v)
    {
        if (string.Equals(v.CodecName, "mpeg2video", StringComparison.OrdinalIgnoreCase) &&
            v.Height is not null && v.Height <= 576)
            return SourceType.DvdLike;

        return SourceType.BluRayLike;
    }

    public static bool IsHdr(FfprobeStream v)
    {
        if (v.ColorTransfer == null) return false;
        return v.ColorTransfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase) ||
               v.ColorTransfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase);
    }
}
