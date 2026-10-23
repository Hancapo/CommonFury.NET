using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace CommonFury.Xml;

/// <summary>Bounded XML document loading without DTDs or external resolution, and canonical UTF-8 writing.</summary>
public static class XmlDocumentCodec
{
    /// <summary>Default input and entity-expansion budget, in characters.</summary>
    public const int DefaultMaximumCharacters = 64 * 1024 * 1024;

    /// <summary>Parses a copy of the input, preserving whitespace. DTDs are prohibited and nothing external is resolved.
    /// Malformed XML or input over the budget throws <see cref="InvalidDataException"/>.
    /// <paramref name="predeclaredNamespaces"/> maps prefixes to URIs for the whole document, for input that uses a prefix
    /// without declaring it; declarations in the document override them.</summary>
    public static XDocument Read(ReadOnlySpan<byte> data, int maximumCharacters = DefaultMaximumCharacters,
        IReadOnlyDictionary<string, string>? predeclaredNamespaces = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCharacters);
        if (data.Length > maximumCharacters)
            throw new InvalidDataException("XML input exceeds the configured byte limit.");
        using var stream = new MemoryStream(data.ToArray(), writable: false);
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = maximumCharacters,
            MaxCharactersFromEntities = maximumCharacters
        };
        XmlParserContext? context = null;
        if (predeclaredNamespaces is not null)
        {
            var namespaces = new XmlNamespaceManager(new NameTable());
            foreach (var (prefix, uri) in predeclaredNamespaces)
                namespaces.AddNamespace(prefix, uri);
            context = new XmlParserContext(null, namespaces, null, XmlSpace.None);
        }
        try
        {
            using XmlReader reader = XmlReader.Create(stream, settings, context);
            return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException exception) { throw new InvalidDataException("Malformed or unsupported XML document.", exception); }
    }

    /// <summary>Writes the document as indented UTF-8 without a byte-order mark, using LF line endings and entitized newlines in values.</summary>
    public static byte[] Write(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        using var stream = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false, true),
            Indent = true,
            NewLineChars = "\n",
            NewLineHandling = NewLineHandling.Entitize
        };
        using (XmlWriter writer = XmlWriter.Create(stream, settings))
            document.Save(writer);
        return stream.ToArray();
    }
}
