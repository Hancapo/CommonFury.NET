using System.Numerics;
using CommonFury.Geometry;
using Xunit;

namespace CommonFury.Tests;

public sealed class MeshSimplificationTests
{
    [Fact]
    public void FlatGridCollapsesToItsCorners()
    {
        var (positions, indices) = Grid(16, 16, (_, _) => 0);
        var result = MeshSimplification.Simplify(positions, indices, new(0, 1e-4f));
        Assert.Equal(6, result.Indices.Length);
        Assert.True(result.Error < 1e-5f, $"error {result.Error}");
        var corners = result.Indices.Select(i => positions[i]).ToHashSet();
        Assert.Equal(new HashSet<Vector3> { new(0, 0, 0), new(1, 0, 0), new(0, 1, 0), new(1, 1, 0) }, corners);
        Assert.Equal(1, Area(positions, result.Indices), 4);
        Assert.All(Normals(positions, result.Indices), normal => Assert.True(normal.Z > 0));
    }

    [Fact]
    public void ClosedSurfacesStayClosedAndFacingOut()
    {
        var (positions, indices) = Sphere(4);
        int source = indices.Length / 3, target = source / 10;
        var result = MeshSimplification.Simplify(positions, indices, new(target, float.PositiveInfinity));
        Assert.InRange(result.Indices.Length / 3, target - 2, target);
        AssertClosed(positions, result.Indices);
        for (int t = 0; t < result.Indices.Length; t += 3)
        {
            Vector3 a = positions[result.Indices[t]], b = positions[result.Indices[t + 1]], c = positions[result.Indices[t + 2]];
            Assert.True(Vector3.Dot(Vector3.Cross(b - a, c - a), a + b + c) > 0, "A triangle faces inward.");
        }
        Assert.InRange(Volume(positions, result.Indices) / Volume(positions, indices), 0.9, 1.0);
        Assert.InRange(result.Error, 1e-4f, 0.2f);
    }

    [Fact]
    public void ErrorTargetBoundsTheReportedError()
    {
        var (positions, indices) = Sphere(4);
        var fine = MeshSimplification.Simplify(positions, indices, new(0, 0.005f));
        var coarse = MeshSimplification.Simplify(positions, indices, new(0, 0.05f));
        Assert.True(fine.Error <= 0.005f && coarse.Error <= 0.05f, $"{fine.Error} {coarse.Error}");
        Assert.True(fine.Indices.Length > coarse.Indices.Length);
        Assert.True(fine.Indices.Length < indices.Length);
        Assert.Equal(indices.Length, MeshSimplification.Simplify(positions, indices, new(0, 0)).Indices.Length);
        AssertClosed(positions, coarse.Indices);
    }

    [Fact]
    public void SeamsKeepEachSideOnItsOwnAttributes()
    {
        // A cube whose six faces have separate vertices and texture coordinates, so every cube edge is a seam.
        var (positions, indices, attributes, faceOf) = Cube(8);
        var result = MeshSimplification.Simplify(positions, indices, new(12, float.PositiveInfinity), attributes, [1, 1]);
        Assert.Equal(36, result.Indices.Length);
        for (int t = 0; t < result.Indices.Length; t += 3)
            Assert.True(faceOf[result.Indices[t]] == faceOf[result.Indices[t + 1]] && faceOf[result.Indices[t]] == faceOf[result.Indices[t + 2]],
                "A triangle mixes vertices of two faces.");
        AssertClosed(positions, result.Indices);
        Assert.Equal(6, Area(positions, result.Indices), 4);
        Assert.True(result.Error < 1e-4f, $"error {result.Error}");
    }

    [Fact]
    public void CarriedAttributesDoNotLockFacetedMeshes()
    {
        // Every triangle has its own three vertices with its face normal, as flat-shaded exports store them.
        var (shared, sharedIndices) = Grid(10, 10, (_, _) => 0);
        var (positions, indices, normals) = Facet(shared, sharedIndices);
        var flat = MeshSimplification.Simplify(positions, indices, new(0, 1e-4f), carriedAttributes: normals);
        Assert.Equal(6, flat.Indices.Length);

        var (sphere, sphereIndices) = Sphere(3);
        (positions, indices, normals) = Facet(sphere, sphereIndices);
        var result = MeshSimplification.Simplify(positions, indices, new(indices.Length / 30, float.PositiveInfinity), carriedAttributes: normals);
        Assert.InRange(result.Indices.Length / 3, indices.Length / 30 - 2, indices.Length / 30);
        AssertClosed(positions, result.Indices);
        // Each corner carries the stored normal nearest the one it started with, which still faces its triangle's side.
        for (int t = 0; t < result.Indices.Length; t += 3)
        {
            Vector3 a = positions[result.Indices[t]], b = positions[result.Indices[t + 1]], c = positions[result.Indices[t + 2]];
            var face = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            for (int k = 0; k < 3; k++)
            {
                int v = result.Indices[t + k];
                Assert.True(Vector3.Dot(face, new(normals[v * 3], normals[v * 3 + 1], normals[v * 3 + 2])) > 0, "A corner carries a reversed normal.");
            }
        }
        // Corners that never moved keep their own vertex.
        var unmoved = MeshSimplification.Simplify(positions, indices, new(indices.Length / 3, 0), carriedAttributes: normals);
        Assert.Equal(indices, unmoved.Indices);
    }

    [Fact]
    public void AttributeWeightsPreserveAttributeDetail()
    {
        // A flat grid whose second half carries a different attribute value.
        var (positions, indices) = Grid(16, 16, (_, _) => 0);
        float[] values = [.. positions.Select(p => p.X < 0.5f ? 0f : 1f)];
        var ignored = MeshSimplification.Simplify(positions, indices, new(0, 1e-3f), values, [0]);
        var weighted = MeshSimplification.Simplify(positions, indices, new(0, 1e-3f), values, [10]);
        Assert.Equal(6, ignored.Indices.Length);
        Assert.True(weighted.Indices.Length > 6);
        Assert.True(weighted.Error <= 1e-3f);
    }

    [Fact]
    public void LockedVerticesAndBordersStay()
    {
        var (positions, indices) = Grid(12, 12, (x, y) => 0.05f * MathF.Sin(x * 9) * MathF.Cos(y * 7));
        var border = Enumerable.Range(0, positions.Length).Where(i => positions[i].X is 0 or 1 || positions[i].Y is 0 or 1).ToHashSet();
        var locked = new bool[positions.Length];
        int interior = 6 * 13 + 6;
        locked[interior] = true;
        var result = MeshSimplification.Simplify(positions, indices, new(0, 0.02f, LockBorders: true), lockedVertices: locked);
        var kept = result.Indices.ToHashSet();
        Assert.Subset(kept, border);
        Assert.Contains(interior, kept);
        Assert.True(result.Indices.Length < indices.Length);
        // Collapses onto locked collinear border vertices may leave vertical slivers, but never folds or holes.
        Assert.All(Normals(positions, result.Indices), normal => Assert.True(normal.Z >= 0, $"A triangle flipped: {normal}"));
        Assert.Equal(1, Area2D(positions, result.Indices), 4);
    }

    [Fact]
    public void HeightFieldsDoNotFlip()
    {
        var (positions, indices) = Grid(24, 24, (x, y) => 0.2f * MathF.Sin(x * 11) * MathF.Sin(y * 13));
        var result = MeshSimplification.Simplify(positions, indices, new(20, float.PositiveInfinity));
        Assert.True(result.Indices.Length / 3 <= 60, $"{result.Indices.Length / 3} triangles");
        Assert.All(Normals(positions, result.Indices), normal => Assert.True(normal.Z > 0, "A triangle flipped."));
        Assert.InRange(Area2D(positions, result.Indices), 0.9, 1.0001);
    }

    [Fact]
    public void SmallClosedPartsDoNotFoldFlat()
    {
        // Collapsing any edge of a tetrahedron leaves two back-to-back triangles, which keep their normals within the flip angle.
        Vector3[] positions = [new(1, 1, 1), new(1, -1, -1), new(-1, 1, -1), new(-1, -1, 1)];
        int[] indices = [0, 1, 2, 0, 3, 1, 0, 2, 3, 1, 3, 2];
        Assert.True(Volume(positions, indices) > 0);
        var result = MeshSimplification.Simplify(positions, indices, new(0, float.PositiveInfinity));
        Assert.Equal(indices.Length, result.Indices.Length);
        Assert.Equal(Volume(positions, indices), Volume(positions, result.Indices), 5);
    }

    [Fact]
    public void PartsSharingOnePositionStayApart()
    {
        // Two spheres touching at one position: that position is not a single fan, so neither part may pull it inside.
        var (sphere, sphereIndices) = Sphere(3);
        var positions = sphere.Concat(sphere.Select(p => p + new Vector3(2, 0, 0))).ToArray();
        var indices = sphereIndices.Concat(sphereIndices.Select(i => i + sphere.Length)).ToArray();
        Assert.Equal(positions[0], positions[sphere.Length + 1]);
        var result = MeshSimplification.Simplify(positions, indices, new(40, float.PositiveInfinity));
        Assert.Contains(result.Indices, i => positions[i] == new Vector3(1, 0, 0));
        for (int t = 0; t < result.Indices.Length; t += 3)
        {
            var xs = result.Indices.AsSpan(t, 3).ToArray().Select(i => positions[i].X).ToArray();
            Assert.True(xs.All(x => x <= 1) || xs.All(x => x >= 1), "A triangle spans both parts.");
        }
        AssertClosed(positions, result.Indices);
    }

    [Fact]
    public void RemovesDegenerateTrianglesAndIsDeterministic()
    {
        var (positions, indices) = Sphere(2);
        int[] withDegenerate = [.. indices, 0, 0, 1, 2, 2, 2];
        var copy = (int[])withDegenerate.Clone();
        var kept = MeshSimplification.Simplify(positions, withDegenerate, new(withDegenerate.Length, 0));
        Assert.Equal(indices, kept.Indices);
        Assert.Equal(copy, withDegenerate);
        var first = MeshSimplification.Simplify(positions, indices, new(30, float.PositiveInfinity));
        var second = MeshSimplification.Simplify(positions, indices, new(30, float.PositiveInfinity));
        Assert.Equal(first.Indices, second.Indices);
        Assert.Equal(first.Error, second.Error);
        Assert.Empty(MeshSimplification.Simplify(positions, [], new(0, 1)).Indices);
    }

    [Fact]
    public void ValidatesArguments()
    {
        var (positions, indices) = Grid(2, 2, (_, _) => 0);
        Assert.Throws<ArgumentException>(() => MeshSimplification.Simplify(positions, [0, 1], new(0, 1)));
        Assert.Throws<ArgumentException>(() => MeshSimplification.Simplify(positions, [0, 1, 99], new(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshSimplification.Simplify(positions, indices, new(-1, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshSimplification.Simplify(positions, indices, new(0, float.NaN)));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshSimplification.Simplify(positions, indices, new(0, -1)));
        Assert.Throws<ArgumentException>(() => MeshSimplification.Simplify(positions, indices, new(0, 1), new float[positions.Length - 1], [1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshSimplification.Simplify(positions, indices, new(0, 1), new float[positions.Length], [-1]));
        Assert.Throws<ArgumentOutOfRangeException>(() => MeshSimplification.Simplify(positions, indices, new(0, 1),
            new float[positions.Length * 33], new float[33]));
        Assert.Throws<ArgumentException>(() => MeshSimplification.Simplify(positions, indices, new(0, 1), lockedVertices: new bool[1]));
        var bad = (Vector3[])positions.Clone();
        bad[0] = new(float.NaN);
        Assert.Throws<ArgumentException>(() => MeshSimplification.Simplify(bad, indices, new(0, 1)));
        Assert.Throws<ArgumentException>(() => MeshSimplification.Simplify(positions, indices, new(0, 1), [.. new float[positions.Length - 1], float.NaN], [1]));
    }

    // A unit square in XY with n x m cells, two counter-clockwise triangles per cell.
    private static (Vector3[] Positions, int[] Indices) Grid(int n, int m, Func<float, float, float> height)
    {
        var positions = new List<Vector3>();
        for (int y = 0; y <= m; y++)
            for (int x = 0; x <= n; x++)
                positions.Add(new((float)x / n, (float)y / m, height((float)x / n, (float)y / m)));
        var indices = new List<int>();
        for (int y = 0; y < m; y++)
            for (int x = 0; x < n; x++)
            {
                int i = y * (n + 1) + x;
                indices.AddRange([i, i + 1, i + n + 2, i, i + n + 2, i + n + 1]);
            }
        return ([.. positions], [.. indices]);
    }

    // A unit sphere from a subdivided octahedron, outward-facing and watertight.
    private static (Vector3[] Positions, int[] Indices) Sphere(int levels)
    {
        var positions = new List<Vector3> { Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ };
        var indices = new List<int> { 0, 2, 4, 2, 1, 4, 1, 3, 4, 3, 0, 4, 2, 0, 5, 1, 2, 5, 3, 1, 5, 0, 3, 5 };
        for (int level = 0; level < levels; level++)
        {
            var midpoints = new Dictionary<(int, int), int>();
            int Midpoint(int a, int b)
            {
                var key = a < b ? (a, b) : (b, a);
                if (!midpoints.TryGetValue(key, out int index))
                {
                    index = positions.Count;
                    positions.Add(Vector3.Normalize(positions[a] + positions[b]));
                    midpoints[key] = index;
                }
                return index;
            }
            var next = new List<int>();
            for (int t = 0; t < indices.Count; t += 3)
            {
                int a = indices[t], b = indices[t + 1], c = indices[t + 2], ab = Midpoint(a, b), bc = Midpoint(b, c), ca = Midpoint(c, a);
                next.AddRange([a, ab, ca, ab, b, bc, ca, bc, c, ab, bc, ca]);
            }
            indices = next;
        }
        return ([.. positions], [.. indices]);
    }

    // A unit cube of six separately indexed n x n faces whose texture coordinates lie in disjoint charts.
    private static (Vector3[] Positions, int[] Indices, float[] Attributes, int[] FaceOf) Cube(int n)
    {
        var positions = new List<Vector3>();
        var attributes = new List<float>();
        var faceOf = new List<int>();
        var indices = new List<int>();
        Vector3[] normals = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY, Vector3.UnitZ, -Vector3.UnitZ];
        for (int face = 0; face < 6; face++)
        {
            Vector3 normal = normals[face];
            Vector3 u = MathF.Abs(normal.Z) > 0.5f ? Vector3.UnitX : Vector3.UnitZ;
            Vector3 v = Vector3.Cross(normal, u);
            int start = positions.Count;
            for (int y = 0; y <= n; y++)
                for (int x = 0; x <= n; x++)
                {
                    float s = (float)x / n, t = (float)y / n;
                    positions.Add(normal * 0.5f + u * (s - 0.5f) + v * (t - 0.5f));
                    attributes.AddRange([s + 2 * face, t]);
                    faceOf.Add(face);
                }
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    int i = start + y * (n + 1) + x;
                    indices.AddRange([i, i + 1, i + n + 2, i, i + n + 2, i + n + 1]);
                }
        }
        return ([.. positions], [.. indices], [.. attributes], [.. faceOf]);
    }

    // Splits every triangle into its own vertices and gives each the triangle's unit normal.
    private static (Vector3[] Positions, int[] Indices, float[] Normals) Facet(Vector3[] positions, int[] indices)
    {
        var split = new Vector3[indices.Length];
        var normals = new float[indices.Length * 3];
        for (int t = 0; t < indices.Length; t += 3)
        {
            var normal = Vector3.Normalize(Vector3.Cross(positions[indices[t + 1]] - positions[indices[t]], positions[indices[t + 2]] - positions[indices[t]]));
            for (int k = 0; k < 3; k++)
            {
                split[t + k] = positions[indices[t + k]];
                normal.CopyTo(normals, (t + k) * 3);
            }
        }
        return (split, [.. Enumerable.Range(0, indices.Length)], normals);
    }

    // Every directed edge, by position, has its opposite.
    private static void AssertClosed(Vector3[] positions, int[] indices)
    {
        var edges = new HashSet<(Vector3, Vector3)>();
        for (int t = 0; t < indices.Length; t += 3)
            for (int k = 0; k < 3; k++)
                edges.Add((positions[indices[t + k]], positions[indices[t + (k + 1) % 3]]));
        Assert.All(edges, edge => Assert.Contains((edge.Item2, edge.Item1), edges));
    }

    private static IEnumerable<Vector3> Normals(Vector3[] positions, int[] indices)
    {
        for (int t = 0; t < indices.Length; t += 3)
            yield return Vector3.Cross(positions[indices[t + 1]] - positions[indices[t]], positions[indices[t + 2]] - positions[indices[t]]);
    }

    private static double Area(Vector3[] positions, int[] indices) => Normals(positions, indices).Sum(normal => normal.Length() / 2.0);

    private static double Area2D(Vector3[] positions, int[] indices) => Normals(positions, indices).Sum(normal => normal.Z / 2.0);

    private static double Volume(Vector3[] positions, int[] indices)
    {
        double volume = 0;
        for (int t = 0; t < indices.Length; t += 3)
            volume += Vector3.Dot(positions[indices[t]], Vector3.Cross(positions[indices[t + 1]], positions[indices[t + 2]])) / 6.0;
        return volume;
    }
}
