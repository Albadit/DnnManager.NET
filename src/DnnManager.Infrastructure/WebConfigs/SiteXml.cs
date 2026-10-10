using System.Xml;
using System.Xml.Linq;

namespace DnnManager.Infrastructure.WebConfigs;

/// <summary>
/// Reading an XML file of a site - its web.config (and a file it names with <c>configSource</c>), DNN's install template.
/// The site's app pool can change them, and DNN Manager reads them with administrator rights: no DTD (an entity that
/// expands to gigabytes, "billion laughs") and nothing fetched from elsewhere (no resolver). <c>XDocument.Load</c> on its
/// own parses a DTD.
/// </summary>
public static class SiteXml
{
    private static XmlReaderSettings Settings() => new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        // XDocument.Load keeps nothing apart without LoadOptions; these keep what the file has.
        IgnoreWhitespace = false,
        IgnoreComments = false
    };

    /// <summary>The file at <paramref name="path"/> - throws <see cref="XmlException"/> when it has a DTD or isn't XML.</summary>
    public static XDocument Load(string path, LoadOptions options = LoadOptions.None)
    {
        using var reader = XmlReader.Create(path, Settings());
        return XDocument.Load(reader, options);
    }

    /// <summary>The XML in <paramref name="stream"/>, read the same way.</summary>
    public static XDocument Load(Stream stream, LoadOptions options = LoadOptions.None)
    {
        using var reader = XmlReader.Create(stream, Settings());
        return XDocument.Load(reader, options);
    }
}
