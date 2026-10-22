using System.Xml.Linq;

namespace CommonFury.Xml;

/// <summary>Resolves RAGE <c>platform="…"</c> selectors in data XML for one target platform.</summary>
public static class XmlPlatformSelection
{
    /// <summary>Platform names that select 64-bit PC content.</summary>
    public static IReadOnlySet<string> Pc { get; } =
        new HashSet<string>(["pc", "pc_only", "64bit", "x64", "win64", "win64pc"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns a copy for the selected platform: elements whose selector does not apply are removed and the remaining
    /// selectors are dropped. Selectors support alternatives separated by |, optional ! negation and case-insensitive names;
    /// <paramref name="platforms"/> lists the names that apply.</summary>
    public static byte[] Select(ReadOnlySpan<byte> data, IReadOnlySet<string> platforms)
    {
        ArgumentNullException.ThrowIfNull(platforms);
        XDocument document = XmlDocumentCodec.Read(data);
        XElement root = document.Root ?? throw new InvalidDataException("Missing XML root.");
        foreach (XElement element in root.DescendantsAndSelf().ToArray())
        {
            XAttribute? platform = element.Attribute("platform");
            if (platform is null)
                continue;
            if (!Applies(platform.Value, platforms))
            {
                if (element == root)
                    throw new InvalidDataException("XML root does not apply to the selected platform.");
                element.Remove();
            }
            else
                platform.Remove();
        }
        return XmlDocumentCodec.Write(document);
    }

    /// <summary>True when the selector applies to any of <paramref name="platforms"/>; a blank selector always applies.</summary>
    public static bool Applies(string expression, IReadOnlySet<string> platforms)
    {
        ArgumentNullException.ThrowIfNull(expression);
        ArgumentNullException.ThrowIfNull(platforms);
        if (string.IsNullOrWhiteSpace(expression))
            return true;
        foreach (string part in expression.Split('|'))
        {
            string token = part.Trim().ToLowerInvariant();
            bool negate = token.StartsWith('!');
            string platform = negate ? token[1..] : token;
            bool current = platforms.Contains(platform) || platforms.Any(name => name.Equals(platform, StringComparison.OrdinalIgnoreCase));
            if (negate ? !current : current)
                return true;
        }
        return false;
    }
}
