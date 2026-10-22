using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Transforms using System.Numerics row-vector matrices; transpose column-vector matrices first.</summary>
public static class TransformMath
{
    /// <summary>Per-component absolute plus right-relative tolerance. Nonfinite matrices never compare equal.</summary>
    public static bool NearlyEquals(Matrix4x4 left, Matrix4x4 right, float absoluteTolerance = 1e-5f, float relativeTolerance = 1e-5f)
    {
        if (!float.IsFinite(absoluteTolerance) || absoluteTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(absoluteTolerance));
        if (!float.IsFinite(relativeTolerance) || relativeTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(relativeTolerance));
        ReadOnlySpan<float> first = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(new ReadOnlySpan<Matrix4x4>(in left));
        ReadOnlySpan<float> second = System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(new ReadOnlySpan<Matrix4x4>(in right));
        for (int i = 0; i < first.Length; i++)
            if (!float.IsFinite(first[i]) || !float.IsFinite(second[i]) || MathF.Abs(first[i] - second[i]) > absoluteTolerance + relativeTolerance * MathF.Abs(second[i]))
                return false;
        return true;
    }

    /// <summary>Decodes three GPU instance registers: basis XYZ, with translation in the W lanes.</summary>
    public static Matrix4x4 FromInstanceRegisters(ReadOnlySpan<Vector4> rows)
    {
        if (rows.Length != 3)
            throw new ArgumentException("Instance transforms require three registers.", nameof(rows));
        Vector4 x = rows[0], y = rows[1], z = rows[2];
        return new(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, x.W, y.W, z.W, 1);
    }
    /// <summary>Packs an affine transform into three registers; the inverse of <see cref="FromInstanceRegisters"/>.</summary>
    public static void ToInstanceRegisters(Matrix4x4 transform, Span<Vector4> rows)
    {
        if (rows.Length != 3)
            throw new ArgumentException("Instance transforms require three registers.", nameof(rows));
        RequireFinite(transform);
        if (transform.M14 != 0 || transform.M24 != 0 || transform.M34 != 0 || transform.M44 != 1)
            throw new ArgumentException("Instance transforms must be affine.", nameof(transform));
        rows[0] = new(transform.M11, transform.M12, transform.M13, transform.M41);
        rows[1] = new(transform.M21, transform.M22, transform.M23, transform.M42);
        rows[2] = new(transform.M31, transform.M32, transform.M33, transform.M43);
    }
    /// <summary>Scale, then rotate, then translate. Rejects nonfinite input and rotations with length at most 1e-12.</summary>
    public static Matrix4x4 CreateTrs(Vector3 translation, Quaternion rotation, Vector3 scale)
    {
        if (!VectorMath.IsFinite(translation))
            throw new ArgumentException("Translation must be finite.", nameof(translation));
        if (!VectorMath.IsFinite(scale))
            throw new ArgumentException("Scale must be finite.", nameof(scale));
        Matrix4x4 result = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(QuaternionMath.NormalizeChecked(rotation))
            * Matrix4x4.CreateTranslation(translation);
        RequireFinite(result);
        return result;
    }

    /// <summary>Affine matrix from three basis rows and a translation.</summary>
    public static Matrix4x4 FromBasis(Vector3 x, Vector3 y, Vector3 z, Vector3 translation) => new(
        x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, translation.X, translation.Y, translation.Z, 1);

    /// <summary>Basis rows with translation in their W lanes, as in <see cref="FromInstanceRegisters"/>.</summary>
    public static Matrix4x4 FromPackedBasis(Matrix3x4 value) => FromBasis(
        new(value.Row0.X, value.Row0.Y, value.Row0.Z), new(value.Row1.X, value.Row1.Y, value.Row1.Z),
        new(value.Row2.X, value.Row2.Y, value.Row2.Z), new(value.Row0.W, value.Row1.W, value.Row2.W));

    /// <summary>Finite, orientation-preserving and rigid within 0.0001, with exact affine metadata lanes.</summary>
    public static bool IsRigid(Matrix4x4 m)
    {
        var x = new Vector3(m.M11, m.M12, m.M13);
        var y = new Vector3(m.M21, m.M22, m.M23);
        var z = new Vector3(m.M31, m.M32, m.M33);
        return VectorMath.IsFinite(m) && m.M14 == 0 && m.M24 == 0 && m.M34 == 0 && m.M44 == 1 &&
            MathF.Abs(x.LengthSquared() - 1) <= 1e-4f && MathF.Abs(y.LengthSquared() - 1) <= 1e-4f && MathF.Abs(z.LengthSquared() - 1) <= 1e-4f &&
            MathF.Abs(Vector3.Dot(x, y)) <= 1e-4f && MathF.Abs(Vector3.Dot(y, z)) <= 1e-4f && MathF.Abs(Vector3.Dot(x, z)) <= 1e-4f && m.GetDeterminant() >= 0;
    }

    /// <summary>Conservative radius multiplier for the linear part, including shear.</summary>
    public static float RadiusScale(Matrix4x4 transform)
    {
        if (!VectorMath.IsFinite(transform))
            throw new ArgumentException("Transform must be finite.", nameof(transform));
        var x = new Vector3(transform.M11, transform.M12, transform.M13);
        var y = new Vector3(transform.M21, transform.M22, transform.M23);
        var z = new Vector3(transform.M31, transform.M32, transform.M33);
        float xy = Math.Abs(Vector3.Dot(x, y)), xz = Math.Abs(Vector3.Dot(x, z)), yz = Math.Abs(Vector3.Dot(y, z));
        return MathF.Sqrt(Math.Max(x.LengthSquared() + xy + xz, Math.Max(y.LengthSquared() + xy + yz, z.LengthSquared() + xz + yz)));
    }

    /// <summary>Returns new object-space matrices (local * parent) for a forest; roots use parent -1.
    /// Rejects cycles, invalid parents and nonfinite results.</summary>
    public static Matrix4x4[] AccumulateHierarchy(ReadOnlySpan<Matrix4x4> local, ReadOnlySpan<int> parents)
    {
        if (local.Length != parents.Length)
            throw new ArgumentException("Matrix and parent counts disagree.");
        int count = local.Length;
        for (int i = 0; i < count; i++)
        {
            if (parents[i] < -1 || parents[i] >= count)
                throw new ArgumentException("Parent lies outside the hierarchy.", nameof(parents));
            if (!VectorMath.IsFinite(local[i]))
                throw new ArgumentException("Local matrix contains nonfinite components.", nameof(local));
        }
        var result = new Matrix4x4[count];
        var state = new byte[count];
        var chain = new List<int>();
        for (int start = 0; start < count; start++)
        {
            chain.Clear();
            int current = start;
            while (current != -1 && state[current] != 2)
            {
                if (state[current] == 1)
                    throw new ArgumentException("Transform hierarchy contains a cycle.", nameof(parents));
                state[current] = 1;
                chain.Add(current);
                current = parents[current];
            }
            for (int i = chain.Count - 1; i >= 0; i--)
            {
                int index = chain[i], parent = parents[index];
                result[index] = parent == -1 ? local[index] : local[index] * result[parent];
                if (!VectorMath.IsFinite(result[index]))
                    throw new ArgumentException("Accumulated transform is nonfinite.", nameof(local));
                state[index] = 2;
            }
        }
        return result;
    }

    /// <summary>Input and output may be the same span, but must not partially overlap.</summary>
    public static void Positions(ReadOnlySpan<Vector3> input, Matrix4x4 transform, Span<Vector3> output)
    {
        VectorMath.RequireOutput(input, output);
        RequireFinite(transform);
        for (int i = 0; i < input.Length; i++)
        {
            if (!VectorMath.IsFinite(input[i]))
                throw new ArgumentException("Positions must be finite.", nameof(input));
            output[i] = Vector3.Transform(input[i], transform);
        }
    }

    /// <summary>Uses the inverse-transpose; singular transforms use the pseudoinverse.</summary>
    public static void Normals(ReadOnlySpan<Vector3> input, Matrix4x4 transform, Span<Vector3> output, float epsilon = 1e-12f)
    {
        VectorMath.RequireOutput(input, output);
        RequireFinite(transform);
        VectorMath.RequireEpsilon(epsilon);
        Matrix4x4 linear = Linear(transform);
        Matrix4x4 inverse = Matrix4x4.Invert(linear, out Matrix4x4 exact) ? exact : MatrixMath.PseudoInverseLinear(linear);
        Matrix4x4 normal = Matrix4x4.Transpose(inverse);
        for (int i = 0; i < input.Length; i++)
            output[i] = VectorMath.NormalizeOr(Vector3.TransformNormal(input[i], normal), Vector3.UnitZ, epsilon);
    }

    /// <summary>Flips handedness (W) for reflecting transforms.</summary>
    public static void Tangents(ReadOnlySpan<Vector4> input, Matrix4x4 transform, Span<Vector4> output)
    {
        VectorMath.RequireOutput(input, output);
        RequireFinite(transform);
        Matrix4x4 linear = Linear(transform);
        float sign = linear.GetDeterminant() < 0 ? -1 : 1;
        for (int i = 0; i < input.Length; i++)
        {
            Vector4 value = input[i];
            if (!VectorMath.IsFinite(value))
                throw new ArgumentException("Tangents must be finite.", nameof(input));
            Vector3 direction = VectorMath.NormalizeOr(Vector3.TransformNormal(new(value.X, value.Y, value.Z), linear), Vector3.UnitX, 1e-12f);
            output[i] = new(direction, value.W * sign);
        }
    }

    /// <summary>The upper-left 3x3 part, without translation or projection.</summary>
    public static Matrix4x4 Linear(Matrix4x4 m) => new(m.M11, m.M12, m.M13, 0, m.M21, m.M22, m.M23, 0, m.M31, m.M32, m.M33, 0, 0, 0, 0, 1);
    public static void RequireFinite(Matrix4x4 m)
    {
        if (!VectorMath.IsFinite(m))
            throw new ArgumentException("Matrix must be finite.");
    }
}
