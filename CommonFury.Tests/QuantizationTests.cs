using System.Buffers.Binary;
using System.Numerics;
using CommonFury.Numerics;
using Xunit;

namespace CommonFury.Tests;

public sealed class QuantizationTests
{
    [Fact]
    public void LinearAndFixedPointGridsRoundTrip()
    {
        Assert.Equal(10u, Quantization.EncodeLinear(2.5f, 0.25f, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.EncodeLinear(4, 0.25f, 0, 4));
        Assert.Equal(2.5f, Quantization.DecodeLinear(10, 0.25f, 0));
        Span<ushort> encoded = stackalloc ushort[3];
        Quantization.EncodeFixedPosition16(new(0.5f, 1, 0), new(Vector3.Zero, Vector3.One), encoded);
        Assert.Equal((ushort)32768, encoded[0]);
        Assert.Equal(ushort.MaxValue, encoded[1]);
        Assert.Equal(0.5f, Quantization.DecodeFixedPosition16(32768, 0, 0, new(Vector3.Zero, Vector3.One)).X);
        Quantization.EncodePosition16(new(1, 0.5f, 0), new(Vector3.Zero, Vector3.One), encoded);
        Assert.Equal(new ushort[] { 65535, 32767, 0 }, encoded.ToArray());
        Assert.Equal(1, Quantization.DecodePosition16(65535, 0, 0, new(Vector3.Zero, Vector3.One)).X);
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.EncodePosition16(new(2, 0, 0), new(Vector3.Zero, Vector3.One), new ushort[3]));
        Assert.Equal((short)3, Quantization.Signed16(3.9f, 1));
        Assert.Equal((short)4, Quantization.Signed16Rounded(3.5f, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.Signed16(40000, 1));
        Assert.Equal(new Vector3(0, 0.5f, -1), Quantization.Snap(new(0.1f, 0.6f, -0.9f), 0.5f));
    }

    [Fact]
    public void AnglesAndBytesUseTheirDocumentedSteps()
    {
        Assert.Equal(0, Quantization.EncodeAngle8(MathF.Tau));
        Assert.Equal(128, Quantization.EncodeAngle8(MathF.PI + 0.01f));
        Assert.Equal(MathF.Tau, Quantization.DecodeAngle8(255), 5);
        Assert.Equal(255, Quantization.Unsigned8(2, 2));
        Assert.Equal(127, Quantization.Unsigned8(1, 2));
        Assert.Equal(2, Quantization.DecodeUnsigned8(255, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.Unsigned8(3, 2));
    }

    [Fact]
    public void PackedNormalsRejectOrClampOutsideTheUnitDisk()
    {
        Vector3 upright = Quantization.DecodePositiveZNormal(127, 127);
        Assert.Equal(1, upright.Length(), 5);
        Assert.True(upright.Z > 0.999f);
        Assert.Throws<ArgumentException>(() => Quantization.DecodePositiveZNormal(0, 255));
        Assert.Equal(new Vector3(-1, 1, .01f), Quantization.DecodePositiveZNormal(0, 255, .0001f));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.DecodePositiveZNormal(0, 0, float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.DecodePositiveZNormal(0, 0, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.DecodePositiveZNormal(0, 0, 2));
        Assert.True(Quantization.IsPositiveZNormal(128, 128));
        Assert.False(Quantization.IsPositiveZNormal(255, 255));
    }

    [Fact]
    public void ElevenElevenTenWordsDecodeWithOriginAndStep()
    {
        Assert.Equal(new Vector3(2047, 2047, 1023), Quantization.Decode111110(uint.MaxValue));
        Assert.Equal(0x12345678u, Quantization.Encode111110(Quantization.Decode111110(0x12345678)));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.Encode111110(new(0, 0, 1024)));
        Assert.Equal(new Vector3(2047, 2047, 1023), Assert.Single(Quantization.Decode111110Positions([255, 255, 255, 255], Vector3.Zero, 1)));
        Assert.Equal(new Vector3(10, 10, 10.5f), Assert.Single(Quantization.Decode111110Positions([1, 0, 0, 0], new(10), 0.5f)));
        Assert.Throws<ArgumentException>(() => Quantization.Decode111110Positions([1, 2, 3], Vector3.Zero, 1));
        Assert.Throws<InvalidDataException>(() => Quantization.Decode111110Positions([0, 0, 0, 0], new(float.PositiveInfinity), 1));
        Assert.Throws<InvalidDataException>(() => Quantization.Decode111110Positions([255, 255, 255, 255], Vector3.Zero, float.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => Quantization.Decode111110Positions([0, 0, 0, 0], Vector3.Zero, 0));
    }

    [Fact]
    public void SixteenBitTriplesDecodeInDoubleOrScaledSinglePrecision()
    {
        byte[] data = new byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(data, 65535);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), 1);
        // A far origin keeps sub-unit steps in double precision that single precision would lose.
        Vector3D far = Assert.Single(Quantization.DecodePositions16(data, new(1e7f, 0, 0), new Vector3D(1, 1, 65535)));
        Assert.Equal(new Vector3D(1e7 + 1, 0, 1), far);
        Assert.Equal(far, Quantization.DecodePosition16(65535, 0, 1, new(1e7f, 0, 0), new Vector3D(1, 1, 65535)));
        Assert.Throws<ArgumentException>(() => Quantization.DecodePositions16(new byte[5], Vector3.Zero, new Vector3D(1, 1, 1)));
        BinaryPrimitives.WriteInt16LittleEndian(data, -2);
        Vector3 signed = Assert.Single(Quantization.DecodeSignedPositions16(data, new(0.5f), new(1, 2, 3)));
        Assert.Equal(new Vector3(0, 2, 3.5f), signed);
    }

    [Fact]
    public void PackedVectorsRoundTripNormalizedComponents()
    {
        Vector4 value = new(0, 0.5f, 1, 0.2f);
        Assert.Equal(new Vector4(0, 128 / 255f, 1, 51 / 255f), PackedVectors.DecodeUNorm8(PackedVectors.EncodeUNorm8(value)));
        Assert.Equal(new Vector4(1, 0, 1, 1), PackedVectors.DecodeUNorm1010102(PackedVectors.EncodeUNorm1010102(new(1, 0, 1, 1))));
        Assert.Equal(new Vector4(-1, 0, 1, -1), PackedVectors.DecodeSNorm8(PackedVectors.EncodeSNorm8(new(-1, 0, 1, -1))));
        Assert.Equal(-1, PackedVectors.DecodeSNorm8(0x80).X);
        Assert.Throws<ArgumentOutOfRangeException>(() => PackedVectors.EncodeUNorm8(new(2, 0, 0, 0)));
        Assert.Throws<ArgumentOutOfRangeException>(() => PackedVectors.EncodeSNorm8(new(float.NaN, 0, 0, 0)));
    }

    [Fact]
    public void UNormEncodingRoundsHalfToEvenAndRejectsOutOfRange()
    {
        Assert.Equal(0u, PackedVectors.EncodeUNorm(0.5f, 1));
        Assert.Equal(2u, PackedVectors.EncodeUNorm(0.5f, 3));
        Assert.Equal(2u, PackedVectors.EncodeUNorm(0.5f, 5));
        Assert.Equal(128u, PackedVectors.EncodeUNorm(0.5f, 255));
        Assert.Equal(32768u, PackedVectors.EncodeUNorm(0.5f, 65535));
        Assert.Equal(0u, PackedVectors.EncodeUNorm(-0f, 65535));
        Assert.Equal(65535u, PackedVectors.EncodeUNorm(1, 65535));
        Assert.Equal((1u << 24) - 1, PackedVectors.EncodeUNorm(1, (1u << 24) - 1));
        foreach (float value in new[] { -float.Epsilon, MathF.BitIncrement(1), float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            Assert.Throws<ArgumentOutOfRangeException>(() => PackedVectors.EncodeUNorm(value, 255));
        Assert.Throws<ArgumentOutOfRangeException>(() => PackedVectors.EncodeUNorm(0.5f, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PackedVectors.EncodeUNorm(0.5f, 1u << 24));
    }

    [Fact]
    public void BoundsQuantizationRoundsOutwardAndMatchesBvhSteps()
    {
        Aabb3 rounded = BoundsQuantization.RoundOutward(new(new(0.3f, -0.3f, 0), new(1.2f, 0.2f, 0)), 2);
        Assert.Equal((new Vector3(0, -0.5f, 0), new Vector3(1.5f, 0.5f, 0)), (rounded.Minimum, rounded.Maximum));
        Aabb3 box = new(new(-1, 0, 2), new(1, 0, 6));
        Assert.Equal(new Vector3(2f / 65534, 1f / 32767, 4f / 65534), BoundsQuantization.Step(box));
        Assert.Equal(CommonFury.Collisions.BvhQuantization.FromSize(box).Quantum, BoundsQuantization.Step(box));
        Assert.Equal(short.MaxValue, BoundsQuantization.BoundCoordinate(1e9f, true));
        Assert.Equal((short)-2, BoundsQuantization.BoundCoordinate(-1.5f, false));
        Assert.True(BoundsQuantization.Contains(box, new(new(-1.1f, 0, 2), new(1, 0, 6)), new(0.2f)));
        Assert.False(BoundsQuantization.Contains(box, new(new(-1.1f, 0, 2), new(1, 0, 6)), Vector3.Zero));
    }

    [Fact]
    public void SkinningBatchReusesDestinationsAndPreservesZeroWeightVertices()
    {
        var batch = new SkinningBatch([Vector3.One, new(3, 2, 1)], [0, 1, 1, 0], [1, 1, 0, 0], 2, [new(0, 0, 2), new(0, 3, 0)]);
        var positions = new Vector3[2];
        var normals = new Vector3[2];
        Matrix4x4[] matrices = [Matrix4x4.CreateTranslation(2, 0, 0), Matrix4x4.CreateTranslation(0, 4, 0)];
        batch.Skin(matrices, positions, normals);
        Assert.Equal(new Vector3(2, 3, 1), positions[0]);
        Assert.Equal(new Vector3(3, 2, 1), positions[1]);
        Assert.Equal(Vector3.UnitZ, normals[0]);
        Assert.Equal(new Vector3(0, 3, 0), normals[1]);
        Assert.Throws<ArgumentException>(() => batch.Skin(matrices, positions, positions));
        Assert.Throws<ArgumentException>(() => batch.Skin([Matrix4x4.Identity], positions, normals));
        Assert.Equal(new Vector3(2, 3, 1), positions[0]);
    }

    [Fact]
    public void SkinningSupportsUnnormalizedAndMoreThanFourInfluences()
    {
        var batch = new SkinningBatch([new(2, 0, 0)], [0, 0, 0, 0, 0], [0.1f, 0.1f, 0.1f, 0.1f, 0.1f], 5, normalizeWeights: false);
        var output = new Vector3[1];
        batch.Skin([Matrix4x4.Identity], output);
        Assert.Equal(1, output[0].X, 5);
        batch.Skin([Matrix4x4.CreateTranslation(2, 0, 0)], output);
        Assert.Equal(2, output[0].X, 5);
    }

    [Fact]
    public void WeightSumAtMinimumHasNoInfluence()
    {
        const float minimum = SkinningMath.MinimumWeightSum;
        Assert.True(SkinningMath.WeightSum([minimum]) <= minimum);
        Assert.True(SkinningMath.WeightSum([minimum, minimum]) > minimum);
        Assert.Equal(0, SkinningMath.WeightSum([1, -1]));
        Assert.True(double.IsNaN(SkinningMath.WeightSum([1, float.NaN])));
        Matrix4x4[] transforms = [Matrix4x4.CreateTranslation(1, 0, 0)];
        Assert.Equal(Matrix4x4.Identity, SkinningMath.Blend([minimum], [0], transforms));
        Assert.Equal(transforms[0], SkinningMath.Blend([2 * minimum], [0], transforms));
    }

    [Fact]
    public void InfluencesNormalizeAndRadialWeightsFollowTheirFalloff()
    {
        var (weights, indices) = SkinningMath.NormalizeInfluences([new(3, 1), new(5, 2), new(3, 1), new(7, 0)], 2);
        Assert.Equal(new Vector4(0.5f, 0.5f, 0, 0), weights);
        Assert.Equal(new Vector4(3, 5, 0, 0), indices);
        Assert.Equal((new Vector4(1, 0, 0, 0), Vector4.Zero), SkinningMath.NormalizeInfluences([new(4, 0)]));
        Matrix4x4 blended = SkinningMath.Blend(new Vector4(1, 1, 0, 0), new Vector4(0, 1, 0, 0),
            [Matrix4x4.CreateTranslation(2, 0, 0), Matrix4x4.CreateTranslation(0, 2, 0)]);
        Assert.Equal(new Vector3(1, 1, 0), blended.Translation);
        Assert.Equal(Matrix4x4.Identity, SkinningMath.Blend(Vector4.Zero, Vector4.Zero, []));
        Assert.Equal(1, SkinningMath.RadialWeight(new(0.5f, 0, 0), Vector3.Zero, 1, 1, RadialFalloff.Constant));
        Assert.Equal(0.5f, SkinningMath.RadialWeight(new(0.5f, 0, 0), Vector3.Zero, 1, 1, RadialFalloff.Linear));
        Assert.Equal(0.5f, SkinningMath.RadialWeight(new(0.5f, 0, 0), Vector3.Zero, 1, 1, RadialFalloff.Smooth));
        Assert.Equal(0, SkinningMath.RadialWeight(new(2, 0, 0), Vector3.Zero, 1, 1, RadialFalloff.Constant));
        Assert.Throws<ArgumentOutOfRangeException>(() => SkinningMath.RadialWeight(Vector3.Zero, Vector3.Zero, 1, 1, (RadialFalloff)9));
    }

    [Fact]
    public void JointFramesCurvesAndIntegerGrids()
    {
        JointFrameMath.RequireOrthonormal(Matrix4x4.CreateRotationY(0.3f) * Matrix4x4.CreateTranslation(1, 2, 3));
        Assert.Throws<ArgumentException>(() => JointFrameMath.RequireOrthonormal(Matrix4x4.CreateScale(1, 1, -1)));
        var (first, second) = JointFrameMath.OrthogonalAxes(new(0, 0, 2));
        Assert.Equal(0, Vector3.Dot(first, Vector3.UnitZ), 6);
        Assert.Equal(0, Vector3.Dot(second, first), 6);
        Assert.Equal(1, second.Length(), 6);
        Matrix4x4 orientation = JointFrameMath.Orientation(Matrix4x4.CreateTranslation(1, 0, 0), new(3, 0, 0), Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ);
        Assert.Equal(Matrix4x4.Identity with { M41 = 2 }, orientation);
        Assert.Equal(Quaternion.Identity, JointFrameMath.LeanCenter(0, 0));
        Vector2[] keys = [new(0, 10), new(2, 20)];
        Assert.Equal(15, CurveMath.PiecewiseLinear(keys, 1));
        Assert.Equal(10, CurveMath.PiecewiseLinear(keys, -5));
        Assert.Equal(20, CurveMath.PiecewiseLinear(keys, 5));
        Assert.Equal(-3, IntegerGrid.Coordinate(-3.0000000001));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntegerGrid.Coordinate(2.5));
        Assert.Equal((-1, -2, 1, 2), IntegerGrid.CenteredRectangle(Vector2.Zero, new(2, 4)));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntegerGrid.CenteredRectangle(Vector2.Zero, new(1, 4)));
    }

    private static Vector3 Round(Vector3 value) => new(MathF.Round(value.X, 4) + 0f, MathF.Round(value.Y, 4) + 0f, MathF.Round(value.Z, 4) + 0f);
}
