namespace CompressMkv;

/// <summary>
/// Five content categories per MPlayer guide §7.2.
/// Each maps to a specific restoration strategy.
/// </summary>
public enum ContentType
{
    /// <summary>24p stored as 24p. §7.2.3.1: no filtering needed.</summary>
    Progressive,

    /// <summary>24p hard-telecined to 30fps (3:2 pulldown baked). §7.2.3.2: inverse-telecine.</summary>
    Telecined,

    /// <summary>Native 60i content (30fps interlaced). §7.2.3.3: deinterlace.</summary>
    Interlaced,

    /// <summary>Mix of progressive + telecined sections. §7.2.3.4: fieldmatch+decimate handles both.</summary>
    MixedProgressiveTelecine,

    /// <summary>Mix of progressive + interlaced sections. §7.2.3.5: deinterlace all (compromise).</summary>
    MixedProgressiveInterlaced
}
