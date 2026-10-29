using System.Numerics;
using CommonFury.Collisions;
using CommonFury.Numerics;
using Xunit;

namespace CommonFury.Tests;

public sealed class BvhTests
{
    private static readonly BvhBuildOptions UpperMedianGeometry = new(4, BvhSplit.UpperMedian, BvhLeafIds.OrderPosition);
    private static readonly BvhBuildOptions UpperMedianComposite = new(1, BvhSplit.UpperMedian, BvhLeafIds.FirstItemIndex);
    private static readonly BvhBuildOptions LowerMedianGeometry = new(4, BvhSplit.LowerMedian, BvhLeafIds.OrderPosition);
    private static readonly BvhBuildOptions LowerMedianComposite = new(1, BvhSplit.LowerMedian, BvhLeafIds.FirstItemIndex);

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(37)] [InlineData(600)]
    public void MatchesUpperMedianBuilderAndInverseClampedQuantization(int count)
    {
        var random = new Random(count);
        BvhItem[] items = RandomItems(random, count, sparse: true);
        foreach (var (options, childIndices) in new[] { (UpperMedianGeometry, false), (UpperMedianComposite, true) })
        {
            var expected = UpperMedianReference.Create(items, options.LeafSize, childIndices);
            BvhBuild actual = BvhBuilder.Build(items, options);
            Assert.Equal(expected.Nodes, actual.Nodes);
            Assert.Equal(expected.Order, actual.Order);
            Assert.Equal(expected.Trees, actual.Subtrees(127));
            var quantization = BvhQuantization.FromSize(actual.Bounds);
            Assert.Equal(expected.Bvh, (quantization.Center, quantization.Quantum, quantization.QuantumInverse));
            foreach (BvhNode node in actual.Nodes)
                Assert.Equal(UpperMedianReference.Quantize(node.Bounds, quantization), quantization.Quantize(node.Bounds, BvhQuantizationRounding.InverseClamped));
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(5)] [InlineData(37)] [InlineData(600)]
    public void MatchesLowerMedianBuilderAndDivideCheckedQuantization(int count)
    {
        var random = new Random(count + 1000);
        foreach (var (options, childIndices) in new[] { (LowerMedianGeometry, false), (LowerMedianComposite, true) })
        {
            // The lower-median reference builds over dense boxes, then maps leaf positions to child slots for composites.
            BvhItem[] items = RandomItems(random, count, sparse: childIndices);
            var boxes = items.Select(item => (item.Bounds.Minimum, item.Bounds.Maximum)).ToArray();
            var expected = LowerMedianReference.Build(boxes, options.LeafSize);
            int[] slots = items.Select(item => item.Index).ToArray();
            int[] expectedOrder = expected.Order.Select(position => slots[position]).ToArray();
            BvhBuild actual = BvhBuilder.Build(items, options);
            Assert.Equal(expectedOrder, actual.Order);
            Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);
            var quantization = BvhQuantization.FromExtent(actual.Bounds);
            for (int i = 0; i < expected.Nodes.Count; i++)
            {
                var node = expected.Nodes[i];
                int id = node.Count != 0 && childIndices ? expectedOrder[node.Id] : node.Id;
                Assert.Equal(new BvhNode(new(node.Minimum, node.Maximum), (ushort)id, (byte)node.Count), actual.Nodes[i]);
                Assert.Equal(LowerMedianReference.Quantize(node.Minimum, node.Maximum, expected.Minimum, expected.Maximum),
                    quantization.Quantize(actual.Nodes[i].Bounds, BvhQuantizationRounding.DivideChecked));
            }
        }
    }

    [Theory]
    [InlineData(4000, false, false)]
    [InlineData(4000, true, false)]
    [InlineData(1500, false, true)]
    [InlineData(1500, true, true)]
    public void ShuffledAndRepeatedIndicesMatchTheReference(int count, bool shuffle, bool repeat)
    {
        var random = new Random(count + (shuffle ? 1 : 0));
        BvhItem[] items = RandomItems(random, count, sparse: true);
        if (repeat)
            items = items.Select(item => item with { Index = item.Index / 3 }).ToArray();
        if (shuffle)
            random.Shuffle(items);
        foreach (var (options, childIndices) in new[] { (UpperMedianGeometry, false), (UpperMedianComposite, true) })
        {
            var expected = UpperMedianReference.Create(items, options.LeafSize, childIndices);
            BvhBuild actual = BvhBuilder.Build(items, options);
            Assert.Equal(expected.Nodes, actual.Nodes);
            Assert.Equal(expected.Order, actual.Order);
        }
    }

    [Theory]
    [InlineData(BvhLeafIds.OrderPosition)]
    [InlineData(BvhLeafIds.FirstItemIndex)]
    public void EqualCentersOnTheSplitAxisOrderByIndex(BvhLeafIds leafIds)
    {
        Aabb3[] boxes = new float[] { 2, 0, 2, 1, 0, 2 }.Select(x => Aabb3.FromCenterSize(new(x, 0, 0), Vector3.One)).ToArray();
        BvhBuild build = BvhBuilder.Build(boxes, new(2, BvhSplit.UpperMedian, leafIds));
        static Aabb3 AlongX(float minimum, float maximum) => new(new(minimum, -0.5f, -0.5f), new(maximum, 0.5f, 0.5f));
        bool positions = leafIds == BvhLeafIds.OrderPosition;
        Assert.Equal(new BvhNode[]
        {
            new(AlongX(-0.5f, 2.5f), 7, 0),
            new(AlongX(-0.5f, 1.5f), 3, 0),
            new(AlongX(-0.5f, 0.5f), (ushort)(positions ? 0 : 1), 2),
            new(AlongX(0.5f, 1.5f), (ushort)(positions ? 2 : 3), 1),
            new(AlongX(1.5f, 2.5f), 3, 0),
            new(AlongX(1.5f, 2.5f), (ushort)(positions ? 3 : 0), 2),
            new(AlongX(1.5f, 2.5f), 5, 1)
        }, build.Nodes);
        Assert.Equal(new[] { 1, 4, 3, 0, 2, 5 }, build.Order);
    }

    [Fact]
    public void EightGeometryItemsMakeOneBranchAndTwoLeaves()
    {
        // Eight polygons at leaf size four.
        Aabb3[] boxes = Enumerable.Range(0, 8).Select(i => Aabb3.FromCenterSize(new(i * 3, 0, 0), Vector3.One)).ToArray();
        BvhBuild build = BvhBuilder.Build(boxes, LowerMedianGeometry);
        Assert.Equal(3, build.Nodes.Count);
        Assert.Equal((ushort)3, build.Nodes[0].ItemId);
        Assert.Equal(0, build.Nodes[0].ItemCount);
        Assert.Equal(8, build.Nodes.Sum(n => n.ItemCount));
        Assert.All(BvhBuilder.Build(boxes, UpperMedianGeometry).Nodes.Where(n => n.IsLeaf), n => Assert.InRange(n.ItemCount, (byte)1, (byte)4));
    }

    [Fact]
    public void CompositeLeavesReferenceChildSlots()
    {
        // Composite leaves keep child slot indexes, including skipped slots.
        BvhItem[] items = Enumerable.Range(0, 8).Select(i => new BvhItem(i * 2, Aabb3.FromCenterSize(new(i * 10, 0, 0), Vector3.One))).ToArray();
        BvhBuild build = BvhBuilder.Build(items, UpperMedianComposite);
        Assert.Equal(Enumerable.Range(0, 8).Select(i => (ushort)(i * 2)), build.Nodes.Where(n => n.IsLeaf).Select(n => n.ItemId).Order());
    }

    [Fact]
    public void SplitRuleIsExplicit()
    {
        Aabb3[] boxes = Enumerable.Range(0, 3).Select(i => Aabb3.FromCenterSize(new(i, 0, 0), new(0.5f))).ToArray();
        Assert.Equal(3, BvhBuilder.Build(boxes, UpperMedianComposite).Nodes[1].ItemId); // left branch over two items: three nodes
        Assert.True(BvhBuilder.Build(boxes, LowerMedianComposite).Nodes[1].IsLeaf);     // left leaf with one item
        Assert.Empty(BvhBuilder.Build(ReadOnlySpan<Aabb3>.Empty, UpperMedianGeometry).Nodes);
        Assert.Throws<ArgumentOutOfRangeException>(() => BvhBuilder.Build(boxes, new(0, BvhSplit.LowerMedian, BvhLeafIds.OrderPosition)));
        Assert.Throws<ArgumentOutOfRangeException>(() => BvhBuilder.Build(boxes, new(1, (BvhSplit)9, BvhLeafIds.OrderPosition)));
    }

    [Fact]
    public void NodesRoundTripSixteenBytesIncludingPadding()
    {
        var node = new QuantizedBvhNode(new(-32768, 2, 3), new(4, 5, 32767), 0xBEEF, 4, 0xBE);
        var bytes = new byte[QuantizedBvhNode.Size];
        node.Write(bytes);
        Assert.Equal(new byte[] { 0x00, 0x80, 2, 0, 3, 0, 4, 0, 5, 0, 0xFF, 0x7F, 0xEF, 0xBE, 4, 0xBE }, bytes);
        Assert.Equal(node, QuantizedBvhNode.Read(bytes));
        Assert.Throws<InvalidDataException>(() => QuantizedBvhNode.Read(bytes.AsSpan(0, 15)));
    }

    [Fact]
    public void RoundingModesHandleOverflowDifferently()
    {
        var quantization = new BvhQuantization(Vector3.Zero, Vector3.One, Vector3.One);
        var far = Aabb3.FromCenterSize(new(100000, 0, 0), Vector3.One);
        Assert.Equal((short)32767, quantization.Quantize(far, BvhQuantizationRounding.InverseClamped).Minimum.X);
        Assert.Throws<ArgumentException>(() => quantization.Quantize(far, BvhQuantizationRounding.DivideChecked));
        var box = new Aabb3(new(-1.5f, 0, 0.25f), new(1.5f, 0, 0.75f));
        var (minimum, maximum) = quantization.Quantize(box, BvhQuantizationRounding.DivideChecked);
        Assert.Equal((new Short3(-2, 0, 0), new Short3(2, 0, 1)), (minimum, maximum));
        var (low, high) = quantization.Dequantize(minimum, maximum);
        Assert.True(low.X <= box.Minimum.X && high.Z >= box.Maximum.Z);
    }

    private static BvhItem[] RandomItems(Random random, int count, bool sparse)
    {
        var items = new BvhItem[count];
        int index = 0;
        for (int i = 0; i < count; i++)
        {
            // Coarse coordinates force ties on the split axis, exercising index tie-breaking.
            Vector3 center = new(random.Next(-20, 20) * 2.5f, random.Next(-5, 5), random.Next(0, 3) * 0.5f);
            Vector3 size = new(random.Next(0, 4), random.Next(0, 4) * 0.25f, random.Next(1, 4));
            items[i] = new(index, Aabb3.FromCenterSize(center, size));
            index += sparse ? random.Next(1, 3) : 1;
        }
        return items;
    }

    // Reference upper-median builder and inverse-clamped quantization, used as the oracle.
    private static class UpperMedianReference
    {
        private readonly record struct Item(int Index, Aabb3 Bounds);
        private sealed class Node(Item[] items)
        {
            public Item[] Items = items;
            public Node? Left, Right;
            public Aabb3 Bounds;
            public int Index, NodeCount = 1;
            public void Split(int threshold)
            {
                Bounds = Items[0].Bounds;
                foreach (Item item in Items.AsSpan(1))
                    Bounds = Bounds.Merge(item.Bounds);
                if (Items.Length <= threshold)
                    return;
                Vector3 size = Bounds.Size;
                int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
                float Coordinate(Item item) => axis switch { 0 => item.Bounds.Center.X, 1 => item.Bounds.Center.Y, _ => item.Bounds.Center.Z };
                Array.Sort(Items, (a, b) => { int order = Coordinate(a).CompareTo(Coordinate(b)); return order == 0 ? a.Index.CompareTo(b.Index) : order; });
                int middle = (Items.Length + 1) / 2;
                Left = new(Items[..middle]);
                Right = new(Items[middle..]);
                Items = [];
                Left.Split(threshold);
                Right.Split(threshold);
                NodeCount += Left.NodeCount + Right.NodeCount;
            }
        }

        public static (List<BvhNode> Nodes, int[] Order, List<BvhSubtree> Trees, (Vector3, Vector3, Vector3) Bvh) Create(BvhItem[] source, int threshold, bool childIndices)
        {
            Item[] items = source.Select(item => new Item(item.Index, item.Bounds)).ToArray();
            Node root = new(items);
            root.Split(threshold);
            Aabb3 bounds = root.Bounds;
            Vector3 size = bounds.Size;
            Vector3 quantum = new(size.X == 0 ? 1f / 32767 : size.X / 65534, size.Y == 0 ? 1f / 32767 : size.Y / 65534, size.Z == 0 ? 1f / 32767 : size.Z / 65534);
            var nodes = new List<BvhNode>();
            var trees = new List<BvhSubtree>();
            var order = new List<int>();
            void Visit(Node node)
            {
                node.Index = nodes.Count;
                ushort id = checked((ushort)(node.Items.Length == 0 ? node.NodeCount : childIndices ? node.Items[0].Index : order.Count));
                nodes.Add(new(node.Bounds, id, checked((byte)node.Items.Length)));
                foreach (Item item in node.Items)
                    order.Add(item.Index);
                if (node.Left is not null)
                { Visit(node.Left); Visit(node.Right!); }
            }
            void Trees(Node node)
            {
                if (node.NodeCount > 127 && node.Left is not null)
                { Trees(node.Left); Trees(node.Right!); return; }
                trees.Add(new(node.Bounds, checked((ushort)node.Index), checked((ushort)(node.Index + node.NodeCount))));
            }
            Visit(root);
            Trees(root);
            return (nodes, order.ToArray(), trees, (bounds.Center, quantum, Vector3.One / quantum));
        }

        public static (Short3, Short3) Quantize(Aabb3 bounds, BvhQuantization bvh)
        {
            Vector3 minimum = (bounds.Minimum - bvh.Center) * bvh.QuantumInverse;
            Vector3 maximum = (bounds.Maximum - bvh.Center) * bvh.QuantumInverse;
            return (new(Coordinate(minimum.X, false), Coordinate(minimum.Y, false), Coordinate(minimum.Z, false)),
                new(Coordinate(maximum.X, true), Coordinate(maximum.Y, true), Coordinate(maximum.Z, true)));
        }

        private static short Coordinate(float value, bool maximum)
        {
            float clamped = Math.Clamp(value, short.MinValue, short.MaxValue);
            return (short)(maximum ? MathF.Ceiling(clamped) : MathF.Floor(clamped));
        }
    }

    // Reference lower-median builder and divide-checked quantization, used as the oracle.
    private static class LowerMedianReference
    {
        public sealed record Node(Vector3 Minimum, Vector3 Maximum, int Id, int Count);
        public sealed record Result(Vector3 Minimum, Vector3 Maximum, List<Node> Nodes, int[] Order);

        public static Result Build((Vector3 Min, Vector3 Max)[] boxes, int leafSize)
        {
            int[] order = Enumerable.Range(0, boxes.Length).ToArray(); var nodes = new List<Node>();
            int Visit(int start, int count)
            {
                Vector3 minimum = boxes[order[start]].Min, maximum = boxes[order[start]].Max;
                for (int i = start + 1; i < start + count; i++) { minimum = Vector3.Min(minimum, boxes[order[i]].Min); maximum = Vector3.Max(maximum, boxes[order[i]].Max); }
                int index = nodes.Count; nodes.Add(new(minimum, maximum, start, count));
                if (count > leafSize)
                {
                    Vector3 size = maximum - minimum; int axis = size.X >= size.Y && size.X >= size.Z ? 0 : size.Y >= size.Z ? 1 : 2;
                    Array.Sort(order, start, count, Comparer<int>.Create((a, b) =>
                    {
                        float ac = boxes[a].Min[axis] * 0.5f + boxes[a].Max[axis] * 0.5f, bc = boxes[b].Min[axis] * 0.5f + boxes[b].Max[axis] * 0.5f;
                        int comparison = ac.CompareTo(bc); return comparison == 0 ? a.CompareTo(b) : comparison;
                    }));
                    int leftCount = count / 2; Visit(start, leftCount); Visit(start + leftCount, count - leftCount);
                    nodes[index] = new(minimum, maximum, nodes.Count - index, 0);
                }
                return index;
            }
            Visit(0, order.Length);
            return new(nodes[0].Minimum, nodes[0].Maximum, nodes, order);
        }

        public static (Short3, Short3) Quantize(Vector3 nodeMinimum, Vector3 nodeMaximum, Vector3 treeMinimum, Vector3 treeMaximum)
        {
            Vector3 center = treeMinimum * 0.5f + treeMaximum * 0.5f;
            Vector3 extent = Vector3.Max(Vector3.Abs(treeMinimum - center), Vector3.Abs(treeMaximum - center));
            Vector3 quantum = Vector3.Max(extent / 32766, new Vector3(1e-6f));
            Vector3 min = (nodeMinimum - center) / quantum, max = (nodeMaximum - center) / quantum;
            short[] lo = new short[3], hi = new short[3];
            for (int axis = 0; axis < 3; axis++)
            {
                lo[axis] = (short)MathF.Floor(min[axis]);
                hi[axis] = (short)MathF.Ceiling(max[axis]);
            }
            return (new(lo[0], lo[1], lo[2]), new(hi[0], hi[1], hi[2]));
        }
    }
}
