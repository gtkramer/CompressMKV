using System.Xml;
using System.Xml.Serialization;

namespace MkvHelper;

/// <summary>
/// XML round-trip for the Matroska chapter document format.  Both methods
/// are pure in-memory operations — fast enough that exposing them as async
/// would just add ceremony.
/// </summary>
public static class ChapterSerializer
{
    private static readonly XmlSerializer s_serializer = new(typeof(Chapters));

    public static Chapters DeserializeXmlToChapters(string xml)
    {
        using var reader = new StringReader(xml);
        return s_serializer.Deserialize(reader) as Chapters
            ?? throw new InvalidOperationException("Chapters XML deserialised to null.");
    }

    public static string SerializeChaptersToXml(Chapters chapters)
    {
        // Strip the default xsi/xsd namespaces — mkvmerge accepts them, but
        // round-tripping XML that didn't have them shouldn't introduce them.
        var namespaces = new XmlSerializerNamespaces();
        namespaces.Add(string.Empty, string.Empty);

        // Write through a MemoryStream so the XML declaration uses UTF-8
        // (the encoding StreamWriter writes the bytes in).  Writing to a
        // StringWriter would emit utf-16 in the declaration, which mkvmerge
        // tolerates but is wrong for the bytes we'll actually save.
        using var memory = new MemoryStream();
        using (var streamWriter = new StreamWriter(memory, leaveOpen: true))
        using (var xmlWriter = new XmlTextWriter(streamWriter) { Formatting = Formatting.Indented })
        {
            xmlWriter.WriteStartDocument();
            xmlWriter.WriteDocType("Chapters", null, "matroskachapters.dtd", null);
            s_serializer.Serialize(xmlWriter, chapters, namespaces);
        }

        memory.Position = 0;
        using var reader = new StreamReader(memory);
        return reader.ReadToEnd();
    }
}
