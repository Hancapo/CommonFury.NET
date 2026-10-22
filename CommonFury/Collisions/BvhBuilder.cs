using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Collisions;

/// <summary>Selects how many items go to the first child when a node is split at the median.</summary>
public enum BvhSplit
{
    /// <summary>The first child receives <c>(n + 1) / 2</c> items (GTA V).</summary>
    UpperMedian,
    /// <summary>The first child receives <c>n / 2</c> items (RDR2).</summary>
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

/// <summary>Builder settings. Every member is explicit because the games differ.</summary>
/// <param name="LeafSize">Largest item count of a leaf, 1–255 (4 for geometry, 1 for composites in both games).</param>
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
    /// at their children, in preorder. GTA V stores such a list with 127 as the limit.</summary>
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
    /// <summary>Builds a hierarchy over boxes whose indices are their positions in the span.</summary>
    public static BvhBuild Build(ReadOnlySpan<Aabb3> boxes, BvhBuildOptions options)
    {
        var items = new BvhItem[boxes.Length];
        for (int i = 0; i < items.Length; i++)
            items[i] = new(i, boxes[i]);
        return Build(items, options);
    }

    /// <summary>Builds a hierarchy over indexed items; indices must increase with item position when they are not positions.</summary>
    public static BvhBuild Build(ReadOnlySpan<BvhItem> items, BvhBuildOptions options)
    {
        if (options.LeafSize is < 1 or > byte.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(options), "Leaf size must be between 1 and 255.");
        if (!Enum.IsDefined(options.Split) || !Enum.IsDefined(options.LeafIds))
            throw new ArgumentOutOfRangeException(nameof(options));
        var nodes = new List<BvhNode>();
        var order = new int[items.Length];
        if (items.IsEmpty)
            return new([], order, Aabb3.Empty);
        BvhItem[] work = items.ToArray();
        Visit(work, 0, work.Length, options, nodes, order);
        if (nodes.Count > ushort.MaxValue)
            throw new ArgumentException("The hierarchy exceeds UInt16 node escapes.", nameof(items));
        return new(nodes.ToArray(), order, nodes[0].Bounds);
    }

    private static void Visit(BvhItem[] items, int start, int count, BvhBuildOptions options, List<BvhNode> nodes, int[] order)
    {
        Aabb3 bounds = items[start].Bounds;
        for (int i = start + 1; i < start + count; i++)
            bounds = bounds.Merge(items[i].Bounds);
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
        Array.Sort(items, start, count, Comparer<BvhItem>.Create((a, b) =>
        {
            int comparison = a.Bounds.Center[axis].CompareTo(b.Bounds.Center[axis]);
            return comparison == 0 ? a.Index.CompareTo(b.Index) : comparison;
        }));
        int first = options.Split == BvhSplit.UpperMedian ? (count + 1) / 2 : count / 2;
        Visit(items, start, first, options, nodes, order);
        Visit(items, start + first, count - first, options, nodes, order);
        nodes[index] = new(bounds, checked((ushort)(nodes.Count - index)), 0);
    }
}
