using System.Xml.Serialization;

namespace MkvHelper;

public sealed class EditionEntry
{
    [XmlElement("ChapterAtom")]
    public List<ChapterAtom> ChapterAtoms { get; set; } = [];
}
