using System.Numerics;
using CommonFury.Geometry;
using Xunit;

namespace CommonFury.Tests;

public sealed class TriangleBvhTests
{
    [Fact]
    public void HitsOneTriangleOnEitherFace()
    {
        Vector3[] positions = [new(0, 0, 0), new(2, 0, 0), new(0, 2, 0)];
        var tree = new TriangleBvh(positions, [0, 1, 2]);
        Assert.True(tree.Raycast(new(0.5f, 0.25f, 3), -Vector3.UnitZ, 10, out var hit));
        Assert.Equal(0, hit.Triangle);
        Assert.Equal(3, hit.Distance, 5);
        AssertVector(new(0.625f, 0.25f, 0.125f), hit.Barycentric);
        Assert.True(tree.Raycast(new(0.5f, 0.25f, -1), Vector3.UnitZ, 10, out hit));
        Assert.Equal(1, hit.Distance, 5);
        Assert.False(tree.Raycast(new(0.5f, 0.25f, 3), -Vector3.UnitZ, 2.5f, out _));
        Assert.False(tree.Raycast(new(1.5f, 1.5f, 3), -Vector3.UnitZ, 10, out _));
        Assert.False(tree.Raycast(new(0.5f, 0.25f, 3), Vector3.UnitZ, 10, out _));

        Assert.True(tree.FindNearest(new(3, -1, 2), 10, out hit));
        Assert.Equal(MathF.Sqrt(6), hit.Distance, 5);
        AssertVector(new(0, 1, 0), hit.Barycentric);
        Assert.False(tree.FindNearest(new(3, -1, 2), 2, out _));
    }

    [Fact]
    public void MatchesBruteForceOnTriangleSoup()
    {
        var random = new Random(31);
        var positions = new Vector3[30000];
        for (int i = 0; i < positions.Length; i += 3)
        {
            var center = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 10;
            for (int k = 0; k < 3; k++)
                positions[i + k] = center + new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f) * 0.6f;
        }
        int[] indices = [.. Enumerable.Range(0, positions.Length)];
        var tree = new TriangleBvh(positions, indices);
        Assert.Equal(10000, tree.TriangleCount);
        for (int r = 0; r < 600; r++)
        {
            var origin = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 14 - new Vector3(2);
            var direction = Vector3.Normalize(new Vector3(random.NextSingle() - 0.5f, random.NextSingle() - 0.5f, random.NextSingle() - 0.5f));
            float limit = r % 3 == 0 ? 3 : 100;
            var (expectedTriangle, expectedDistance) = BruteRay(positions, origin, direction, limit);
            bool found = tree.Raycast(origin, direction, limit, out var hit);
            Assert.Equal(expectedTriangle >= 0, found);
            if (!found)
                continue;
            Assert.Equal(expectedTriangle, hit.Triangle);
            Assert.Equal(expectedDistance, hit.Distance, 3);
            var point = positions[hit.Triangle * 3] * hit.Barycentric.X + positions[hit.Triangle * 3 + 1] * hit.Barycentric.Y
                + positions[hit.Triangle * 3 + 2] * hit.Barycentric.Z;
            AssertVector(origin + direction * hit.Distance, point, 1e-3f);
        }
        for (int q = 0; q < 300; q++)
        {
            var point = new Vector3(random.NextSingle(), random.NextSingle(), random.NextSingle()) * 12 - Vector3.One;
            float expected = BruteNearest(positions, point);
            Assert.True(tree.FindNearest(point, 100, out var hit));
            Assert.Equal(expected, hit.Distance, 4);
            Assert.Equal(1, hit.Barycentric.X + hit.Barycentric.Y + hit.Barycentric.Z, 5);
        }
    }

    [Fact]
    public void HandlesEmptyDegenerateAndAxisAlignedInput()
    {
        var empty = new TriangleBvh([], []);
        Assert.False(empty.Raycast(Vector3.Zero, Vector3.UnitX, 10, out _));
        Assert.False(empty.FindNearest(Vector3.Zero, 10, out _));

        // A zero-area triangle is never hit by rays; many coincident triangles still build a usable tree.
        Vector3[] line = [new(0, 0, 0), new(1, 0, 0), new(2, 0, 0)];
        Assert.False(new TriangleBvh(line, [0, 1, 2]).Raycast(new(1, 0, 1), -Vector3.UnitZ, 10, out _));
        Vector3[] square = [new(0, 0, 0), new(1, 0, 0), new(1, 1, 0), new(0, 1, 0)];
        int[] repeated = [.. Enumerable.Repeat(new[] { 0, 1, 2, 0, 2, 3 }, 50).SelectMany(x => x)];
        var stacked = new TriangleBvh(square, repeated);
        Assert.True(stacked.Raycast(new(0.25f, 0.75f, 1), -Vector3.UnitZ, 10, out var hit));
        Assert.Equal(1, hit.Triangle);
        // Rays along an axis through box faces.
        Assert.True(stacked.Raycast(new(0.5f, 0.5f, -5), Vector3.UnitZ, 10, out hit));
        Assert.Equal(5, hit.Distance, 5);
    }

    [Fact]
    public void ValidatesArguments()
    {
        Vector3[] positions = [new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)];
        Assert.Throws<ArgumentException>(() => new TriangleBvh(positions, [0, 1]));
        Assert.Throws<ArgumentException>(() => new TriangleBvh(positions, [0, 1, 5]));
        Assert.Throws<ArgumentException>(() => new TriangleBvh([new(float.NaN), positions[1], positions[2]], [0, 1, 2]));
        var tree = new TriangleBvh(positions, [0, 1, 2]);
        Assert.Throws<ArgumentException>(() => tree.Raycast(Vector3.Zero, Vector3.Zero, 1, out _));
        Assert.Throws<ArgumentException>(() => tree.Raycast(new(float.PositiveInfinity), Vector3.UnitX, 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => tree.Raycast(Vector3.Zero, Vector3.UnitX, -1, out _));
        Assert.Throws<ArgumentException>(() => tree.FindNearest(new(float.NaN), 1, out _));
    }

    // Plane intersection and sub-triangle areas, independent of the tree's edge-based test.
    private static (int Triangle, float Distance) BruteRay(Vector3[] positions, Vector3 origin, Vector3 direction, float limit)
    {
        int best = -1;
        float bestDistance = limit;
        for (int t = 0; t < positions.Length / 3; t++)
        {
            Vector3 a = positions[t * 3], b = positions[t * 3 + 1], c = positions[t * 3 + 2];
            var normal = Vector3.Cross(b - a, c - a);
            float facing = Vector3.Dot(normal, direction);
            if (MathF.Abs(facing) < 1e-12f)
                continue;
            float distance = Vector3.Dot(normal, a - origin) / facing;
            if (distance < 0 || distance > bestDistance)
                continue;
            var p = origin + direction * distance;
            float area = normal.Length();
            float sum = Vector3.Cross(b - p, c - p).Length() + Vector3.Cross(c - p, a - p).Length() + Vector3.Cross(a - p, b - p).Length();
            if (sum > area * (1 + 1e-5f))
                continue;
            best = t;
            bestDistance = distance;
        }
        return (best, bestDistance);
    }

    // Projection onto the plane when it lands inside, otherwise the nearest of the three edges.
    private static float BruteNearest(Vector3[] positions, Vector3 point)
    {
        float best = float.MaxValue;
        for (int t = 0; t < positions.Length / 3; t++)
        {
            Vector3 a = positions[t * 3], b = positions[t * 3 + 1], c = positions[t * 3 + 2];
            var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
            var projected = point - normal * Vector3.Dot(point - a, normal);
            float area = Vector3.Cross(b - a, c - a).Length();
            float sum = Vector3.Cross(b - projected, c - projected).Length() + Vector3.Cross(c - projected, a - projected).Length()
                + Vector3.Cross(a - projected, b - projected).Length();
            float distance = sum <= area * (1 + 1e-5f) ? Vector3.Distance(point, projected)
                : MathF.Min(Segment(point, a, b), MathF.Min(Segment(point, b, c), Segment(point, c, a)));
            best = MathF.Min(best, distance);
        }
        return best;
    }

    private static float Segment(Vector3 p, Vector3 a, Vector3 b)
    {
        float t = Math.Clamp(Vector3.Dot(p - a, b - a) / Vector3.Dot(b - a, b - a), 0, 1);
        return Vector3.Distance(p, a + (b - a) * t);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual, float tolerance = 1e-5f) =>
        Assert.True(Vector3.Distance(expected, actual) <= tolerance, $"expected {expected}, got {actual}");
}
