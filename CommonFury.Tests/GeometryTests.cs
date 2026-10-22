using System.Numerics;
using System.Text.Json;
using CommonFury.Geometry;
using CommonFury.Numerics;
using Xunit;

namespace CommonFury.Tests;

public sealed class GeometryTests : IDisposable
{
    // Vectors produced independently of this library.
    private readonly JsonDocument reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ReferenceData", "numerics.json")));
    public void Dispose() => reference.Dispose();

    [Fact]
    public void NormalsAndTangentsMatchReferenceVectors()
    {
        JsonElement mesh = reference.RootElement.GetProperty("mesh");
        Vector3[] positions = mesh.GetProperty("points").EnumerateArray().Select(V3).ToArray();
        Vector2[] uv = mesh.GetProperty("uv").EnumerateArray().Select(e => new Vector2(e[0].GetSingle(), e[1].GetSingle())).ToArray();
        int[] indices = mesh.GetProperty("indices").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        Vector3[] normals = MeshMath.GenerateNormals(positions, indices);
        Vector4[] tangents = MeshMath.GenerateTangents(positions, normals, uv, indices);
        for (int i = 0; i < positions.Length; i++)
        {
            Near(normals[i], mesh.GetProperty("normals")[i], "Mesh normal");
            Near(new Vector3(tangents[i].X, tangents[i].Y, tangents[i].Z), mesh.GetProperty("tangents")[i], "Mesh tangent");
            NumericAssert.Near(tangents[i].W, mesh.GetProperty("tangents")[i][3].GetDouble(), "Tangent handedness");
        }
        Assert.Equal(Vector3.UnitZ, MeshMath.GenerateNormals([Vector3.Zero], [0, 0, 0])[0]);
        Vector4 fallback = MeshMath.GenerateTangents([Vector3.Zero], [Vector3.UnitZ], [Vector2.Zero], [0, 0, 0])[0];
        Vector3 direction = new(fallback.X, fallback.Y, fallback.Z);
        NumericAssert.Near(direction.Length(), 1, "Degenerate UV fallback is unit length.");
        NumericAssert.Near(Vector3.Dot(direction, Vector3.UnitZ), 0, "Degenerate UV fallback is perpendicular to the normal.");
        Assert.Equal(1, fallback.W);
    }

    [Fact]
    public void LargeFiniteUvsDoNotOverflowTangentBasis()
    {
        Vector3[] positions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY];
        Vector3[] normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ];
        Vector2[] uv = [Vector2.Zero, new(2e27f, 1e27f), new(1e27f, 2e27f)];
        Assert.True(PlaneMath.TryUvBasis(positions[0], positions[1], positions[2], uv[0], uv[1], uv[2], out var basis));
        Assert.True(VectorMath.IsFinite(basis.Width) && VectorMath.IsFinite(basis.Height) && VectorMath.IsFinite(basis.Origin));
        Assert.True(Vector3.Distance(basis.Width * uv[1].X + basis.Height * uv[1].Y + basis.Origin, positions[1]) < 1e-6f);
        Assert.All(MeshMath.GenerateTangents(positions, normals, uv, [0, 1, 2]), value => Assert.True(VectorMath.IsFinite(value)));
        Assert.False(PlaneMath.TryUvBasis(Vector3.Zero, new(3e38f, 0, 0), new(0, 3e38f, 0), Vector2.Zero, new(.001f, 0), new(0, .001f), out _));
        Assert.Throws<ArgumentException>(() => PlaneMath.UvBasis(Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector2.Zero, Vector2.Zero, Vector2.Zero));
    }

    [Fact]
    public void TriangleValidationChecksEveryIndex()
    {
        int[] indices = new int[Vector<int>.Count * 3 + 3];
        MeshMath.ValidateTriangleIndices(indices, 1);
        MeshMath.ValidateTriangleIndices([], 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshMath.ValidateTriangleIndices([], -1));
        Assert.Throws<ArgumentException>(() => MeshMath.ValidateTriangleIndices([0], 1));
        Assert.Throws<ArgumentException>(() => MeshMath.ValidateTriangleIndices([0, 0, 0], 0));
        MeshMath.ValidateTriangleIndices([0, int.MaxValue - 1, 1], int.MaxValue);
        for (int i = 0; i < indices.Length; i++)
        {
            foreach (int invalid in new[] { -1, 1, int.MinValue, int.MaxValue })
            {
                indices[i] = invalid;
                Assert.Equal("indices", Assert.Throws<ArgumentException>(() => MeshMath.ValidateTriangleIndices(indices, 1)).ParamName);
            }
            indices[i] = 0;
        }
    }

    [Fact]
    public void PlaneFitUsesTheCallersAxisConvention()
    {
        Vector3[] wall = [new(0, 0, 0), new(4, 0, 0), new(4, 0, 2), new(0, 0, 2)];
        PlaneBasis zUp = PlaneMath.Fit(wall, Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(Vector3.UnitY, Round(zUp.Normal));
        Assert.Equal(-Vector3.UnitX, Round(zUp.X));      // up × normal
        Assert.Equal(-Vector3.UnitZ, Round(zUp.Y));      // X × normal
        Assert.Equal(new Vector3(2, 1, 0), Round(zUp.Extent));
        // A floor is parallel to Z-up, so the fallback direction defines the axes.
        Vector3[] floor = [new(0, 0, 5), new(3, 0, 5), new(3, 1, 5), new(0, 1, 5)];
        PlaneBasis flat = PlaneMath.Fit(floor, Vector3.UnitZ, Vector3.UnitX);
        Assert.Equal(Vector3.UnitZ, Round(flat.Normal));
        Assert.Equal(-Vector3.UnitY, Round(flat.X));     // fallback × normal
        // Another convention gives other axes for the same points.
        PlaneBasis xUp = PlaneMath.Fit(wall, Vector3.UnitX, Vector3.UnitZ);
        Assert.Equal(Vector3.UnitZ, Round(xUp.X));
        Assert.Throws<ArgumentException>(() => PlaneMath.Fit(wall, Vector3.Zero, Vector3.UnitX));
        Assert.Throws<ArgumentException>(() => PlaneMath.Fit(floor, Vector3.UnitZ, Vector3.UnitZ));
        Assert.Throws<ArgumentException>(() => PlaneMath.Fit([Vector3.Zero, Vector3.UnitX, new(2, 0, 0)], Vector3.UnitZ, Vector3.UnitX));
    }

    [Fact]
    public void PolygonsClipTriangulateAndTestContainment()
    {
        Vector3[] square = [new(0, 0, 0), new(2, 0, 2), new(2, 2, 2), new(0, 2, 0)];
        Assert.Equal(4, PolygonMath.SignedAreaXY(square));
        Assert.Equal(-4, PolygonMath.SignedAreaXY(square.Reverse().ToArray()));
        Assert.True(PolygonMath.ContainsConvexXY(square, new(1, 1)));
        Assert.True(PolygonMath.ContainsConvexXY(square, new(2, 1)));
        Assert.False(PolygonMath.ContainsConvexXY(square, new(3, 1)));
        Vector3[] clipped = PolygonMath.ClipToRectangleXY(square, new Aabb2(new(1, 0), new(3, 3)));
        Assert.Equal(1, Math.Round(clipped.Min(v => v.X), 5));
        Assert.Equal(2, Math.Round(PolygonMath.SignedAreaXY(clipped), 5));
        Assert.Contains(clipped, v => v.X == 1 && v.Y == 0 && v.Z == 1); // height interpolated on the cut edge
        Assert.Empty(PolygonMath.ClipToRectangleXY(square, new Aabb2(new(5, 5), new(6, 6))));
        // A concave L shape needs ear clipping; the triangles cover its area exactly.
        Vector3[] shape = [new(0, 0, 0), new(2, 0, 0), new(2, 1, 0), new(1, 1, 0), new(1, 2, 0), new(0, 2, 0)];
        IReadOnlyList<Vector3[]> triangles = PolygonMath.TriangulateXY(shape);
        Assert.Equal(4, triangles.Count);
        Assert.Equal(3, Math.Round(triangles.Sum(t => Math.Abs(PolygonMath.SignedAreaXY(t))), 5));
        Assert.Throws<ArgumentException>(() => PolygonMath.TriangulateXY([Vector3.Zero, Vector3.UnitX, new(2, 0, 0)]));
    }

    [Fact]
    public void HeightFieldsInterpolateTrianglesAndSamples()
    {
        Assert.True(HeightFieldMath.TryTriangleHeight(new(0.25f, 0.25f), new(0, 0, 0), new(1, 0, 4), new(0, 1, 8), out float height));
        Assert.Equal(3, height, 5);
        Assert.False(HeightFieldMath.TryTriangleHeight(new(2, 2), new(0, 0, 0), new(1, 0, 4), new(0, 1, 8), out _));
        Assert.False(HeightFieldMath.TryTriangleHeight(Vector2.Zero, new(0, 0, 0), new(1, 1, 4), new(2, 2, 8), out _));
        Vector3[] samples = [new(0, 0, 10), new(2, 0, 20), new(0, 0, 30)];
        Assert.Equal(10, HeightFieldMath.InterpolateSamples(Vector2.Zero, samples));
        Assert.Equal(15, HeightFieldMath.InterpolateSamples(new(1, 0), [new(0, 0, 10), new(2, 0, 20)]), 5);
        Assert.Equal(10, HeightFieldMath.InterpolateSamples(new(0.1f, 0), [new(0, 0, 10), new(2, 0, 20)], nearestCount: 1));
    }

    [Fact]
    public void StripsAndFansBecomeTriangleLists()
    {
        Assert.Equal([0, 1, 2, 2, 1, 3, 2, 3, 4], MeshPartitioning.TriangulateStrip([0, 1, 2, 3, 4]));
        Assert.Equal([0, 1, 2, 0, 2, 3], MeshPartitioning.TriangulateFan([0, 1, 2, 3]));
        Assert.Throws<ArgumentException>(() => MeshPartitioning.TriangulateFan([0, 1]));
        Assert.Equal([0, 1, 2, 0, 2, 3], MeshPartitioning.TriangulateFanSkippingRepeats(new ushort[] { 0, 0, 1, 1, 2, 3, 3, 0, 0 }));
        Assert.Empty(MeshPartitioning.TriangulateFanSkippingRepeats(new ushort[] { 0, 1, 0 }));
        Assert.Equal([0, 1, 2, 0, 2, 0, 0, 0, 3], MeshPartitioning.TriangulateFanSkippingRepeats(new ushort[] { 0, 1, 2, 0, 3 }));
        Assert.Equal([0, 1, 2, 0, 2, 3], MeshPartitioning.TriangulateFanSkippingRepeats([0, 1, 2, 3, 0]));
        Assert.Empty(MeshPartitioning.TriangulateFanSkippingRepeats([0, 1]));
    }

    [Fact]
    public void SplitKeepsWholeTrianglesUnderTheVertexLimit()
    {
        int[] indices = [0, 1, 2, 2, 1, 3, 3, 4, 5, 5, 4, 0];
        IReadOnlyList<MeshPartition> parts = MeshPartitioning.Split(indices, 6, 4);
        Assert.All(parts, part => Assert.True(part.VertexIndices.Length <= 4 && part.Indices.Length % 3 == 0));
        int[] rebuilt = parts.SelectMany(part => part.Indices.Select(local => part.VertexIndices[local])).ToArray();
        Assert.Equal(indices, rebuilt);
        MeshPartition whole = Assert.Single(MeshPartitioning.Split(indices, 6, 6));
        Assert.Equal([0, 1, 2, 3, 4, 5], whole.VertexIndices);
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshPartitioning.Split(indices, 6, 2));
    }

    [Fact]
    public void RasterCoverageDistanceAndBlur()
    {
        bool[] mask = RasterMath.RasterizeTriangles([new(0, 0), new(4, 0), new(0, 4)], [0, 1, 2], 4, 4);
        Assert.True(mask[0] && mask[1] && mask[4]);
        Assert.False(mask[15]);
        double[] distance = RasterMath.DistanceTransform([true, true, true, false], 4, 1);
        Assert.Equal([9, 4, 1, 0], distance);
        Assert.All(RasterMath.DistanceTransform([true, true], 2, 1), value => Assert.Equal(6, value));
        Assert.Equal([1, 1, 1, 1], RasterMath.BoxBlur([1, 1, 1, 1], 2, 2, 3));
        Assert.Equal([0, 9], RasterMath.BoxBlur([0, 9], 2, 1, 0));
        Assert.Throws<ArgumentException>(() => RasterMath.BoxBlur([1], 2, 1, 1));
    }

    [Fact]
    public void GridsMapPositionsToRowMajorCells()
    {
        SpatialGrid2 grid = SpatialGrid2.FromBounds(new(0, 0), new(10, 5), 5);
        // Bounds are extended by 0.001 cells, so a boundary at zero also covers the cell below it.
        Assert.Equal((-1, 2, -1, 1), (grid.MinimumX, grid.MaximumX, grid.MinimumY, grid.MaximumY));
        Assert.Equal(12, grid.CellCount);
        Assert.Equal(10, grid.CellIndex(new(6, 5)));
        Assert.False(grid.TryCellIndex(new(-6, 0), out int index));
        Assert.Equal(-1, index);
        Assert.Throws<ArgumentOutOfRangeException>(() => grid.CellIndex(new(100, 0)));
        Assert.Throws<ArgumentException>(() => SpatialGrid2.FromBounds(new(1, 0), new(0, 1), 1));
    }

    [Fact]
    public void ShapesHaveExactBounds()
    {
        Assert.Equal(0.5f, ShapeGeometry.TriangleArea(Vector3.Zero, Vector3.UnitX, Vector3.UnitY));
        Aabb3 segment = ShapeGeometry.SegmentBounds(new(1, 0, 0), new(-1, 0, 0), 0.5f);
        Assert.Equal((new Vector3(-1.5f, -0.5f, -0.5f), new Vector3(1.5f, 0.5f, 0.5f)), (segment.Minimum, segment.Maximum));
        Aabb3 cylinder = ShapeGeometry.CylinderBounds(Vector3.Zero, new(0, 2, 0), 1);
        Assert.Equal((new Vector3(-1, 0, -1), new Vector3(1, 2, 1)), (cylinder.Minimum, cylinder.Maximum));
        Aabb3 box = ShapeGeometry.OppositeDiagonalBoxBounds(new(-1, -1, -1), new(1, 1, -1), new(1, -1, 1), new(-1, 1, 1));
        Assert.Equal((new Vector3(-1), new Vector3(1)), (box.Minimum, box.Maximum));
        Aabb3 cone = ShapeGeometry.ConeBounds(Vector3.Zero, Vector3.UnitX, 2, MathF.PI / 2);
        Assert.Equal(new Vector3(0, -2, -2), Round(cone.Minimum));
        Assert.Equal(new Vector3(2, 2, 2), Round(cone.Maximum));
        Assert.Throws<ArgumentException>(() => ShapeGeometry.ConeBounds(Vector3.Zero, Vector3.UnitX, -1, 0));
    }

    [Fact]
    public void MassPropertiesOfSolidsAndCombinations()
    {
        Assert.Equal(24, MassProperties.BoxVolume(new(2, 3, 4)));
        NumericAssert.Near(MassProperties.SphereVolume(1), 4 * Math.PI / 3, "Sphere volume");
        NumericAssert.Near(MassProperties.CapsuleVolume(1, 2), 2 * Math.PI + 4 * Math.PI / 3, "Capsule volume");
        Assert.Equal(new Vector3(25f / 12, 20f / 12, 13f / 12), MassProperties.BoxInertia(new(2, 3, 4)));
        Assert.Equal(new Vector3(0.4f), MassProperties.SphereInertia(1));
        Assert.Equal(new Vector3(1f / 4 + 4f / 12, 0.5f, 1f / 4 + 4f / 12), MassProperties.CylinderInertia(1, 2));
        Assert.Equal(Vector3.Zero, MassProperties.CapsuleInertia(0, 0));
        // Two point masses one unit either side of the origin along X.
        var (combined, center) = MassProperties.Combine([new(0, 0, 0, 1), new(0, 0, 0, 1)], [Vector3.Zero, Vector3.Zero],
            [Matrix4x4.CreateTranslation(-1, 0, 0), Matrix4x4.CreateTranslation(1, 0, 0)]);
        Assert.Equal(Vector3.Zero, center);
        Assert.Equal(new Vector4(0, 2, 2, 2), combined);
        Assert.Equal(new Vector3(1, 3, 2), MassProperties.RotateInertia(new(3, 1, 2), Matrix4x4.CreateRotationZ(MathF.PI / 2)) is var r ? Round(r) : default);
        Assert.Throws<ArgumentException>(() => MassProperties.Combine([new(0, 0, 0, -1)], [Vector3.Zero], [Matrix4x4.Identity]));
        Assert.Throws<ArgumentException>(() => MassProperties.Combine([Vector4.One], [Vector3.Zero], [Matrix4x4.CreateScale(2)]));
    }

    private static Vector3 Round(Vector3 value) => new(MathF.Round(value.X, 4) + 0f, MathF.Round(value.Y, 4) + 0f, MathF.Round(value.Z, 4) + 0f);
    private static Vector3 V3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
    private static void Near(Vector3 value, JsonElement expected, string label)
    {
        NumericAssert.Near(value.X, expected[0].GetDouble(), label);
        NumericAssert.Near(value.Y, expected[1].GetDouble(), label);
        NumericAssert.Near(value.Z, expected[2].GetDouble(), label);
    }
}
