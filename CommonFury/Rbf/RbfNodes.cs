using System.Globalization;
using System.Numerics;
using System.Text;

namespace CommonFury.Rbf;

/// <summary>Source name-table entry: ID, text and the record type of its first use.</summary>
public sealed record RbfDescriptor(int Index, string Name, RbfRecordType Type);

/// <summary>Record in an editable RBF graph. Writing requires a finite tree.</summary>
public abstract class RbfNode
{
    private protected RbfNode() { }
}

/// <summary>Structure or value with a non-null name.</summary>
public abstract class RbfNamedNode : RbfNode
{
    private string name;
    /// <summary>Stored as given, without normalization.</summary>
    public string Name
    {
        get => this.name;
        set { ArgumentNullException.ThrowIfNull(value); this.name = value; }
    }
    private protected RbfNamedNode(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        this.name = name;
    }
}

/// <summary>Owns its editable record lists. Attributes contain scalar records and optional comments.</summary>
/// <remarks>
/// Comments do not contribute to the serialized attribute count. Reusing a node in several places
/// serializes each occurrence; cycles and unsupported attribute records are diagnosed by Validate.
/// No parent links or derived indexes are retained.
/// </remarks>
public sealed class RbfStructure(string name) : RbfNamedNode(name)
{
    /// <summary>Scalar attribute records and comments, in file order.</summary>
    public List<RbfNode> Attributes { get; } = [];
    /// <summary>Child structures, values, data blocks and comments, in file order.</summary>
    public List<RbfNode> Children { get; } = [];

    public ushort Reserved1 { get; set; }
    public ushort Reserved2 { get; set; }
    /// <summary>Both reserved values as the little-endian 32-bit word they occupy on disk, <see cref="Reserved1"/> in the low half.</summary>
    public uint Reserved
    {
        get => Reserved1 | (uint)Reserved2 << 16;
        set => (Reserved1, Reserved2) = ((ushort)value, (ushort)(value >> 16));
    }

    /// <summary>Direct child structures, optionally filtered by exact name.</summary>
    public IEnumerable<RbfStructure> Structures(string? name = null) =>
        Children.OfType<RbfStructure>().Where(node => name is null || node.Name == name);

    /// <summary>Direct child values (not attributes), optionally filtered by exact name.</summary>
    public IEnumerable<RbfValue> Values(string? name = null) =>
        Children.OfType<RbfValue>().Where(node => name is null || node.Name == name);

    /// <summary>Reads the first text child with an exact name, stored either as a value or as a structure whose first child is
    /// a data block or value. Data blocks are decoded as ASCII; NULs and surrounding whitespace are removed. Returns an empty
    /// string when no such child exists.</summary>
    public string StringField(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        foreach (RbfNode child in Children)
        {
            string? text = child switch
            {
                RbfValue value when value.Name == name => value.FormatInvariant(),
                RbfStructure structure when structure.Name == name && structure.Children.Count != 0 => structure.Children[0] switch
                {
                    RbfBytes bytes => Encoding.ASCII.GetString(bytes.Data),
                    RbfValue value => value.FormatInvariant(),
                    _ => null
                },
                _ => null
            };
            if (text is not null)
                return text.Replace("\0", "", StringComparison.Ordinal).Trim();
        }
        return "";
    }
}

/// <summary>Named scalar record.</summary>
public abstract class RbfValue : RbfNamedNode
{
    private protected RbfValue(string name) : base(name) { }
    internal abstract string FormatInvariant();
}

/// <summary>32-bit integer record; the bit pattern is preserved.</summary>
public sealed class RbfInt32Value(string name, int value) : RbfValue(name)
{
    /// <summary>The stored bits as a signed value; reinterpret as <see cref="uint"/> where a format treats them as unsigned.</summary>
    public int Value { get; set; } = value;
    internal override string FormatInvariant() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Boolean record; the value is encoded in the record type.</summary>
public sealed class RbfBooleanValue(string name, bool value) : RbfValue(name)
{
    public bool Value { get; set; } = value;
    internal override string FormatInvariant() => Value ? "True" : "False";
}

/// <summary>Single-precision record; the bit pattern is preserved.</summary>
public sealed class RbfFloatValue(string name, float value) : RbfValue(name)
{
    public float Value { get; set; } = value;
    internal override string FormatInvariant() => Value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Three stored single-precision values in X, Y, Z order; no unit or coordinate conversion is applied.</summary>
public sealed class RbfVector3Value(string name, Vector3 value) : RbfValue(name)
{
    public Vector3 Value { get; set; } = value;
    internal override string FormatInvariant() => Value.ToString(null, CultureInfo.InvariantCulture);
}

/// <summary>ASCII string record; written as an attribute.</summary>
public sealed class RbfStringValue : RbfValue
{
    private string value;
    public string Value
    {
        get => this.value;
        set { ArgumentNullException.ThrowIfNull(value); this.value = value; }
    }
    public RbfStringValue(string name, string value) : base(name)
    {
        ArgumentNullException.ThrowIfNull(value);
        this.value = value;
    }
    internal override string FormatInvariant() => Value;
}

/// <summary>Opaque payload record. Owns a copy of its input; Data is fixed-length and caller-editable.</summary>
public abstract class RbfRawNode : RbfNode
{
    public byte[] Data { get; }
    private protected RbfRawNode(ReadOnlySpan<byte> data) => Data = data.ToArray();
}

/// <summary>Opaque data block; payload bytes are never reinterpreted.</summary>
public sealed class RbfBytes(ReadOnlySpan<byte> data) : RbfRawNode(data);

/// <summary>Opaque comment record; may contain bytes outside ASCII.</summary>
public sealed class RbfComment(ReadOnlySpan<byte> data) : RbfRawNode(data);
