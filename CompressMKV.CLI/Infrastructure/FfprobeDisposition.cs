using System.Text.Json.Serialization;

namespace CompressMkv;

/// <summary>
/// Stream-level disposition flags from ffprobe.  Each flag is 0 (unset) or 1.
/// We only care about <c>attached_pic</c> for distinguishing real video
/// streams from cover-art images embedded in audio files; other dispositions
/// (default, dub, original, comment, ...) are unused but accepted.
/// </summary>
public sealed class FfprobeDisposition
{
    /// <summary>
    /// 1 when this stream is an "attached picture" — i.e. cover art embedded
    /// in an audio file (mp3, m4a, flac with embedded JPEG/PNG).  Such streams
    /// have <c>codec_type = "video"</c> but represent a still image, not video
    /// content.  Used to filter cover art out of the video discovery pass.
    /// </summary>
    [JsonPropertyName("attached_pic")] public int AttachedPic { get; set; }
}
