using System.Xml.Serialization;

namespace MkvHelper;

/// <summary>
/// Root element of a Matroska chapters XML document, mirroring the schema
/// produced by <c>mkvextract chapters</c> and consumed by
/// <c>mkvmerge --chapters</c>.
/// </summary>
[XmlRoot("Chapters")]
public sealed class Chapters
{
    [XmlElement("EditionEntry")]
    public EditionEntry EditionEntry { get; set; } = new();
}
