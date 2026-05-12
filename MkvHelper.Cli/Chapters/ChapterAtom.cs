using System.Xml.Serialization;

namespace MkvHelper;

/// <summary>
/// One Matroska chapter entry.  Timestamps round-trip as
/// <c>HH:MM:SS.fffffffff</c> strings; trailing zeros are normalised on the
/// way in so equality comparisons aren't tripped by formatting noise from
/// different mkvextract versions.
/// </summary>
public sealed class ChapterAtom
{
    [XmlElement("ChapterUID")]
    public long ChapterUID { get; set; }

    [XmlElement("ChapterTimeStart")]
    public string ChapterTimeStart
    {
        get;
        set => field = ChapterTimecode.Normalize(value);
    } = "";

    [XmlElement("ChapterTimeEnd")]
    public string ChapterTimeEnd
    {
        get;
        set => field = ChapterTimecode.Normalize(value);
    } = "";

    [XmlElement("ChapterDisplay")]
    public ChapterDisplay ChapterDisplay { get; set; } = new();

    public double GetChapterTimeStartSeconds() => ChapterTimecode.ToSeconds(ChapterTimeStart);
    public double GetChapterTimeEndSeconds() => ChapterTimecode.ToSeconds(ChapterTimeEnd);
    public double GetDurationSeconds() => GetChapterTimeEndSeconds() - GetChapterTimeStartSeconds();
}
