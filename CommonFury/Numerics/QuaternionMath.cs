using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Quaternion operations beyond System.Numerics. Euler angles are XYZ radians.</summary>
public static class QuaternionMath
{
    /// <summary>Shortest rotation between directions; antiparallel inputs use a deterministic perpendicular axis.</summary>
    public static Quaternion FromToDirection(Vector3 from, Vector3 to)
    {
        from = VectorMath.NormalizeOr(from, Vector3.UnitY);
        to = VectorMath.NormalizeOr(to, Vector3.UnitY);
        float dot = Math.Clamp(Vector3.Dot(from, to), -1, 1);
        if (dot < -0.999999f)
        {
            Vector3 axis = Vector3.Cross(from, MathF.Abs(from.X) < MathF.Abs(from.Z) ? Vector3.UnitX : Vector3.UnitZ);
            return Quaternion.CreateFromAxisAngle(VectorMath.NormalizeOr(axis, Vector3.UnitY), MathF.PI);
        }
        return NormalizeOr(new Quaternion(Vector3.Cross(from, to), 1 + dot), Quaternion.Identity);
    }
    /// <summary>Signed twist about <paramref name="axis"/> (normalized) that aligns the projected directions.</summary>
    public static Quaternion AlignTwist(Vector3 from, Vector3 to, Vector3 axis)
    {
        axis = VectorMath.NormalizeOr(axis, Vector3.UnitY);
        from -= axis * Vector3.Dot(from, axis);
        to -= axis * Vector3.Dot(to, axis);
        if (from.LengthSquared() <= 1e-12f || to.LengthSquared() <= 1e-12f)
            return Quaternion.Identity;
        from = Vector3.Normalize(from);
        to = Vector3.Normalize(to);
        return Quaternion.CreateFromAxisAngle(axis, MathF.Atan2(Vector3.Dot(axis, Vector3.Cross(from, to)), Math.Clamp(Vector3.Dot(from, to), -1, 1)));
    }
    /// <summary>Scales the rotation angle per lane, using the shortest signed half angle.</summary>
    public static Quaternion ScaleAngle(Quaternion value, Vector4 amount)
    {
        Vector3 xyz = new(value.X, value.Y, value.Z);
        float lengthSquared = xyz.LengthSquared();
        if (lengthSquared <= 1.1920929e-7f)
            return value;
        float angle = MathF.Acos(Math.Clamp(value.W, -1, 1));
        if (angle > MathF.PI / 2)
            angle -= MathF.PI;
        Vector4 angles = amount * angle, sin = Vector4.Sin(angles);
        xyz /= MathF.Sqrt(lengthSquared);
        return new(xyz.X * sin.X, xyz.Y * sin.Y, xyz.Z * sin.Z, MathF.Cos(angles.W));
    }
    /// <summary>Returns <paramref name="fallback"/> when the length is nonfinite or at most <paramref name="epsilon"/>.</summary>
    public static Quaternion NormalizeOr(Quaternion value, Quaternion fallback, float epsilon = 1e-12f)
    {
        VectorMath.RequireEpsilon(epsilon);
        double length = Length(value);
        if (!double.IsFinite(length) || length <= epsilon)
            return fallback;
        return new((float)(value.X / length), (float)(value.Y / length), (float)(value.Z / length), (float)(value.W / length));
    }

    /// <summary>Throws when the value is nonfinite or its length is at most <paramref name="epsilon"/>.</summary>
    public static Quaternion NormalizeChecked(Quaternion value, float epsilon = 1e-12f)
    {
        VectorMath.RequireEpsilon(epsilon);
        double length = Length(value);
        if (!double.IsFinite(length) || length <= epsilon)
            throw new ArgumentException("Quaternion must be finite and nonzero.", nameof(value));
        return NormalizeOr(value, Quaternion.Identity, epsilon);
    }

    private static double Length(Quaternion value) => Math.Sqrt((double)value.X * value.X + (double)value.Y * value.Y
        + (double)value.Z * value.Z + (double)value.W * value.W);

    /// <summary>Requires finite angles; the result is normalized.</summary>
    public static Quaternion FromEulerXyz(Vector3 radians)
    {
        if (!VectorMath.IsFinite(radians))
            throw new ArgumentException("Angles must be finite.", nameof(radians));
        // Composed as qz * qy * qx.
        Quaternion value = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, radians.Z)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitY, radians.Y)
            * Quaternion.CreateFromAxisAngle(Vector3.UnitX, radians.X);
        return NormalizeChecked(value);
    }

    /// <summary>A quaternion that cannot be normalized converts as identity.</summary>
    public static Vector3 ToEulerXyz(Quaternion value)
    {
        Quaternion q = NormalizeOr(value, Quaternion.Identity);
        return new(MathF.Atan2(2 * (q.W * q.X + q.Y * q.Z), 1 - 2 * (q.X * q.X + q.Y * q.Y)),
            MathF.Asin(Math.Clamp(2 * (q.W * q.Y - q.Z * q.X), -1, 1)),
            MathF.Atan2(2 * (q.W * q.Z + q.X * q.Y), 1 - 2 * (q.Y * q.Y + q.Z * q.Z)));
    }

    /// <summary>Rebuilds the nonnegative omitted component (XYZW index) from the other three.</summary>
    public static Quaternion Reconstruct(Vector3 components, int omittedComponent = 3)
    {
        if (omittedComponent is < 0 or > 3)
            throw new ArgumentOutOfRangeException(nameof(omittedComponent));
        if (!VectorMath.IsFinite(components))
            throw new ArgumentException("Components must be finite.", nameof(components));
        float missing = (float)Math.Sqrt(Math.Max(0, 1 - ((double)components.X * components.X + (double)components.Y * components.Y + (double)components.Z * components.Z)));
        return omittedComponent switch
        {
            0 => new(missing, components.X, components.Y, components.Z),
            1 => new(components.X, missing, components.Y, components.Z),
            2 => new(components.X, components.Y, missing, components.Z),
            _ => new(components.X, components.Y, components.Z, missing)
        };
    }

    /// <summary>Normalized shortest-path interpolation. <paramref name="amount"/> is clamped to [0, 1]; nonfinite counts as zero.
    /// An invalid start uses identity; an invalid end uses the normalized start.</summary>
    public static Quaternion Nlerp(Quaternion start, Quaternion end, float amount)
    {
        amount = float.IsFinite(amount) ? Math.Clamp(amount, 0, 1) : 0;
        start = NormalizeOr(start, Quaternion.Identity);
        end = NormalizeOr(end, start);
        if (Quaternion.Dot(start, end) < 0)
            end = -end;
        return NormalizeOr(start * (1 - amount) + end * amount, amount <= 0.5f ? start : end);
    }

    /// <summary>Normalizes and picks the sign with nonnegative W; invalid input becomes identity.</summary>
    public static Quaternion Canonicalize(Quaternion value)
    {
        value = NormalizeOr(value, Quaternion.Identity);
        return value.W < 0 ? -value : value;
    }

    /// <summary>Positive infinity for invalid or near-zero quaternions.</summary>
    public static float AngularErrorDegrees(Quaternion left, Quaternion right)
    {
        double product = Length(left) * Length(right);
        if (!double.IsFinite(product) || Length(left) <= 1e-12 || Length(right) <= 1e-12)
            return float.PositiveInfinity;
        double dot = (double)left.X * right.X + (double)left.Y * right.Y + (double)left.Z * right.Z + (double)left.W * right.W;
        return (float)(2 * Math.Acos(Math.Clamp(Math.Abs(dot) / product, 0, 1)) * 180 / Math.PI);
    }

    /// <summary>Normalizes each sample and flips signs so successive samples stay in one hemisphere.</summary>
    public static void MakeContinuous(ReadOnlySpan<Quaternion> input, Span<Quaternion> output)
    {
        VectorMath.RequireOutput(input, output);
        for (int i = 0; i < input.Length; i++)
        {
            Quaternion value = NormalizeChecked(input[i]);
            if (i > 0 && Quaternion.Dot(output[i - 1], value) < 0)
                value = -value;
            output[i] = value;
        }
    }
}
