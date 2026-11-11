using System.Numerics;
using CommonFury.Geometry;
using Xunit;

namespace CommonFury.Tests;

public sealed class VoxelRemeshingTests
{
    [Fact]
    public void SphereShellSitsAtTheOffset()
    {
        var (positions, indices) = Sphere(3, 1, Vector3.Zero);
        var shell = VoxelRemeshing.Shell(positions, indices, new(0.05f, 0.1f));
        AssertClosed(shell);
        AssertAtOffset(positions, indices, shell, 0.1f, 0.05f);
        // The source is a polyhedron slightly inside the unit sphere; the shell encloses it grown by the offset.
        double expected = 4 / 3.0 * Math.PI * Math.Pow(0.99 + 0.1, 3);
        Assert.InRange(Volume(shell), expected * 0.95, expected * 1.05);
        var again = VoxelRemeshing.Shell(positions, indices, new(0.05f, 0.1f));
        Assert.Equal(shell.Positions, again.Positions);
        Assert.Equal(shell.Indices, again.Indices);
    }

    [Fact]
    public void SingleSidedSheetsBecomeClosedSlabs()
    {
        Vector3[] square = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];
        int[] indices = [0, 1, 2, 0, 2, 3];
        var shell = VoxelRemeshing.Shell(square, indices, new(0.05f, 0.05f));
        AssertClosed(shell);
        AssertAtOffset(square, indices, shell, 0.05f, 0.05f, MathF.Sqrt(3));
        // About the square's area times twice the offset, plus the rounded rim.
        Assert.InRange(Volume(shell), 0.09, 0.13);
    }

    [Fact]
    public void EnclosedSurfacesAreDropped()
    {
        var (outer, outerIndices) = Sphere(3, 1, Vector3.Zero);
        var (inner, innerIndices) = Sphere(2, 0.5f, Vector3.Zero);
        Vector3[] positions = [.. outer, .. inner];
        int[] indices = [.. outerIndices, .. innerIndices.Select(i => i + outer.Length)];
        var shell = VoxelRemeshing.Shell(positions, indices, new(0.05f, 0.1f));
        AssertClosed(shell);
        AssertAtOffset(outer, outerIndices, shell, 0.1f, 0.05f);
    }

    [Fact]
    public void GapsNarrowerThanTwiceTheOffsetClose()
    {
        // A unit box of single-sided faces whose lid floats 0.06 above the walls: the slit seals at an offset of 0.05.
        var (positions, indices) = Box(lidGap: 0.06f);
        var sealedShell = VoxelRemeshing.Shell(positions, indices, new(0.025f, 0.05f));
        AssertClosed(sealedShell);
        Assert.DoesNotContain(sealedShell.Positions, p => p.X is > 0.2f and < 0.8f && p.Y is > 0.2f and < 0.8f && p.Z is > 0.2f and < 0.8f);
        Assert.InRange(Volume(sealedShell), 1.0, 1.5);
        // At a smaller offset the slit stays open, the inside is outside, and the shell also lines the inner faces.
        var open = VoxelRemeshing.Shell(positions, indices, new(0.015f, 0.02f));
        AssertClosed(open);
        Assert.Contains(open.Positions, p => p.X is > 0.2f and < 0.8f && p.Y is > 0.2f and < 0.8f && p.Z is > 0.02f and < 0.5f);
        Assert.InRange(Volume(open), 0.1, 0.4);
    }

    [Fact]
    public void ShellsTouchingWithinAVoxelStayManifold()
    {
        // Two spheres along a diagonal, at gaps around where their shells meet: the grid sees them touch at points and slivers.
        var (sphere, sphereIndices) = Sphere(3, 0.5f, Vector3.Zero);
        var diagonal = Vector3.Normalize(new Vector3(1, 1, 1));
        for (float gap = -0.04f; gap <= 0.04f; gap += 0.01f)
        {
            var far = sphere.Select(p => p + diagonal * (1.2f + gap));
            Vector3[] positions = [.. sphere, .. far];
            int[] indices = [.. sphereIndices, .. sphereIndices.Select(i => i + sphere.Length)];
            var shell = VoxelRemeshing.Shell(positions, indices, new(0.05f, 0.1f));
            AssertClosed(shell);
            AssertManifold(shell);
        }
    }

    [Fact]
    public void VerticesStayApartAtHighGridIndices()
    {
        // Unit voxels on whole-number positions: a sphere 40000 voxels out has poles whose shell crosses exactly at samples, where
        // floats are too coarse for a fixed small margin to keep neighbouring cells' vertices apart.
        var (sphere, sphereIndices) = Sphere(2, 2, Vector3.Zero);
        Vector3[] positions = [.. sphere, .. sphere.Select(p => p + new Vector3(40000, 0, 0))];
        int[] indices = [.. sphereIndices, .. sphereIndices.Select(i => i + sphere.Length)];
        var shell = VoxelRemeshing.Shell(positions, indices, new(1, 1));
        Assert.Equal(shell.Positions.Length, shell.Positions.Distinct().Count());
        AssertClosed(shell);
        AssertManifold(shell);
    }

    [Fact]
    public void ValidatesArguments()
    {
        var (positions, indices) = Sphere(1, 1, Vector3.Zero);
        Assert.Throws<ArgumentOutOfRangeException>(() => VoxelRemeshing.Shell(positions, indices, new(0, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VoxelRemeshing.Shell(positions, indices, new(float.NaN, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VoxelRemeshing.Shell(positions, indices, new(0.1f, 0.04f)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VoxelRemeshing.Shell(positions, indices, new(0.1f, float.PositiveInfinity)));
        Assert.Throws<ArgumentOutOfRangeException>(() => VoxelRemeshing.Shell(positions, indices, new(1e-5f, 1e-5f)));
        Assert.Throws<ArgumentException>(() => VoxelRemeshing.Shell(positions, [0, 1], new(0.1f, 0.1f)));
        Assert.Empty(VoxelRemeshing.Shell(positions, [], new(0.1f, 0.1f)).Indices);
    }

    // Every shell vertex lies about the offset away from the source surface; where the shell turns a sharp edge or corner around
    // the source, the vertex sits where the tangent planes meet, up to √2 or √3 times the offset away.
    private static void AssertAtOffset(Vector3[] positions, int[] indices, RemeshedMesh shell, float offset, float voxel, float corner = 1)
    {
        var tree = new TriangleBvh(positions, indices);
        foreach (var p in shell.Positions)
        {
            Assert.True(tree.FindNearest(p, 10, out var hit));
            Assert.InRange(hit.Distance, offset - 0.15f * voxel, offset * corner + 0.15f * voxel);
        }
    }

    // No edge is used by a single triangle, and the winding is consistent where edges are manifold.
    private static void AssertClosed(RemeshedMesh shell)
    {
        Assert.NotEmpty(shell.Indices);
        var directed = new Dictionary<(int, int), int>();
        for (int t = 0; t < shell.Indices.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                var edge = (shell.Indices[t + k], shell.Indices[t + (k + 1) % 3]);
                directed[edge] = directed.GetValueOrDefault(edge) + 1;
            }
        foreach (var ((a, b), count) in directed)
            Assert.Equal(count, directed.GetValueOrDefault((b, a)));
    }

    // Every edge, by position, joins exactly two triangles, and the triangles around every position form a single fan.
    private static void AssertManifold(RemeshedMesh shell)
    {
        var p = shell.Positions;
        var outgoing = new Dictionary<(Vector3, Vector3), int>();
        var around = new Dictionary<Vector3, List<(Vector3 Next, Vector3 Previous)>>();
        for (int t = 0; t < shell.Indices.Length; t += 3)
            for (int k = 0; k < 3; k++)
            {
                Vector3 a = p[shell.Indices[t + k]], b = p[shell.Indices[t + (k + 1) % 3]], c = p[shell.Indices[t + (k + 2) % 3]];
                outgoing[(a, b)] = outgoing.GetValueOrDefault((a, b)) + 1;
                if (!around.TryGetValue(a, out var fan))
                    around[a] = fan = [];
                fan.Add((b, c));
            }
        Assert.All(outgoing, edge => Assert.Equal(1, edge.Value));
        foreach (var (_, fan) in around)
        {
            var byNext = fan.ToDictionary(corner => corner.Next);
            var current = fan[0];
            for (int step = 1; step < fan.Count; step++)
            {
                current = byNext[current.Previous];
                Assert.False(current == fan[0], "A position joins two fans.");
            }
            Assert.Equal(fan[0], byNext[current.Previous]);
        }
    }

    private static double Volume(RemeshedMesh shell)
    {
        double volume = 0;
        for (int t = 0; t < shell.Indices.Length; t += 3)
            volume += Vector3.Dot(shell.Positions[shell.Indices[t]], Vector3.Cross(shell.Positions[shell.Indices[t + 1]], shell.Positions[shell.Indices[t + 2]])) / 6.0;
        return volume;
    }

    // Five walls of a unit box and a lid raised by lidGap, each face a separate quad.
    private static (Vector3[] Positions, int[] Indices) Box(float lidGap)
    {
        var positions = new List<Vector3>();
        var indices = new List<int>();
        void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d)
        {
            int start = positions.Count;
            positions.AddRange([a, b, c, d]);
            indices.AddRange([start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        Quad(new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0));
        Quad(new(0, 0, 0), new(0, 0, 1), new(1, 0, 1), new(1, 0, 0));
        Quad(new(0, 1, 0), new(1, 1, 0), new(1, 1, 1), new(0, 1, 1));
        Quad(new(0, 0, 0), new(0, 1, 0), new(0, 1, 1), new(0, 0, 1));
        Quad(new(1, 0, 0), new(1, 0, 1), new(1, 1, 1), new(1, 1, 0));
        float z = 1 + lidGap;
        Quad(new(0, 0, z), new(0, 1, z), new(1, 1, z), new(1, 0, z));
        return ([.. positions], [.. indices]);
    }

    private static (Vector3[] Positions, int[] Indices) Sphere(int levels, float radius, Vector3 center)
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
        return ([.. positions.Select(p => center + p * radius)], [.. indices]);
    }
}
