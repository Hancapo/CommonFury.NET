using System.Globalization;
using System.Xml.Linq;

namespace CommonFury.Xml;

/// <summary>Diagnostic codes reported by <see cref="XmlElementReader"/>.</summary>
public static class XmlDiagnosticCodes
{
    /// <summary>An element or attribute was not read by the model; it survives only in the source document.</summary>
    public const string ContentUnmodeled = "xml.content.unmodeled";
}

/// <summary>Strict XML reading of scalars, containers and objects that reports any content left unread.</summary>
/// <remarks>Each read marks the child as consumed. <see cref="Finish"/> reports every element or attribute that was not consumed
/// as an <see cref="XmlDiagnosticCodes.ContentUnmodeled"/> warning. Duplicate fields and unparsable scalars throw
/// <see cref="InvalidDataException"/>. The reader borrows the element and the diagnostic list; it is not thread safe.</remarks>
public sealed class XmlElementReader
{
    private readonly XElement sourceElement;
    private readonly List<Diagnostic> issues;
    private readonly string path;
    private readonly HashSet<XElement> consumed = [];
    private readonly HashSet<XAttribute> attributes = [];

    /// <summary>Reports diagnostics into <paramref name="issues"/> with paths under <paramref name="path"/>.</summary>
    public XmlElementReader(XElement element, List<Diagnostic> issues, string path)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(issues);
        ArgumentNullException.ThrowIfNull(path);
        (sourceElement, this.issues, this.path) = (element, issues, path);
    }

    public XElement Element => sourceElement;

    /// <summary>Returns the only child with the name, or null. Duplicates throw unless
    /// <paramref name="allowTrailingEmptyContainers"/> permits later empty copies.</summary>
    public XElement? Child(string name, bool allowTrailingEmptyContainers = false)
    {
        XElement[] found = sourceElement.Elements(name).ToArray();
        if (found.Length > 1 && (!allowTrailingEmptyContainers || found.Skip(1).Any(child =>
                child.HasElements || child.HasAttributes || !string.IsNullOrWhiteSpace(child.Value))))
            throw new InvalidDataException($"Duplicate XML field {path}/{name}.");
        if (found.Length == 0)
            return null;
        foreach (XElement child in found)
            consumed.Add(child);
        return found[0];
    }

    /// <summary>Returns the attribute value, or an empty string when absent.</summary>
    public string Attribute(string name)
    {
        XAttribute? value = sourceElement.Attribute(name);
        if (value is null)
            return "";
        attributes.Add(value);
        return value.Value;
    }

    /// <summary>Returns the trimmed text of a scalar child, or the fallback when absent.</summary>
    public string Text(string name, string fallback = "")
    {
        XElement? child = Child(name);
        if (child is null)
            return fallback;
        CheckScalar(child, false);
        return child.Value.Trim();
    }

    /// <summary>Reads a signed 32-bit child from its value attribute or text, or returns the fallback when absent.</summary>
    public int Integer(string name, int fallback = 0)
    {
        string? text = Value(name);
        if (text is null)
            return fallback;
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int result))
            throw new InvalidDataException($"Invalid signed 32-bit integer at {path}/{name}.");
        return result;
    }

    /// <summary>Reads a Boolean child spelled true, false, 1 or 0, or returns null when absent.</summary>
    public bool? Boolean(string name)
    {
        string? text = Value(name);
        return text switch
        {
            null => null,
            "true" or "1" => true,
            "false" or "0" => false,
            _ => throw new InvalidDataException($"Invalid XML boolean at {path}/{name}.")
        };
    }

    /// <summary>Reads a 32-bit float child from its value attribute or text, or returns the fallback when absent.</summary>
    public float Single(string name, float fallback = 0)
    {
        string? text = Value(name);
        if (text is null)
            return fallback;
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float result))
            throw new InvalidDataException($"Invalid 32-bit float at {path}/{name}.");
        return result;
    }

    private string? Value(string name)
    {
        XElement? child = Child(name);
        if (child is null)
            return null;
        CheckScalar(child, true);
        return (child.Attribute("value")?.Value ?? child.Value).Trim();
    }

    private void CheckScalar(XElement child, bool valueAttribute)
    {
        if (child.HasElements || child.Attributes().Any(a => !valueAttribute || a.Name != "value"))
            Unknown(child.Name.LocalName);
        if (valueAttribute && child.Attribute("value") is not null && !string.IsNullOrWhiteSpace(child.Value))
            throw new InvalidDataException($"XML value attribute and text conflict at {path}/{child.Name}.");
    }

    /// <summary>Reads each <c>Item</c> (case-insensitive) of a container child and finishes every item reader. An absent container reads as empty.</summary>
    public List<T> Items<T>(string name, Func<XmlElementReader, T> read, bool allowTrailingEmptyContainers = false)
    {
        ArgumentNullException.ThrowIfNull(read);
        XElement? container = Child(name, allowTrailingEmptyContainers);
        if (container is null)
            return [];
        var reader = new XmlElementReader(container, issues, $"{path}/{name}");
        var result = new List<T>();
        foreach (XElement child in container.Elements().Where(e => e.Name.Namespace == XNamespace.None && e.Name.LocalName.Equals("Item", StringComparison.OrdinalIgnoreCase)))
        {
            reader.consumed.Add(child);
            var item = new XmlElementReader(child, issues, $"{path}/{name}/Item[{result.Count}]");
            result.Add(read(item));
            item.Finish();
        }
        reader.Finish();
        return result;
    }

    /// <summary>Reads the trimmed text of each <c>Item</c> of a container child.</summary>
    public List<string> Strings(string name) => Items(name, item =>
    {
        if (item.sourceElement.HasElements)
            item.Unknown("Item");
        return item.sourceElement.Value.Trim();
    });

    /// <summary>Reads an optional child with its own reader and finishes it; null when absent.</summary>
    public T? Object<T>(string name, Func<XmlElementReader, T> read) where T : class
    {
        ArgumentNullException.ThrowIfNull(read);
        XElement? child = Child(name);
        if (child is null)
            return null;
        var reader = new XmlElementReader(child, issues, $"{path}/{name}");
        T result = read(reader);
        reader.Finish();
        return result;
    }

    /// <summary>Reports every child element and attribute that no read consumed, as warnings.</summary>
    public void Finish()
    {
        foreach (XElement child in sourceElement.Elements())
            if (!consumed.Contains(child))
                Unknown(child.Name.ToString());
        foreach (XAttribute attribute in sourceElement.Attributes())
            if (!attributes.Contains(attribute))
                Unknown("@" + attribute.Name);
    }

    private void Unknown(string name) => issues.Add(new(XmlDiagnosticCodes.ContentUnmodeled,
        "Unmodeled XML is retained in the original document; editing this document is guarded.", $"{path}/{name}", DiagnosticSeverity.Warning));
}

/// <summary>Builds elements in the conventions <see cref="XmlElementReader"/> reads.</summary>
public static class XmlElements
{
    /// <summary>Element whose text is the value; empty for null.</summary>
    public static XElement Text(string name, object? value) => new(name, value ?? "");
    /// <summary>Element with a <c>value</c> attribute (Booleans as true/false); null for a null value.</summary>
    public static XElement? Value(string name, object? value) => value is null ? null :
        new XElement(name, new XAttribute("value", value is bool boolean ? (boolean ? "true" : "false") : value));
    /// <summary>Container of text <c>Item</c> elements.</summary>
    public static XElement Strings(string name, IEnumerable<string> values) => new(name, values.Select(value => Text("Item", value)));
    public static XElement Items<T>(string name, IEnumerable<T> values, Func<T, XElement> write) => new(name, values.Select(write));
}
