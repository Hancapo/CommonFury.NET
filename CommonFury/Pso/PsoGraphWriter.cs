using System.Diagnostics.CodeAnalysis;
using CommonFury.Binary;

namespace CommonFury.Pso;

/// <summary>
/// Writes supported decoded values with canonical padding and relocations. Node identity is retained;
/// original padding, inactive fixed-array storage and binary allocation identity are not retained.
/// With <see cref="PsoPointerLayout.BlockId20"/>, every allocation gets its own block and pointers must target either a
/// block start or a block no longer than 4 KiB, so readers never need to reconstruct wrapped page offsets.
/// </summary>
public sealed class PsoGraphWriter(PsoSchema schema, PsoPointerLayout layout = PsoPointerLayout.BlockId12)
{
    private readonly bool wide = layout == PsoPointerLayout.BlockId20;
    private readonly List<BlockPlan> blockOrder = [];
    private readonly HashSet<BlockPlan> pagedTargets = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<PsoNode, NodePlan> nodes = new(ReferenceEqualityComparer.Instance);
    private readonly List<NodePlan> nodeOrder = [];
    private readonly Queue<NodePlan> pending = [];
    private readonly List<NodePlan> pointerTargets = [];
    private readonly Dictionary<uint, BlockPlan> blocks = [];
    private readonly Dictionary<PsoNode, Location> locations = new(ReferenceEqualityComparer.Instance);
    private bool started;

    /// <summary>Configure framing and extra sections here before calling <see cref="Write"/>.</summary>
    public PsoBuilder Builder { get; } = new(schema) { PointerLayout = layout };

    /// <summary>Lays out the graph reachable from <paramref name="root"/> and builds the file. Each writer writes once.</summary>
    public PsoFile Write(PsoNode root)
    {
        ArgumentNullException.ThrowIfNull(root);
        if (started || Builder.Blocks.Count != 0 || Builder.PointerPatches.Count != 0)
            throw new InvalidOperationException("A PSO graph writer performs one write and owns its blocks and relocations.");
        started = true;
        Register(root);
        while (this.pending.TryDequeue(out NodePlan? plan))
            Discover(plan);
        RejectInlineCycles();
        PlaceNodes(root);
        Builder.Root = locations[root].Region.Block.Builder;
        foreach (NodePlan target in pointerTargets)
        {
            Location location = locations[target.Node];
            Require(target.Node.TypeHash != 1 && location.Region.Block.Builder.NameHash == target.Node.TypeHash,
                "A polymorphic PSO pointer cannot identify an inline node with a different containing block type.");
        }
        foreach (NodePlan plan in nodeOrder)
        {
            Location owner = locations[plan.Node];
            foreach (FieldPlan field in plan.Fields)
            {
                if (field.Entry.DataType == PsoDataType.Array)
                    WriteArray(owner, field.Entry, field.Element!.Value, Value<PsoArray>(field.Value, field.Entry));
                else if (field.Value is PsoMap map && field.Entry.DataType == PsoDataType.Map)
                {
                    WriteCount(owner.Add(field.Entry.DataOffset), map.Sorted ? 1 : 0, 1);
                    WriteArray(owner, PsoMapLayout.Array(field.Entry), PsoMapLayout.Element(map.PairTypeHash), PsoMapLayout.Values(map));
                }
                else
                    WriteValue(field.Entry, field.Value, owner.Add(field.Entry.DataOffset), false);
            }
        }
        foreach (BlockPlan block in pagedTargets)
            if (block.Length > 0x1000)
                throw new NotSupportedException("A 20-bit PSO pointer into a block longer than 4 KiB must target the block start.");
        foreach (BlockPlan block in blockOrder)
        {
            foreach (Region region in block.Regions)
            {
                int padding = region.Offset - block.Builder.Data.Length;
                if (padding != 0)
                    block.Builder.Append(new byte[padding]);
                block.Builder.Append(region.Data);
            }
        }
        return Builder.Build();
    }

    private NodePlan Register(PsoNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (this.nodes.TryGetValue(node, out NodePlan? found))
            return found;
        Require(node.Complete, "An incomplete PSO node cannot be written.");
        if (Builder.Schema.ByHash.GetValueOrDefault(node.TypeHash) is not PsoStruct structure)
            throw new NotSupportedException($"No PSO structure schema for 0x{node.TypeHash:X8}.");
        var plan = new NodePlan(node, structure);
        this.nodes.Add(node, plan);
        nodeOrder.Add(plan);
        this.pending.Enqueue(plan);
        return plan;
    }

    private void Discover(NodePlan plan)
    {
        var fields = new Dictionary<PsoEntry, PsoValue>();
        foreach (PsoField field in plan.Node.Fields)
        {
            Require(field is not null && field.Value is not null, "PSO fields cannot be null.");
            Require(fields.TryAdd(field.Entry, field.Value), "Duplicate PSO field.");
        }
        PsoEntry? element = null;
        foreach (PsoEntry entry in plan.Schema.Entries)
        {
            if (entry.IsAnonymous)
            { element = entry; continue; }
            Require(fields.Remove(entry, out PsoValue? value), $"PSO field 0x{entry.NameHash:X8} is missing or has mismatched metadata.");
            int storage;
            if (entry.DataType == PsoDataType.Map)
            {
                PsoMap map = Value<PsoMap>(value!, entry);
                PsoMapLayout.Descriptors(plan.Schema, entry);
                storage = PsoLayout.MapStorageSize;
                if (map.Entries.Count != 0 || map.PairTypeHash != 0)
                {
                    var pair = PsoMapLayout.Pair(Builder.Schema, plan.Schema, entry, map.PairTypeHash);
                    DiscoverArray(plan, PsoMapLayout.Array(entry), PsoMapLayout.Element(map.PairTypeHash), PsoMapLayout.Values(map), pair.Length);
                }
            }
            else if (entry.DataType == PsoDataType.Array)
            {
                Require(element is not null, "A PSO array requires an ARRAYINFO descriptor.");
                PsoArray array = Value<PsoArray>(value!, entry);
                int stride = PsoLayout.ElementStride(Builder.Schema, element.Value);
                Require(stride > 0 || array.Values.Count == 0 && (!PsoLayout.IsInlineArray(entry) || entry.ReferenceKey >> 16 == 0),
                    "PSO array element size is unknown.");
                storage = PsoLayout.ArrayStorageSize(entry, stride);
                CheckArrayCount(plan, entry, array.Values.Count);
                DiscoverArray(plan, entry, element.Value, array, stride);
            }
            else
            {
                storage = PsoLayout.ElementStride(Builder.Schema, entry);
                bool emptyStructure = entry.DataType == PsoDataType.Structure && entry.Subtype == 0 &&
                    Builder.Schema.ByHash.GetValueOrDefault(entry.ReferenceKey) is PsoStruct { Length: 0 };
                Require(storage > 0 || emptyStructure || entry.DataType == PsoDataType.String && entry.Subtype == 0 && Value<PsoText>(value!, entry).Bytes.IsEmpty,
                    $"Unsupported PSO field layout for 0x{entry.NameHash:X8}.");
                DiscoverValue(plan, entry, value!, entry.DataOffset, false);
            }
            Require(entry.DataOffset <= plan.Schema.Length - storage, "PSO field storage exceeds its containing structure.");
            plan.Fields.Add(new(entry, value!, entry.DataType == PsoDataType.Array ? element : null));
        }
        Require(fields.Count == 0, "PSO node contains fields absent from its schema.");
    }

    private void DiscoverArray(NodePlan owner, PsoEntry entry, PsoEntry element, PsoArray array, int stride)
    {
        NodePlan? first = null;
        bool inline = PsoLayout.IsInlineArray(entry);
        for (int i = 0; i < array.Values.Count; i++)
        {
            PsoValue value = array.Values[i];
            if (!inline && element.DataType == PsoDataType.Structure && !PsoLayout.IsPointerElement(element))
            {
                NodePlan item = StructureValue(element, value);
                if (first is null)
                    first = item;
                else
                    Relate(first, item, checked(i * stride), false);
            }
            else
                DiscoverValue(owner, element, value, checked(entry.DataOffset + i * stride), true);
        }
    }

    private void DiscoverValue(NodePlan owner, PsoEntry entry, PsoValue value, int offset, bool arrayElement)
    {
        RejectUnsupported(value);
        if (entry.DataType != PsoDataType.Structure)
            return;
        bool pointer = entry.Subtype is 3 or 4 || arrayElement && PsoLayout.IsPointerElement(entry);
        if (pointer)
        {
            if (value is PsoNodeValue target)
                pointerTargets.Add(Register(target.Node));
            else
                Require(value is PsoNull or PsoText, "PSO structure pointers require a node, text target or null.");
        }
        else
        {
            Require(entry.Subtype == 0, "Unsupported PSO structure subtype.");
            Relate(owner, StructureValue(entry, value), offset, true);
        }
    }

    private NodePlan StructureValue(PsoEntry entry, PsoValue value)
    {
        PsoNode node = Value<PsoNodeValue>(value, entry).Node;
        Require(node.TypeHash == entry.ReferenceKey && entry.ReferenceKey != 0, "Inline PSO structure type does not match its schema.");
        return Register(node);
    }

    private static void Relate(NodePlan owner, NodePlan child, int offset, bool inline)
    {
        owner.Relations.Add(new(child, offset));
        child.Relations.Add(new(owner, -offset));
        if (inline)
        {
            owner.InlineChildren.Add(child);
            child.InlineParents++;
        }
    }

    private void RejectInlineCycles()
    {
        var pending = new Queue<NodePlan>(nodeOrder.Where(node => node.InlineParents == 0));
        int visited = 0;
        while (pending.TryDequeue(out NodePlan? node))
        {
            visited++;
            foreach (NodePlan child in node.InlineChildren)
                if (--child.InlineParents == 0)
                    pending.Enqueue(child);
        }
        Require(visited == nodeOrder.Count, "Recursive inline PSO structure.");
    }

    private void PlaceNodes(PsoNode root)
    {
        var positions = new Dictionary<NodePlan, long>();
        var identities = new HashSet<(PsoBlockBuilder Block, int Offset, uint Type)>();
        foreach (NodePlan start in nodeOrder)
        {
            if (positions.ContainsKey(start))
                continue;
            var component = new List<NodePlan>();
            var pending = new Queue<NodePlan>();
            positions.Add(start, 0);
            pending.Enqueue(start);
            long first = 0, end = 0;
            NodePlan blockType = start;
            bool hasRoot = false;
            while (pending.TryDequeue(out NodePlan? node))
            {
                long position = positions[node];
                component.Add(node);
                hasRoot |= ReferenceEquals(node.Node, root);
                if (position < first || position == first && node.Schema.Length > blockType.Schema.Length)
                { first = position; blockType = node; }
                end = Math.Max(end, checked(position + node.Schema.Length));
                foreach (Relation relation in node.Relations)
                {
                    long expected = checked(position + relation.Offset);
                    if (positions.TryGetValue(relation.Node, out long actual))
                        Require(actual == expected, "PSO inline structure or array positions conflict.");
                    else
                    { positions.Add(relation.Node, expected); pending.Enqueue(relation.Node); }
                }
            }
            Require(!hasRoot || first == 0 && positions[this.nodes[root]] == 0, "The PSO root must begin at offset zero in its block.");
            if (hasRoot)
                blockType = this.nodes[root];
            Region region = Allocate(blockType.Node.TypeHash, Math.Max(1, checked((int)(end - first))), 16);
            foreach (NodePlan node in component)
            {
                Location location = new(region, checked((int)(positions[node] - first)));
                Require(wide || location.Offset <= 0xFFFFF, "PSO node offset exceeds 20 bits.");
                Require(identities.Add((region.Block.Builder, location.Offset, node.Node.TypeHash)), "Distinct PSO nodes occupy the same typed address.");
                locations.Add(node.Node, location);
            }
        }
    }

    private void CheckArrayCount(NodePlan owner, PsoEntry entry, int count)
    {
        int capacity = (int)(entry.ReferenceKey >> 16);
        switch (entry.Subtype)
        {
            case 0:
                Require(count <= ushort.MaxValue, "PSO atArray count exceeds 16 bits.");
                break;
            case 1:
                Require(count <= capacity, "PSO fixed array exceeds its capacity.");
                break;
            case 2:
            case 4:
                Require(count == capacity, "PSO member/range arrays require their exact fixed length.");
                break;
            case 3:
                Require(count == 0 || count == capacity, "PSO pointer arrays require their declared fixed length or null.");
                break;
            case 5:
                break;
            case 6:
            case 7:
            case 8:
                int width = PsoLayout.ExternalCounterWidth(entry);
                Require(capacity <= owner.Schema.Length - width, "PSO array counter exceeds its containing structure.");
                Require(width != 1 || count <= byte.MaxValue, "PSO array count exceeds 8 bits.");
                Require(width != 2 || count <= ushort.MaxValue, "PSO array count exceeds 16 bits.");
                foreach (PsoField field in owner.Node.Fields)
                {
                    if (field.Entry.DataOffset == capacity)
                        Require(field.Value is PsoInteger integer && integer.Value == count && PsoLayout.ElementStride(Builder.Schema, field.Entry) == width,
                            "PSO array count disagrees with its modeled counter member.");
                }
                break;
            default:
                throw new NotSupportedException($"Unsupported PSO array subtype {entry.Subtype}.");
        }
    }

    private void WriteArray(Location owner, PsoEntry entry, PsoEntry element, PsoArray array)
    {
        int count = array.Values.Count;
        int stride = PsoLayout.ElementStride(Builder.Schema, element);
        Location field = owner.Add(entry.DataOffset);
        bool inline = PsoLayout.IsInlineArray(entry);
        Location contents = field;
        if (!inline && count != 0)
        {
            if (element.DataType == PsoDataType.Structure && !PsoLayout.IsPointerElement(element))
                contents = locations[Value<PsoNodeValue>(array.Values[0], element).Node];
            else
            {
                uint type = element.DataType == PsoDataType.String && element.Subtype is 7 or 8 ? (uint)PsoDataType.UInt32 : (uint)element.DataType;
                contents = new(Allocate(type, checked(count * stride), 16), 0);
            }
            Pointer(field, contents);
        }
        for (int i = 0; i < count; i++)
            WriteValue(element, array.Values[i], contents.Add(checked(i * stride)), true);
        switch (entry.Subtype)
        {
            case 0:
                WriteCount(field.Add(8), count, 2);
                WriteCount(field.Add(10), count, 2);
                break;
            case 1:
                WriteCount(field.Add(PsoLayout.FixedArrayCountOffset(entry, stride)), count, 4);
                break;
            case 5:
                WriteCount(field.Add(8), count, 4);
                WriteCount(field.Add(12), count, 4);
                break;
            case 6:
            case 7:
            case 8:
                WriteCount(owner.Add((int)(entry.ReferenceKey >> 16)), count, PsoLayout.ExternalCounterWidth(entry));
                break;
        }
    }

    private void WriteValue(PsoEntry entry, PsoValue value, Location location, bool arrayElement)
    {
        RejectUnsupported(value);
        if (entry.DataType == PsoDataType.Structure)
        {
            if (entry.Subtype is 3 or 4 || arrayElement && PsoLayout.IsPointerElement(entry))
            {
                if (value is PsoNodeValue node)
                    Pointer(location, locations[node.Node]);
                else if (value is PsoText text)
                    Pointer(location, Text(text));
                else
                    Require(value is PsoNull, "PSO structure pointers require a node, text target or null.");
            }
            return;
        }
        if (entry.DataType == PsoDataType.String)
        { WriteString(entry, value, location); return; }
        var writer = new BinarySpanWriter(location.Span(PsoLayout.ElementStride(Builder.Schema, entry)), ByteOrder.Big);
        checked
        {
            switch (entry.DataType)
            {
                case PsoDataType.Bool:
                    writer.WriteByte(Value<PsoBoolean>(value, entry).Value ? (byte)1 : (byte)0);
                    break;
                case PsoDataType.SByte:
                    writer.WriteSByte((sbyte)Value<PsoInteger>(value, entry).Value);
                    break;
                case PsoDataType.Byte:
                    writer.WriteByte((byte)Value<PsoInteger>(value, entry).Value);
                    break;
                case PsoDataType.Int16:
                    writer.WriteInt16((short)Value<PsoInteger>(value, entry).Value);
                    break;
                case PsoDataType.UInt16:
                    writer.WriteUInt16((ushort)Value<PsoInteger>(value, entry).Value);
                    break;
                case PsoDataType.Int32:
                case PsoDataType.Enum:
                case PsoDataType.Flags:
                    writer.WriteInt32((int)Value<PsoInteger>(value, entry).Value);
                    break;
                case PsoDataType.UInt32:
                    writer.WriteUInt32((uint)Value<PsoInteger>(value, entry).Value);
                    break;
                case PsoDataType.Int64:
                    writer.WriteInt64(Value<PsoInteger>(value, entry).Value);
                    break;
                case PsoDataType.Float:
                    writer.WriteSingle(Value<PsoFloat>(value, entry).Value);
                    break;
                case PsoDataType.Half:
                    writer.WriteUInt16(Value<PsoHalf>(value, entry).Bits);
                    break;
                case PsoDataType.Float2:
                    writer.WriteVector2(Value<PsoVector2>(value, entry).Value);
                    break;
                case PsoDataType.Float3:
                case PsoDataType.Float3Aligned:
                    writer.WriteVector3(Value<PsoVector3>(value, entry).Value);
                    break;
                case PsoDataType.Float4:
                case PsoDataType.Float4Aligned:
                    writer.WriteVector4(Value<PsoVector4>(value, entry).Value);
                    break;
                default:
                    throw new NotSupportedException($"Unsupported PSO data type {entry.DataType}.");
            }
        }
    }

    private void WriteString(PsoEntry entry, PsoValue value, Location location)
    {
        if (entry.Subtype is 7 or 8)
        {
            new BinarySpanWriter(location.Span(4), ByteOrder.Big).WriteUInt32(Value<PsoHash>(value, entry).Value.Value);
            return;
        }
        if (value is PsoNull && entry.Subtype is 1 or 2 or 3)
            return;
        PsoText text = Value<PsoText>(value, entry);
        Require(!text.Bytes.Contains((byte)0), "PSO text cannot contain an embedded NUL.");
        if (entry.Subtype == 0)
        {
            int capacity = (int)(entry.ReferenceKey >> 16);
            Require(text.Bytes.Length <= capacity, "PSO inline text exceeds its capacity.");
            text.Bytes.CopyTo(location.Span(capacity));
            return;
        }
        Require(entry.Subtype is 1 or 2 or 3, "Unsupported PSO string subtype.");
        // Empty atStrings are written as a null pointer.
        if (entry.Subtype == 3 && text.Bytes.IsEmpty)
            return;
        int length = text.Bytes.Length;
        Require(entry.Subtype != 3 || length < ushort.MaxValue, "PSO atString length and terminator exceed 16 bits.");
        Pointer(location, Text(text));
        if (entry.Subtype == 3)
        {
            // The length excludes the NUL; the allocation size includes it.
            WriteCount(location.Add(8), length, 2);
            WriteCount(location.Add(10), length + 1, 2);
        }
    }

    private Location Text(PsoText text)
    {
        Require(!text.Bytes.Contains((byte)0), "PSO text cannot contain an embedded NUL.");
        Region region = Allocate(1, checked(text.Bytes.Length + 1), 1);
        text.Bytes.CopyTo(region.Data);
        return new(region, 0);
    }

    private Region Allocate(uint type, int length, int alignment)
    {
        if (wide || !this.blocks.TryGetValue(type, out BlockPlan? block))
        {
            block = new(new(type));
            if (!wide)
                this.blocks.Add(type, block);
            blockOrder.Add(block);
            Builder.Blocks.Add(block.Builder);
        }
        int offset = BinaryLayout.Align(block.Length, alignment);
        Require(wide || offset <= 0xFFFFF, "PSO allocation offset exceeds 20 bits.");
        var region = new Region(block, offset, new byte[length]);
        block.Length = checked(offset + length);
        block.Regions.Add(region);
        return region;
    }

    private void Pointer(Location source, Location target)
    {
        _ = source.Span(8);
        if (wide)
        {
            Require(target.Offset <= 0xFFF, "PSO pointer offset exceeds 12 bits.");
            if (target.Offset != 0)
                pagedTargets.Add(target.Region.Block);
        }
        else
            Require(target.Offset <= 0xFFFFF, "PSO pointer offset exceeds 20 bits.");
        Builder.PointerPatches.Add(new(source.Region.Block.Builder, source.Offset, target.Region.Block.Builder, target.Offset));
    }

    private static void WriteCount(Location location, int count, int width)
    {
        var writer = new BinarySpanWriter(location.Span(width), ByteOrder.Big);
        switch (width)
        {
            case 1:
                writer.WriteByte(checked((byte)count));
                break;
            case 2:
                writer.WriteUInt16(checked((ushort)count));
                break;
            default:
                writer.WriteUInt32(checked((uint)count));
                break;
        }
    }

    private static T Value<T>(PsoValue value, PsoEntry entry) where T : PsoValue
    {
        RejectUnsupported(value);
        return value as T ?? throw new InvalidDataException($"PSO field 0x{entry.NameHash:X8} has a value incompatible with its schema type.");
    }

    private static void RejectUnsupported(PsoValue value)
    {
        if (value is null or PsoUnsupported or PsoUnresolvedPointer or PsoMalformed)
            throw new NotSupportedException("Unresolved or unsupported PSO values cannot be relocated.");
    }

    private static void Require([DoesNotReturnIf(false)] bool condition, string message) => BinaryLayout.Require(condition, message);

    private sealed class NodePlan(PsoNode node, PsoStruct schema)
    {
        internal PsoNode Node { get; } = node;
        internal PsoStruct Schema { get; } = schema;
        internal List<FieldPlan> Fields { get; } = [];
        internal List<Relation> Relations { get; } = [];
        internal List<NodePlan> InlineChildren { get; } = [];
        internal int InlineParents { get; set; }
    }

    private sealed record FieldPlan(PsoEntry Entry, PsoValue Value, PsoEntry? Element);
    private sealed record Relation(NodePlan Node, int Offset);

    private sealed class BlockPlan(PsoBlockBuilder builder)
    {
        internal PsoBlockBuilder Builder { get; } = builder;
        internal List<Region> Regions { get; } = [];
        internal int Length { get; set; }
    }

    private sealed record Region(BlockPlan Block, int Offset, byte[] Data);

    private readonly record struct Location(Region Region, int RelativeOffset)
    {
        internal int Offset => checked(Region.Offset + RelativeOffset);
        internal Location Add(int offset) => new(Region, checked(RelativeOffset + offset));
        internal Span<byte> Span(int length)
        {
            Require(length >= 0 && RelativeOffset >= 0 && RelativeOffset <= Region.Data.Length - length, "PSO value exceeds its allocated storage.");
            return Region.Data.AsSpan(RelativeOffset, length);
        }
    }
}
