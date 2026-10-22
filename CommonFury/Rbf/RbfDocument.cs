using CommonFury.IO;

namespace CommonFury.Rbf;

/// <summary>Finite input, output and traversal budgets. The root container has depth one; comments count as nodes.</summary>
public sealed class RbfLimits
{
    /// <summary>Largest accepted input or produced output, in bytes.</summary>
    public int MaximumBytes { get; }
    /// <summary>Largest number of records, including comments.</summary>
    public int MaximumNodes { get; }
    public int MaximumNames { get; }
    public int MaximumDepth { get; }

    /// <summary>Bytes may be zero; nodes, names (up to 2^28) and depth (up to 256) must be positive.</summary>
    public RbfLimits(int maximumBytes = 256 * 1024 * 1024, int maximumNodes = 1_000_000,
        int maximumNames = 100_000, int maximumDepth = 128)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNodes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNames);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumNames, 0x10000000);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDepth);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maximumDepth, 256);
        (MaximumBytes, MaximumNodes, MaximumNames, MaximumDepth) =
            (maximumBytes, maximumNodes, maximumNames, maximumDepth);
    }
}

/// <summary>An owned, editable RBF0 document with one root container.</summary>
/// <remarks>
/// Read copies retained data and rejects unknown record types and non-ASCII names/string records.
/// Write preserves modeled values, reserved words and raw payloads, while assigning name IDs in
/// encounter order. Original name-table choices are not retained, so byte identity is not promised,
/// although input that already uses encounter-order IDs is reproduced exactly.
/// Do not mutate the graph or its buffers concurrently with reading its values, validation or writing.
/// </remarks>
public sealed class RbfDocument
{
    private RbfStructure root;
    public RbfStructure Root
    {
        get => this.root;
        set { ArgumentNullException.ThrowIfNull(value); this.root = value; }
    }
    /// <summary>Comments stored before the root container.</summary>
    public List<RbfComment> LeadingComments { get; } = [];
    /// <summary>Comments stored after the root container.</summary>
    public List<RbfComment> TrailingComments { get; } = [];
    /// <summary>The source name table in ID order, with the record type of each name's first use. Empty for authored documents.
    /// It reflects the input as read: edits do not update it and writing does not use it.</summary>
    public IReadOnlyList<RbfDescriptor> Descriptors { get; }

    public RbfDocument(RbfStructure root) : this(root, []) { }

    internal RbfDocument(RbfStructure root, IReadOnlyList<RbfDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(root);
        this.root = root;
        Descriptors = descriptors;
    }

    /// <summary>Checks only the RBF0 signature.</summary>
    public static bool IsRbf(ReadOnlySpan<byte> data) => data.StartsWith("RBF0"u8);

    /// <summary>Copies the input into owned nodes.</summary>
    /// <exception cref="InvalidDataException">The input is malformed or unsupported.</exception>
    public static RbfDocument Read(ReadOnlySpan<byte> data, RbfLimits? limits = null)
    {
        var reader = new RbfReader(data, limits ?? new());
        return reader.Read();
    }

    /// <summary>Checks whether the document can be written within <paramref name="limits"/>.</summary>
    public ValidationReport Validate(RbfLimits? limits = null) => new RbfWritePlan(this, limits ?? new()).Report;

    /// <exception cref="ValidationException">The document cannot be written within <paramref name="limits"/>.</exception>
    public byte[] Write(RbfLimits? limits = null) => new RbfWritePlan(this, limits ?? new()).Write();

    /// <summary>Validates before writing and leaves the stream open; an I/O failure can leave partial output.</summary>
    public void Write(Stream destination, RbfLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        destination.Write(Write(limits));
    }

    /// <summary>Validates, then replaces the file atomically where supported; power-loss durability is not guaranteed.</summary>
    public void Save(string path, RbfLimits? limits = null) => AtomicFile.Write(path, Write(limits));
}
