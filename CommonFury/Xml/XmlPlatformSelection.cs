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
        XAttribute? selector = root.Attribute("platform");
        if (selector is not null)
        {
            if (!Applies(selector.Value, platforms))
                throw new InvalidDataException("XML root does not apply to the selected platform.");
            selector.Remove();
        }
        // Each container is rebuilt once from its kept nodes, since removing siblings one by one is quadratic. Removed
        // subtrees are not visited, and an explicit stack keeps deep documents off the call stack.
        var containers = new Stack<XElement>();
        var kept = new List<XNode>();
        containers.Push(root);
        while (containers.TryPop(out XElement? container))
        {
            bool dropped = false;
            for (XNode? node = container.FirstNode; node is not null; node = node.NextNode)
            {
                if (node is XElement element)
                {
                    XAttribute? platform = element.Attribute("platform");
                    if (platform is not null)
                    {
                        if (!Applies(platform.Value, platforms))
                        { dropped = true; continue; }
                        platform.Remove();
                    }
                    containers.Push(element);
                }
                kept.Add(node);
            }
            if (dropped)
                container.ReplaceNodes(kept);
            kept.Clear();
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
