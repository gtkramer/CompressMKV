namespace CompressMkv;

/// <summary>
/// Per-frame interlace classification from the idet filter's multi-frame detector.
/// </summary>
public enum FrameFlag : byte
{
    /// <summary>Frame detected as progressive (no combing artifacts).</summary>
    Progressive = 0,

    /// <summary>Frame detected as interlaced (TFF or BFF combing).</summary>
    Interlaced = 1,

    /// <summary>Frame classification undetermined by idet.</summary>
    Undetermined = 2
}
