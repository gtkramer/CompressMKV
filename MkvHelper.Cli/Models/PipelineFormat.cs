namespace MkvHelper;

/// <summary>
/// Single source of truth for bit-depth-derived ffmpeg format choices.
/// Built once per source from <see cref="FfprobeStream.GetBitDepth"/> and
/// flows through every ffmpeg invocation so the pipeline operates at the
/// source's native depth end-to-end:
///
///   8-bit source  →  encode pix_fmt = nv12     (8-bit, semi-planar, NVENC-native)
///                 →  hwaccel output = nv12     (NVDEC outputs 8-bit on download)
///                 →  VMAF compare   = yuv420p  (matched 8-bit comparison)
///
///   10-bit source →  encode pix_fmt = p010le      (10-bit, semi-planar, NVENC-native)
///                 →  hwaccel output = p010le      (NVDEC outputs 10-bit on download)
///                 →  VMAF compare   = yuv420p10le (matched 10-bit comparison)
///
/// Why this matters:
///   - Encoding 8-bit content into a 10-bit container (the previous default)
///     bloats files ~10-15% with no quality gain — the encoder has no real
///     LSB content to put in the extra bits.
///   - Down-converting 10-bit decode output to 8-bit nv12 during NVDEC
///     loses precision before filters/idet ever see the frame.
///   - VMAF comparing zero-padded 8-bit against genuine 10-bit biases the
///     score low (the comparison detects fake LSB differences).
///
/// All three are fixed by routing every ffmpeg call through the matching
/// PipelineFormat for the source.
/// </summary>
public sealed class PipelineFormat
{
    public int BitDepth { get; init; }

    /// <summary>
    /// pix_fmt for NVENC AV1 encode output (the `-pix_fmt` arg).  Semi-planar
    /// formats (nv12 / p010le) match NVENC's native input layout — yuv420p
    /// planar variants work too but require an extra GPU shader conversion.
    /// </summary>
    public string EncodePixFmt => BitDepth >= 10 ? "p010le" : "nv12";

    /// <summary>
    /// pix_fmt for NVDEC hwaccel output (the `-hwaccel_output_format` arg).
    /// Determines the bit depth at which CPU filters (idet, fieldmatch, bwdif)
    /// see the decoded frames.
    /// </summary>
    public string HwaccelOutputFormat => BitDepth >= 10 ? "p010le" : "nv12";

    /// <summary>
    /// Format both reference and encoded streams are converted to before
    /// libvmaf compares them (the `format=` filter on each VMAF input).
    /// Matched-depth comparison avoids zero-padding LSBs on one side.
    /// </summary>
    public string VmafCompareFormat => BitDepth >= 10 ? "yuv420p10le" : "yuv420p";

    public static PipelineFormat FromStream(FfprobeStream s) =>
        new() { BitDepth = s.GetBitDepth() };

    public override string ToString() => $"{BitDepth}-bit (encode={EncodePixFmt}, vmaf={VmafCompareFormat})";
}
