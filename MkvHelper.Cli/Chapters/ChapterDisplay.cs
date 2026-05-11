using System.Xml.Serialization;

namespace MkvHelper;

public sealed class ChapterDisplay
{
    [XmlElement("ChapterString")]
    public string ChapterString { get; set; } = "";

    [XmlElement("ChapterLanguage")]
    public string ChapterLanguage { get; set; } = "";
}
