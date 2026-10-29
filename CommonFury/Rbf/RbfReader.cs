using CommonFury.Binary;

namespace CommonFury.Rbf;

internal ref struct RbfReader
{
    private BinarySpanReader reader;
    private readonly RbfLimits limits;
    private readonly List<string> names = [];
    private readonly List<RbfDescriptor> descriptors = [];
    private int nodes;

    internal RbfReader(ReadOnlySpan<byte> data, RbfLimits limits)
    {
        BinaryLayout.Require(data.Length <= limits.MaximumBytes, "RBF input exceeds the byte budget.");
        BinaryLayout.Require(RbfDocument.IsRbf(data), "Expected RBF0 at the beginning of the document.");
        reader = new BinarySpanReader(data);
        reader.Seek(4);
        this.limits = limits;
    }

    internal RbfDocument Read()
    {
        var leading = new List<RbfComment>();
        RbfNode? node = ReadRecord(1, attribute: false);
        while (node is RbfComment comment)
        {
            leading.Add(comment);
            node = ReadRecord(1, attribute: false);
        }
        if (node is not RbfStructure root)
            throw new InvalidDataException("RBF requires one root container.");
        var document = new RbfDocument(root, descriptors.AsReadOnly());
        document.LeadingComments.AddRange(leading);
        while (reader.Remaining != 0)
        {
            BinaryLayout.Require(reader.ReadUInt16() == RbfEncoding.Comment, "Only comments may follow the RBF root.");
            CountNode();
            document.TrailingComments.Add(new RbfComment(ReadData()));
        }
        return document;
    }

    private RbfNode? ReadRecord(int depth, bool attribute)
    {
        ushort record = reader.ReadUInt16();
        if (record == RbfEncoding.Close)
            return null;
        CountNode();
        if (record == RbfEncoding.Comment)
            return new RbfComment(ReadData());
        if (record == RbfEncoding.Bytes)
        {
            BinaryLayout.Require(!attribute, "RBF data blocks cannot be attributes.");
            return new RbfBytes(ReadData());
        }

        uint fullId = record == RbfEncoding.Extended ? reader.ReadUInt32() : record;
        uint nameId = fullId & (record == RbfEncoding.Extended ? 0x0FFFFFFFU : 0x0FFFU);
        var type = (RbfRecordType)(fullId >> (record == RbfEncoding.Extended ? 28 : 12));
        if (type > RbfRecordType.String)
            throw new InvalidDataException($"Unsupported RBF record type 0x{(byte)type:X}.");
        if (nameId > names.Count)
            throw new InvalidDataException($"Invalid RBF name index {nameId}.");
        if (nameId == names.Count)
        {
            BinaryLayout.Require(names.Count < this.limits.MaximumNames, "RBF exceeds the name budget.");
            names.Add(RbfEncoding.ReadText(ref reader));
            descriptors.Add(new((int)nameId, names[^1], type));
        }
        string name = names[(int)nameId];
        if (type == RbfRecordType.Structure)
        {
            BinaryLayout.Require(!attribute, "RBF containers cannot be attributes.");
            return ReadStructure(name, depth);
        }
        return type switch
        {
            RbfRecordType.Int32 => new RbfInt32Value(name, reader.ReadInt32()),
            RbfRecordType.True => new RbfBooleanValue(name, true),
            RbfRecordType.False => new RbfBooleanValue(name, false),
            RbfRecordType.Float => new RbfFloatValue(name, reader.ReadSingle()),
            RbfRecordType.Vector3 => new RbfVector3Value(name, reader.ReadVector3()),
            RbfRecordType.String => new RbfStringValue(name, RbfEncoding.ReadText(ref reader)),
            _ => throw new InvalidDataException("Unsupported RBF record type.")
        };
    }

    private RbfStructure ReadStructure(string name, int depth)
    {
        BinaryLayout.Require(depth <= this.limits.MaximumDepth, "RBF exceeds the container depth budget.");
        var structure = new RbfStructure(name)
        {
            Reserved1 = reader.ReadUInt16(),
            Reserved2 = reader.ReadUInt16()
        };
        int remainingAttributes = reader.ReadUInt16();
        while (remainingAttributes != 0)
        {
            RbfNode? attribute = ReadRecord(depth, attribute: true);
            BinaryLayout.Require(attribute is not null, "RBF container closed before all attributes were read.");
            structure.Attributes.Add(attribute!);
            if (attribute is not RbfComment)
                remainingAttributes--;
        }
        while (true)
        {
            RbfNode? child = ReadRecord(depth + 1, attribute: false);
            if (child is null)
                return structure;
            structure.Children.Add(child);
        }
    }

    private ReadOnlySpan<byte> ReadData()
    {
        uint length = reader.ReadUInt32();
        BinaryLayout.Require(length <= reader.Remaining, "RBF data or comment length exceeds the available bytes.");
        return reader.ReadBytes((int)length);
    }

    private void CountNode()
    {
        BinaryLayout.Require(nodes < this.limits.MaximumNodes, "RBF exceeds the record budget.");
        nodes++;
    }
}
