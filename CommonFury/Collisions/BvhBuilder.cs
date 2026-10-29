using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Collisions;

/// <summary>Selects how many items go to the first child when a node is split at the median.</summary>
public enum BvhSplit
{
    /// <summary>The first child receives <c>(n + 1) / 2</c> items.</summary>
    UpperMedian,
    /// <summary>The first child receives <c>n / 2</c> items.</summary>
    LowerMedian
}

/// <summary>Selects what a leaf's <see cref="BvhNode.ItemId"/> refers to.</summary>
public enum BvhLeafIds
{
    /// <summary>Position of the leaf's first item in <see cref="BvhBuild.Order"/>; items are stored in that order (geometry polygons).</summary>
    OrderPosition,
    /// <summary>Caller index of the leaf's first item; items stay in place (composite child slots, normally with leaf size 1).</summary>
    FirstItemIndex
}

/// <summary>Builder settings. Every member is explicit because formats differ.</summary>
/// <param name="LeafSize">Largest item count of a leaf, 1–255 (typically 4 for geometry, 1 for composites).</param>
/// <param name="Split">Which child receives the odd item when splitting.</param>
/// <param name="LeafIds">What a leaf's item id refers to.</param>
public readonly record struct BvhBuildOptions(int LeafSize, BvhSplit Split, BvhLeafIds LeafIds);

/// <summary>One item to place in the hierarchy. <paramref name="Index"/> breaks sorting ties and is reported in <see cref="BvhBuild.Order"/>.</summary>
public readonly record struct BvhItem(int Index, Aabb3 Bounds);

/// <summary>An unquantized hierarchy node. Branch: <see cref="ItemId"/> is the escape distance (the subtree's node count)
/// and <see cref="ItemCount"/> is zero. Leaf: first item id and item count.</summary>
public readonly record struct BvhNode(Aabb3 Bounds, ushort ItemId, byte ItemCount)
{
    public bool IsLeaf => ItemCount != 0;
}

/// <summary>A subtree's bounds and preorder node range; <see cref="EndNode"/> is exclusive.</summary>
public readonly record struct BvhSubtree(Aabb3 Bounds, ushort RootNode, ushort EndNode);

/// <summary>Result of <see cref="BvhBuilder.Build(ReadOnlySpan{BvhItem}, BvhBuildOptions)"/>: preorder nodes and the item order of the leaves.</summary>
public sealed class BvhBuild
{
    internal BvhBuild(BvhNode[] nodes, int[] order, Aabb3 bounds)
    {
        Nodes = Array.AsReadOnly(nodes);
        Order = order;
        Bounds = bounds;
    }

    /// <summary>Nodes in preorder; empty when there were no items.</summary>
    public IReadOnlyList<BvhNode> Nodes { get; }
    /// <summary>Item indices in leaf order. The array is owned by the caller after building.</summary>
    public int[] Order { get; }
    /// <summary>Bounds of the root node, or <see cref="Aabb3.Empty"/> when there were no items.</summary>
    public Aabb3 Bounds { get; }

    /// <summary>Partitions the hierarchy into subtrees of at most <paramref name="maximumNodeCount"/> nodes, splitting larger branches
    /// at their children, in preorder.</summary>
    public IReadOnlyList<BvhSubtree> Subtrees(int maximumNodeCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumNodeCount);
        var result = new List<BvhSubtree>();
        int Size(int index) => Nodes[index].IsLeaf ? 1 : Nodes[index].ItemId;
        void Visit(int index)
        {
            int size = Size(index);
            if (size > maximumNodeCount && !Nodes[index].IsLeaf)
            {
                Visit(index + 1);
                Visit(index + 1 + Size(index + 1));
                return;
            }
            result.Add(new(Nodes[index].Bounds, checked((ushort)index), checked((ushort)(index + size))));
        }
        if (Nodes.Count != 0)
            Visit(0);
        return result.AsReadOnly();
    }
}

/// <summary>Builds collision bounding-volume hierarchies by recursive median splits along the longest axis.</summary>
/// <remarks>Items are ordered by box center on the split axis, then by index, so the output is deterministic.
/// Nodes are emitted in preorder.</remarks>
public static class BvhBuilder
{
    private static readonly Comparison<Entry>[] ByAxis = [(a, b) => Compare(a, b, 0), (a, b) => Compare(a, b, 1), (a, b) => Compare(a, b, 2)];

    /// <summary>Builds a hierarchy over boxes whose indices are their positions in the span.</summary>
    public static BvhBuild Build(ReadOnlySpan<Aabb3> boxes, BvhBuildOptions options)
    {
        Validate(options);
        var entries = new Entry[boxes.Length];
        for (int i = 0; i < entries.Length; i++)
            entries[i] = new(i, boxes[i]);
        return Build(entries, true, options);
    }

    /// <summary>Builds a hierarchy over indexed items; indices must increase with item position when they are not positions.</summary>
    public static BvhBuild Build(ReadOnlySpan<BvhItem> items, BvhBuildOptions options)
    {
        Validate(options);
        var entries = new Entry[items.Length];
        bool increasing = true;
        for (int i = 0; i < entries.Length; i++)
        {
            entries[i] = new(items[i].Index, items[i].Bounds);
            increasing &= i == 0 || items[i].Index > items[i - 1].Index;
        }
        return Build(entries, increasing, options);
    }

    private static void Validate(BvhBuildOptions options)
    {
        if (options.LeafSize is < 1 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "Leaf size must be between 1 and 255.");
        if (!Enum.IsDefined(options.Split) || !Enum.IsDefined(options.LeafIds))
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    private static BvhBuild Build(Entry[] items, bool uniqueIndices, BvhBuildOptions options)
    {
        var nodes = new List<BvhNode>();
        var order = new int[items.Length];
        if (items.Length == 0)
            return new([], order, Aabb3.Empty);
        Visit(items, 0, items.Length, uniqueIndices, options, nodes, order);
        if (nodes.Count > ushort.MaxValue)
            throw new ArgumentException("The hierarchy exceeds UInt16 node escapes.", nameof(items));
        return new(nodes.ToArray(), order, nodes[0].Bounds);
    }

    private static void Visit(Entry[] items, int start, int count, bool uniqueIndices, BvhBuildOptions options, List<BvhNode> nodes, int[] order)
    {
        Vector3 minimum = items[start].Bounds.Minimum, maximum = items[start].Bounds.Maximum;
        for (int i = start + 1; i < start + count; i++)
        {
            minimum = Vector3.Min(minimum, items[i].Bounds.Minimum);
            maximum = Vector3.Max(maximum, items[i].Bounds.Maximum);
        }
        var bounds = new Aabb3(minimum, maximum);
        int index = nodes.Count;
        if (count <= options.LeafSize)
        {
            int id = options.LeafIds == BvhLeafIds.OrderPosition ? start : items[start].Index;
            nodes.Add(new(bounds, checked((ushort)id), (byte)count));
            for (int i = start; i < start + count; i++)
                order[i] = items[i].Index;
            return;
        }
        nodes.Add(default);
        Vector3 size = bounds.Size;
        int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
        int first = options.Split == BvhSplit.UpperMedian ? (count + 1) / 2 : count / 2;
        Span<Entry> range = items.AsSpan(start, count);
        if (uniqueIndices)
        {
            // Unique keys make each child's item set independent of the order inside a range, and Vector3.Min/Max bounds do not
            // depend on order either, so selecting the median is enough; only leaves need their items in sorted order.
            Select(range, first, axis);
            if (first <= options.LeafSize)
                range[..first].Sort(ByAxis[axis]);
            if (count - first <= options.LeafSize)
                range[first..].Sort(ByAxis[axis]);
        }
        else
        {
            // Repeated indices can tie, and then the sort's arrangement of equal keys decides the children.
            range.Sort(ByAxis[axis]);
        }
        Visit(items, start, first, uniqueIndices, options, nodes, order);
        Visit(items, start + first, count - first, uniqueIndices, options, nodes, order);
        nodes[index] = new(bounds, checked((ushort)(nodes.Count - index)), 0);
    }

    // Moves the k smallest entries to the front. Falls back to sorting after too many unbalanced partitions.
    private static void Select(Span<Entry> range, int k, int axis)
    {
        int budget = 2 * BitOperations.Log2((uint)range.Length);
        while (k > 0 && k < range.Length)
        {
            if (range.Length <= 16 || budget-- == 0)
            {
                range.Sort(ByAxis[axis]);
                return;
            }
            int pivot = Partition(range, axis);
            if (k <= pivot)
            {
                range = range[..pivot];
            }
            else
            {
                range = range[(pivot + 1)..];
                k -= pivot + 1;
            }
        }
    }

    // Partitions around the median of the first, middle and last entries and returns the pivot's final position.
    private static int Partition(Span<Entry> range, int axis)
    {
        int last = range.Length - 1, middle = last / 2;
        if (Compare(range[middle], range[0], axis) < 0)
            (range[0], range[middle]) = (range[middle], range[0]);
        if (Compare(range[last], range[0], axis) < 0)
            (range[0], range[last]) = (range[last], range[0]);
        if (Compare(range[last], range[middle], axis) < 0)
            (range[middle], range[last]) = (range[last], range[middle]);
        (range[middle], range[last]) = (range[last], range[middle]);
        Entry pivot = range[last];
        int store = 0;
        for (int i = 0; i < last; i++)
        {
            if (Compare(range[i], pivot, axis) < 0)
            {
                (range[i], range[store]) = (range[store], range[i]);
                store++;
            }
        }
        (range[store], range[last]) = (range[last], range[store]);
        return store;
    }

    private static int Compare(in Entry a, in Entry b, int axis)
    {
        int comparison = a.Center[axis].CompareTo(b.Center[axis]);
        return comparison == 0 ? a.Index.CompareTo(b.Index) : comparison;
    }

    private readonly struct Entry(int index, Aabb3 bounds)
    {
        public readonly int Index = index;
        public readonly Aabb3 Bounds = bounds;
        public readonly Vector3 Center = bounds.Center;
    }
}
