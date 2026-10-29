using System.Text;
using CommonFury.Binary;

namespace CommonFury.Rbf;

/// <summary>The 4-bit record type stored beside an RBF name ID. Booleans encode their value in the type.</summary>
public enum RbfRecordType : byte
{
    Structure = 0,
    Int32 = 1,
    /// <summary>Boolean true; no payload.</summary>
    True = 2,
    /// <summary>Boolean false; no payload.</summary>
    False = 3,
    Float = 4,
    Vector3 = 5,
    /// <summary>Length-prefixed ASCII string.</summary>
    String = 6
}

/// <summary>Diagnostic codes reported when validating an RBF document for writing.</summary>
public static class RbfDiagnosticCodes
{
    /// <summary>More than 65535 attributes.</summary>
    public const string AttributeCount = "rbf.attribute.count";
    /// <summary>A record that cannot be an attribute is in an attribute list.</summary>
    public const string AttributeType = "rbf.attribute.type";
    /// <summary>A container mixes data blocks with child elements.</summary>
    public const string ContentMixed = "rbf.content.mixed";
    public const string LimitBytes = "rbf.limit.bytes";
    public const string LimitDepth = "rbf.limit.depth";
    public const string LimitNames = "rbf.limit.names";
    public const string LimitNodes = "rbf.limit.nodes";
    /// <summary>A record name is not ASCII text that fits a 16-bit length.</summary>
    public const string Name = "rbf.name";
    public const string NodeNull = "rbf.node.null";
    /// <summary>A string record is a child element instead of an attribute.</summary>
    public const string StringElement = "rbf.string.element";
    /// <summary>A container references itself directly or indirectly.</summary>
    public const string StructureCycle = "rbf.structure.cycle";
    /// <summary>A string value is not ASCII text that fits a 16-bit length.</summary>
    public const string Text = "rbf.text";
}

internal static class RbfEncoding
{
    internal const ushort Close = 0xFFFF, Extended = 0xFFFE, Bytes = 0xFFFD, Comment = 0xFFFC;
    internal const uint ShortNameLimit = 0x1000;

    internal static string ReadText(ref BinarySpanReader reader)
    {
        ReadOnlySpan<byte> bytes = reader.ReadBytes(reader.ReadUInt16());
        BinaryLayout.Require(!bytes.ContainsAnyExceptInRange((byte)0, (byte)127), "RBF names and string records must be ASCII in this implementation.");
        return Encoding.ASCII.GetString(bytes);
    }

    // Encodes straight into output, the storage that writer wraps.
    internal static void WriteText(ref BinarySpanWriter writer, Span<byte> output, string text)
    {
        writer.WriteUInt16(checked((ushort)text.Length));
        Encoding.ASCII.GetBytes(text, output.Slice(writer.Position, text.Length));
        writer.Seek(writer.Position + text.Length);
    }

    // Accepts exactly what ReadText accepts, so every readable document can be written back. Text is length-prefixed,
    // so NUL bytes are representable and preserved.
    internal static bool IsValidText(string? text) => text is not null && text.Length <= ushort.MaxValue && Ascii.IsValid(text);

    internal static RbfRecordType TypeOf(RbfNamedNode node) => node switch
    {
        RbfStructure => RbfRecordType.Structure,
        RbfInt32Value => RbfRecordType.Int32,
        RbfBooleanValue value => value.Value ? RbfRecordType.True : RbfRecordType.False,
        RbfFloatValue => RbfRecordType.Float,
        RbfVector3Value => RbfRecordType.Vector3,
        RbfStringValue => RbfRecordType.String,
        _ => throw new InvalidOperationException("Unknown RBF model type.")
    };
}
