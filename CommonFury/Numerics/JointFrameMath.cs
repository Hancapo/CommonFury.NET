using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Orientation frames for physical joints.</summary>
public static class JointFrameMath
{
    /// <summary>Rejects matrices that are not finite, affine, right-handed and orthonormal within <paramref name="tolerance"/>.</summary>
    public static void RequireOrthonormal(Matrix4x4 transform, float tolerance = 1e-5f)
    {
        TransformMath.RequireFinite(transform);
        Vector3 x = new(transform.M11, transform.M12, transform.M13), y = new(transform.M21, transform.M22, transform.M23), z = new(transform.M31, transform.M32, transform.M33);
        if (MathF.Abs(x.LengthSquared() - 1) > tolerance || MathF.Abs(y.LengthSquared() - 1) > tolerance || MathF.Abs(z.LengthSquared() - 1) > tolerance ||
            MathF.Abs(Vector3.Dot(x, y)) > tolerance || MathF.Abs(Vector3.Dot(x, z)) > tolerance || MathF.Abs(Vector3.Dot(y, z)) > tolerance ||
            Vector3.Dot(Vector3.Cross(x, y), z) < 1 - tolerance || transform.M14 != 0 || transform.M24 != 0 || transform.M34 != 0 || transform.M44 != 1)
            throw new ArgumentException("Joint transforms must be right-handed orthonormal affine matrices.");
    }

    /// <summary>Two unit directions perpendicular to a finite, nonzero axis and to each other.</summary>
    public static (Vector3 First, Vector3 Second) OrthogonalAxes(Vector3 axis)
    {
        axis = Vector3.Normalize(axis);
        if (!VectorMath.IsFinite(axis))
            throw new ArgumentException("Axis must be nonzero and finite.");
        Vector3 first = MathF.Abs(axis.X) > 0.5f || MathF.Abs(axis.Y) > 0.5f ? new(axis.Y, -axis.X, 0) : new(0, axis.Z, -axis.Y);
        first = Vector3.Normalize(first);
        return (first, Vector3.Normalize(Vector3.Cross(axis, first)));
    }

    /// <summary>Joint orientation relative to its link, as a row-vector transform: the transposed link rotation times the
    /// axis frame, translated by the pivot offset rotated into the link.</summary>
    public static Matrix4x4 Orientation(Matrix4x4 linkWorld, Vector3 pivot, Vector3 first, Vector3 second, Vector3 axis)
    {
        RequireOrthonormal(linkWorld);
        var frame = new Matrix4x4(first.X, second.X, axis.X, 0, first.Y, second.Y, axis.Y, 0, first.Z, second.Z, axis.Z, 0, 0, 0, 0, 1);
        Matrix4x4 rotation = linkWorld;
        rotation.Translation = Vector3.Zero;
        Matrix4x4 result = Matrix4x4.Transpose(rotation) * frame;
        result.Translation = Vector3.TransformNormal(pivot - linkWorld.Translation, rotation);
        return result;
    }

    /// <summary>Normalized center orientation for two lean angles in radians.</summary>
    public static Quaternion LeanCenter(float first, float second)
    {
        float y = MathF.Tan(first * 0.25f), z = MathF.Tan(second * 0.25f), factor = 2 / (1 + y * y + z * z);
        return Quaternion.Normalize(new(factor * z, -factor * y, 0, factor - 1));
    }
}
