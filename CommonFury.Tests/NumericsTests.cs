using System.Numerics;
using System.Text.Json;
using CommonFury.Binary;
using CommonFury.Numerics;
using Xunit;

namespace CommonFury.Tests;

public sealed class NumericsTests : IDisposable
{
    // Vectors produced independently of this library.
    private readonly JsonDocument reference = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ReferenceData", "numerics.json")));
    public void Dispose() => reference.Dispose();

    [Fact]
    public void QuaternionsMatchReferenceVectors()
    {
        foreach (JsonElement item in reference.RootElement.GetProperty("quaternions").EnumerateArray())
        {
            Quaternion q = QuaternionMath.FromEulerXyz(V3(item.GetProperty("angles")));
            Near(q, item.GetProperty("quaternion"), "Euler XYZ composition");
            Near(QuaternionMath.Nlerp(q, Q(item.GetProperty("end")), item.GetProperty("amount").GetSingle()), item.GetProperty("nlerp"), "Nlerp");
            Near(Vector3.Transform(V3(item.GetProperty("point")), q), item.GetProperty("rotated"), "Rotation");
            Near(QuaternionMath.ToEulerXyz(q), item.GetProperty("euler"), "Euler extraction");
        }
        Assert.True(QuaternionMath.Nlerp(default, default, float.NaN) == Quaternion.Identity, "Quaternion fallback.");
        Assert.True(QuaternionMath.Nlerp(Quaternion.Identity, -Quaternion.Identity, 0.5f) == Quaternion.Identity, "Shortest hemisphere.");
        Assert.ThrowsAny<ArgumentException>(() => QuaternionMath.NormalizeChecked(default));
        Assert.ThrowsAny<ArgumentException>(() => QuaternionMath.NormalizeChecked(new(float.NaN, 0, 0, 1)));
        for (int omitted = 0; omitted < 4; omitted++)
            NumericAssert.Near(QuaternionMath.Reconstruct(new(0.1f, 0.2f, 0.3f), omitted).Length(), 1, "Packed quaternion");
        NumericAssert.Near(QuaternionMath.AngularErrorDegrees(Quaternion.Identity, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)), 90, "Angular error");
        Quaternion[] sequence = [Quaternion.Identity, -Quaternion.Identity, Quaternion.Identity];
        QuaternionMath.MakeContinuous(sequence, sequence);
        Assert.True(sequence.All(q => q == Quaternion.Identity), "Quaternion continuity.");
    }

    [Fact]
    public void TransformsMatchReferenceVectors()
    {
        foreach (JsonElement item in reference.RootElement.GetProperty("transforms").EnumerateArray())
        {
            float[] m = item.GetProperty("matrix").EnumerateArray().Select(v => v.GetSingle()).ToArray();
            // Reference matrices are column-vector; System.Numerics is row-vector.
            Matrix4x4 transform = Matrix4x4.Transpose(new(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7], m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]));
            Vector3[] points = item.GetProperty("points").EnumerateArray().Select(V3).ToArray();
            Vector3[] normals = item.GetProperty("normals").EnumerateArray().Select(V3).ToArray();
            Vector4[] tangents = item.GetProperty("tangents").EnumerateArray().Select(V4).ToArray();
            TransformMath.Positions(points, transform, points);
            TransformMath.Normals(normals, transform, normals);
            TransformMath.Tangents(tangents, transform, tangents);
            for (int i = 0; i < points.Length; i++)
            {
                Near(points[i], item.GetProperty("expected_positions")[i], "Position transform");
                Near(normals[i], item.GetProperty("expected_normals")[i], "Normal inverse/pseudoinverse");
                Near(tangents[i], item.GetProperty("expected_tangents")[i], "Tangent handedness");
            }
        }
    }

    [Fact]
    public void BoundsEncloseTransformAndKeepEmptyBoxesEmpty()
    {
        Vector3[] cloud = [new(-2, 1, 4), new(6, -3, 0), new(1, 5, -8)];
        Aabb3 bounds = Aabb3.FromPoints(cloud);
        Assert.True(bounds.Minimum == new Vector3(-2, -3, -8) && bounds.Maximum == new Vector3(6, 5, 4), "Point bounds.");
        NumericAssert.Near(VectorMath.SphereRadius(new(2, 1, -2), cloud), Math.Sqrt(53), "Sphere radius");
        Aabb3 mirrored = bounds.Transform(Matrix4x4.CreateScale(-2, 3, 1) * Matrix4x4.CreateTranslation(1, 2, 3));
        Assert.True(mirrored.Minimum == new Vector3(-11, -7, -5) && mirrored.Maximum == new Vector3(5, 17, 7), "Mirrored bounds.");
        Assert.ThrowsAny<ArgumentException>(() => new Aabb3(new(1), new(-1)));
        Assert.ThrowsAny<ArgumentException>(() => Aabb3.FromPoints([]));
        Assert.Equal(Aabb3.Empty, Aabb3.Empty.Transform(Matrix4x4.CreateTranslation(1, 2, 3)));
        Assert.Equal(bounds, Aabb3.Empty.Merge(bounds));
        Assert.ThrowsAny<ArgumentException>(() => bounds.Transform(Matrix4x4.CreateScale(float.MaxValue)));
        var many = Enumerable.Range(0, 200).Select(i => new Vector3(i, -i, i % 7)).ToArray();
        Assert.Equal(new Aabb3(new(0, -199, 0), new(199, 0, 6)), Aabb3.FromPoints(many));
        many[150].Y = float.NaN;
        Assert.ThrowsAny<ArgumentException>(() => Aabb3.FromPoints(many));
    }

    [Fact]
    public void PackedBasisMatchesInstanceRegisters()
    {
        var matrix = new Matrix3x4(new(1, 2, 3, 10), new(4, 5, 6, 20), new(7, 8, 9, 30));
        Assert.Equal(TransformMath.FromInstanceRegisters([matrix.Row0, matrix.Row1, matrix.Row2]), TransformMath.FromPackedBasis(matrix));
        Assert.Equal(new Vector3(10, 20, 30), TransformMath.FromPackedBasis(matrix).Translation);
        byte[] bytes = new byte[48];
        var writer = new BinarySpanWriter(bytes, ByteOrder.Big);
        writer.WriteVector4(matrix.Row0);
        writer.WriteVector4(matrix.Row1);
        writer.WriteVector4(matrix.Row2);
        Assert.Equal(matrix, Matrix3x4.Read(bytes, ByteOrder.Big));
        Assert.Throws<InvalidDataException>(() => Matrix3x4.Read(bytes.AsSpan(0, 47)));
    }

    [Fact]
    public void RadiusScaleIncludesRotationNonuniformScaleAndShear()
    {
        var rotation = Matrix4x4.CreateRotationZ(.8f);
        Assert.InRange(TransformMath.RadiusScale(Matrix4x4.CreateScale(2, 3, 4) * rotation), 3.9999f, 4.0001f);
        var shear = Matrix4x4.Identity;
        shear.M12 = 1;
        float bound = TransformMath.RadiusScale(shear);
        Assert.Equal(MathF.Sqrt(3), bound);
        foreach (var axis in new[] { Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ, Vector3.Normalize(new Vector3(1, 1, 1)) })
            Assert.True(Vector3.TransformNormal(axis, shear).Length() <= bound);
        shear.M11 = float.NaN;
        Assert.Throws<ArgumentException>(() => TransformMath.RadiusScale(shear));
    }

    [Fact]
    public void HierarchiesComposeUnorderedParentsAndRejectCyclesWithoutRecursion()
    {
        var local = Enumerable.Repeat(Matrix4x4.CreateTranslation(1, 0, 0), 4096).ToArray();
        var parents = Enumerable.Range(1, 4096).ToArray();
        parents[^1] = -1;
        var world = TransformMath.AccumulateHierarchy(local, parents);
        Assert.Equal(4096, world[0].M41);
        Assert.Equal(1, world[^1].M41);
        Assert.Equal(1, local[0].M41);
        Assert.Throws<ArgumentException>(() => TransformMath.AccumulateHierarchy([Matrix4x4.Identity, Matrix4x4.Identity], [1, 0]));
        Assert.Throws<ArgumentException>(() => TransformMath.AccumulateHierarchy([Matrix4x4.Identity], [-2]));
        Assert.Throws<ArgumentException>(() => TransformMath.AccumulateHierarchy([Matrix4x4.Identity], [1]));
    }

    [Fact]
    public void TrsComposesScaleRotationTranslationAndRejectsDegenerateRotations()
    {
        Assert.Throws<ArgumentException>(() => TransformMath.CreateTrs(Vector3.Zero, new Quaternion(), Vector3.One));
        Assert.Throws<ArgumentException>(() => TransformMath.CreateTrs(new(float.NaN), Quaternion.Identity, Vector3.One));
        var transform = TransformMath.CreateTrs(new(3, 0, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), new(2));
        Near(Vector3.Transform(Vector3.UnitX, transform), new Vector3(3, 2, 0));
        // Unnormalized rotations with tiny or huge components are normalized in double precision.
        var scaled = TransformMath.CreateTrs(Vector3.Zero, new Quaternion(0, 0, 1e-10f, 1e-10f), Vector3.One);
        Assert.True(TransformMath.NearlyEquals(Matrix4x4.CreateRotationZ(MathF.PI / 2), scaled));
        Assert.True(TransformMath.IsRigid(TransformMath.CreateTrs(new(1, 2, 3), new Quaternion(0, 0, 3e37f, 3e37f), Vector3.One)));
        Assert.False(TransformMath.IsRigid(Matrix4x4.CreateScale(-1, 1, 1)));
        Assert.False(TransformMath.IsRigid(Matrix4x4.CreateScale(2)));
    }

    [Fact]
    public void FinitenessCoversEveryComponent()
    {
        Assert.True(VectorMath.IsFinite(Matrix4x4.Identity));
        var m = Matrix4x4.Identity;
        m.M43 = float.PositiveInfinity;
        Assert.False(VectorMath.IsFinite(m));
        Assert.False(VectorMath.IsFinite(new Vector2(0, float.NaN)));
        Assert.True(new Vector3D(1e300, 0, 0).IsFinite);
        Assert.False(new Vector3D(double.NaN, 0, 0).IsFinite);
        Assert.Equal(new Vector3(1, 2, 3), new Vector3D(1, 2, 3).ToVector3());
    }

    private static Vector3 V3(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle());
    private static Vector4 V4(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());
    private static Quaternion Q(JsonElement e) => new(e[0].GetSingle(), e[1].GetSingle(), e[2].GetSingle(), e[3].GetSingle());
    private static void Near(Vector3 actual, Vector3 expected)
    {
        NumericAssert.Near(actual.X, expected.X, "X");
        NumericAssert.Near(actual.Y, expected.Y, "Y");
        NumericAssert.Near(actual.Z, expected.Z, "Z");
    }
    private static void Near(Vector3 value, JsonElement expected, string label)
    {
        NumericAssert.Near(value.X, expected[0].GetDouble(), label);
        NumericAssert.Near(value.Y, expected[1].GetDouble(), label);
        NumericAssert.Near(value.Z, expected[2].GetDouble(), label);
    }
    private static void Near(Vector4 value, JsonElement expected, string label)
    {
        Near(new Vector3(value.X, value.Y, value.Z), expected, label);
        NumericAssert.Near(value.W, expected[3].GetDouble(), label);
    }
    private static void Near(Quaternion value, JsonElement expected, string label) => Near(new Vector4(value.X, value.Y, value.Z, value.W), expected, label);
}
