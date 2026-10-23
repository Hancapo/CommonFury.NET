using System.Numerics;
using System.Text;
using CommonFury.Hashing;

namespace CommonFury.Pso;

/// <summary>Decoded PSO field value.</summary>
public abstract record PsoValue;
/// <summary>Absent value, such as a null pointer.</summary>
public sealed record PsoNull : PsoValue
{
    public static PsoNull Instance { get; } = new();
    private PsoNull() { }
}
/// <summary>Decoded boolean.</summary>
public sealed record PsoBoolean(bool Value) : PsoValue;
/// <summary>Any decoded integer, widened to 64 bits.</summary>
public sealed record PsoInteger(long Value) : PsoValue;
/// <summary>Decoded single-precision float.</summary>
public sealed record PsoFloat(float Value) : PsoValue;
/// <summary>Half-precision value kept as its exact 16-bit pattern.</summary>
public sealed record PsoHalf(ushort Bits) : PsoValue
{
    public Half Value => BitConverter.UInt16BitsToHalf(Bits);
}
/// <summary>Decoded two-component vector.</summary>
public sealed record PsoVector2(Vector2 Value) : PsoValue;
/// <summary>Decoded three-component vector.</summary>
public sealed record PsoVector3(Vector3 Value) : PsoValue;
/// <summary>Decoded four-component vector.</summary>
public sealed record PsoVector4(Vector4 Value) : PsoValue;
/// <summary>Decoded hash, with its spelling when a name was resolved.</summary>
public sealed record PsoHash(Hash32 Value) : PsoValue;

/// <summary>Text bytes before the first NUL. No implicit lossy ASCII decoding; equality compares bytes.</summary>
public sealed record PsoText : PsoValue
{
    private readonly byte[] bytes;
    /// <summary>Text bytes without the terminator.</summary>
    public ReadOnlySpan<byte> Bytes => this.bytes;
    /// <summary>Copies the text bytes, which exclude the terminator.</summary>
    /// <exception cref="ArgumentException"><paramref name="bytes"/> contains a NUL.</exception>
    public PsoText(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
            throw new ArgumentException("PSO text cannot contain a NUL.", nameof(bytes));
        this.bytes = bytes.ToArray();
    }
    public string Decode(Encoding encoding) => encoding.GetString(this.bytes);
    public bool Equals(PsoText? other) => other is not null && Bytes.SequenceEqual(other.Bytes);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.AddBytes(this.bytes);
        return hash.ToHashCode();
    }
}

/// <summary>Read-only array of values in source order, with element-wise equality.</summary>
public sealed record PsoArray : PsoValue
{
    public IReadOnlyList<PsoValue> Values { get; }
    internal PsoArray(PsoValue[] values) => Values = Array.AsReadOnly(values);
    /// <summary>Copies the value list but shares the values and nodes it references.</summary>
    /// <exception cref="ArgumentException">A value is null.</exception>
    public static PsoArray Create(IEnumerable<PsoValue> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        PsoValue[] items = values.ToArray();
        if (items.Any(item => item is null))
            throw new ArgumentException("PSO array values cannot be null.", nameof(values));
        return new(items);
    }
    public bool Equals(PsoArray? other) => other is not null && Values.SequenceEqual(other.Values);
    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (PsoValue value in Values)
            hash.Add(value);
        return hash.ToHashCode();
    }
}

/// <summary>Value the generic reader cannot interpret. Original bytes remain in PsoDocument.File.</summary>
public sealed record PsoUnsupported(PsoEntry Entry, string Reason) : PsoValue;
/// <summary>Field whose bytes could not be decoded, isolated by <see cref="PsoReadOptions.ContinueOnFieldErrors"/>. Original bytes remain in PsoDocument.File.</summary>
public sealed record PsoMalformed(PsoEntry Entry, string Reason) : PsoValue;
/// <summary>Pointer whose target cannot be resolved, kept with its expected type hash.</summary>
public sealed record PsoUnresolvedPointer(PsoPointer Pointer, uint TypeHash) : PsoValue;
/// <summary>Reference to a decoded node; shared nodes keep their identity.</summary>
public sealed record PsoNodeValue(PsoNode Node) : PsoValue;
/// <summary>Ordered binary-map entries. Each entry is a key/item node in PairTypeHash's schema; node identity is retained.</summary>
public sealed record PsoMap : PsoValue
{
    /// <summary>Schema of the key/item pair nodes; zero for an empty map without pair storage.</summary>
    public uint PairTypeHash { get; }
    /// <summary>Stored ordering declaration; entries are never reordered.</summary>
    public bool Sorted { get; }
    public IReadOnlyList<PsoNode> Entries { get; }
    /// <summary>Copies the entry list while retaining its nodes.</summary>
    /// <exception cref="ArgumentException">An entry has another schema, or there are more than 65535 entries.</exception>
    public PsoMap(uint pairTypeHash, IEnumerable<PsoNode> entries, bool sorted = false)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var items = entries.ToArray();
        if (items.Any(item => item is null || item.TypeHash != pairTypeHash) || items.Length > ushort.MaxValue)
            throw new ArgumentException("Map entries must share the pair schema and fit a 16-bit count.", nameof(entries));
        PairTypeHash = pairTypeHash;
        Sorted = sorted;
        Entries = Array.AsReadOnly(items);
    }
}
/// <summary>Schema field and its decoded value.</summary>
public sealed record PsoField(PsoEntry Entry, PsoValue Value);

/// <summary>Decoded or authored node, identified by source pointer and type hash. Pointer cycles retain node identity. Fields
/// are set once and then read-only.</summary>
public sealed class PsoNode
{
    public uint TypeHash { get; }
    /// <summary>Source location; default for authored nodes.</summary>
    public PsoPointer Pointer { get; }
    /// <summary>Fields in schema order, excluding anonymous descriptors; empty until the node is complete.</summary>
    public IReadOnlyList<PsoField> Fields { get; private set; } = Array.Empty<PsoField>();
    /// <summary>False only for a node from <see cref="CreateIncomplete"/> before <see cref="Complete"/>;
    /// <see cref="PsoFile.FromGraph"/> rejects incomplete nodes.</summary>
    public bool IsComplete { get; private set; }
    internal PsoNode(uint typeHash, PsoPointer pointer) { TypeHash = typeHash; Pointer = pointer; }
    /// <summary>Creates an authored node; copies the field list but shares the values and nodes it references.</summary>
    public static PsoNode Create(uint typeHash, IEnumerable<PsoField> fields)
    {
        PsoNode result = CreateIncomplete(typeHash);
        result.Complete(fields);
        return result;
    }
    /// <summary>Creates an authored node without fields, so values can reference it before <see cref="Complete"/> supplies
    /// them, as cycles and nodes built out of order require.</summary>
    public static PsoNode CreateIncomplete(uint typeHash) => new(typeHash, default);
    /// <summary>Sets the fields once; copies the field list but shares the values and nodes it references.</summary>
    /// <exception cref="InvalidOperationException">The node is already complete, as every decoded node is.</exception>
    public void Complete(IEnumerable<PsoField> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        CompleteWith(fields.ToArray());
    }
    internal void CompleteWith(PsoField[] fields)
    {
        if (IsComplete)
            throw new InvalidOperationException("The PSO node is already complete.");
        Fields = Array.AsReadOnly(fields);
        IsComplete = true;
    }
}

/// <summary>A PSO file, its decoded root graph and the reading diagnostics.</summary>
public sealed record PsoDocument(PsoFile File, PsoNode Root, ValidationReport Report);

/// <summary>Limits on graph depth, decoded nodes and array elements, and the field error policy.</summary>
public sealed class PsoReadOptions
{
    public int MaximumDepth { get; }
    /// <summary>Maximum number of decoded structure nodes.</summary>
    public int MaximumNodes { get; }
    /// <summary>Maximum total number of array elements and map entries.</summary>
    public int MaximumArrayElements { get; }
    /// <summary>When true, a field whose bytes are malformed or exceed a limit becomes <see cref="PsoMalformed"/> with an
    /// error diagnostic, and the remaining fields are still decoded. When false (default), reading throws.</summary>
    public bool ContinueOnFieldErrors { get; }
    /// <summary>All limits must be positive.</summary>
    public PsoReadOptions(int maximumDepth = 128, int maximumNodes = 100_000, int maximumArrayElements = 1_000_000, bool continueOnFieldErrors = false)
    {
        ContinueOnFieldErrors = continueOnFieldErrors;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumArrayElements);
        MaximumDepth = maximumDepth;
        MaximumNodes = maximumNodes;
        MaximumArrayElements = maximumArrayElements;
    }
}
