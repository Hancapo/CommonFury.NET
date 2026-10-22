using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Volumes and diagonal inertia per unit mass of uniform solids. Cylinders and capsules lie along Y.</summary>
public static class MassProperties
{
    /// <summary>Combines child masses (XYZ diagonal inertia, W mass) placed by row-vector orthonormal transforms, about
    /// <paramref name="center"/> or, when omitted, their mass-weighted center.</summary>
    public static (Vector4 InertiaAndMass, Vector3 Center) Combine(ReadOnlySpan<Vector4> masses, ReadOnlySpan<Vector3> centers, ReadOnlySpan<Matrix4x4> transforms, Vector3? center = null)
    {
        if (masses.Length != centers.Length || masses.Length != transforms.Length)
            throw new ArgumentException("Mass, center and transform counts differ.");
        float total = 0;
        Vector3 weighted = default;
        var world = new Vector3[masses.Length];
        for (int i = 0; i < masses.Length; i++)
        {
            if (!VectorMath.IsFinite(masses[i]) || Vector4.Min(masses[i], Vector4.Zero) != Vector4.Zero || !VectorMath.IsFinite(centers[i]))
                throw new ArgumentException("Mass properties must be finite and nonnegative.");
            JointFrameMath.RequireOrthonormal(transforms[i]);
            world[i] = Vector3.Transform(centers[i], transforms[i]);
            weighted += world[i] * masses[i].W;
            total += masses[i].W;
        }
        Vector3 origin = center ?? (total > 0 ? weighted / total : Vector3.Zero);
        Vector3 inertia = default;
        if (!VectorMath.IsFinite(origin) || !float.IsFinite(total))
            throw new ArgumentException("Combined mass exceeds finite storage.");
        for (int i = 0; i < masses.Length; i++)
            inertia += RotateInertia(new(masses[i].X, masses[i].Y, masses[i].Z), transforms[i]) + ParallelAxis(masses[i].W, world[i] - origin);
        if (!VectorMath.IsFinite(inertia))
            throw new ArgumentException("Combined inertia exceeds finite storage.");
        return (new(inertia, total), origin);
    }

    /// <param name="size">Full dimensions along X, Y and Z.</param>
    public static float BoxVolume(Vector3 size) => size.X * size.Y * size.Z;
    public static float SphereVolume(float radius) => (4f / 3f) * MathF.PI * radius * radius * radius;
    public static float CylinderVolume(float radius, float length) => MathF.PI * radius * radius * length;
    /// <summary>Includes both hemispherical ends; <paramref name="shaftLength"/> is the cylindrical part only.</summary>
    public static float CapsuleVolume(float radius, float shaftLength) => CylinderVolume(radius, shaftLength) + SphereVolume(radius);
    /// <param name="size">Full dimensions along X, Y and Z.</param>
    public static Vector3 BoxInertia(Vector3 size) => new((size.Y * size.Y + size.Z * size.Z) / 12,
        (size.X * size.X + size.Z * size.Z) / 12, (size.X * size.X + size.Y * size.Y) / 12);
    public static Vector3 SphereInertia(float radius) => new(0.4f * radius * radius);

    public static Vector3 CylinderInertia(float radius, float length)
    {
        float radial = radius * radius, cross = radial * 0.25f + length * length / 12;
        return new(cross, radial * 0.5f, cross);
    }

    /// <summary><paramref name="length"/> is the cylindrical shaft only.</summary>
    public static Vector3 CapsuleInertia(float radius, float length)
    {
        float divisor = length + 4f / 3f * radius;
        if (divisor == 0)
            return Vector3.Zero;
        float r2 = radius * radius, l2 = length * length;
        float axial = 0.5f * r2 * (length + 16f / 15f * radius) / divisor;
        float cross = (0.25f * length * l2 + radius * l2 + 2.25f * length * r2 + 1.6f * radius * r2) / (3 * divisor);
        return new(cross, axial, cross);
    }

    /// <summary>Diagonal of <c>R I Rᵀ</c> for a row-vector rotation; <paramref name="rotation"/> must be orthonormal.</summary>
    public static Vector3 RotateInertia(Vector3 diagonal, Matrix4x4 rotation) => new(
        diagonal.X * rotation.M11 * rotation.M11 + diagonal.Y * rotation.M21 * rotation.M21 + diagonal.Z * rotation.M31 * rotation.M31,
        diagonal.X * rotation.M12 * rotation.M12 + diagonal.Y * rotation.M22 * rotation.M22 + diagonal.Z * rotation.M32 * rotation.M32,
        diagonal.X * rotation.M13 * rotation.M13 + diagonal.Y * rotation.M23 * rotation.M23 + diagonal.Z * rotation.M33 * rotation.M33);

    /// <summary>Diagonal inertia added by moving <paramref name="mass"/> by <paramref name="offset"/> from the reference point.</summary>
    public static Vector3 ParallelAxis(float mass, Vector3 offset) => new(mass * (offset.Y * offset.Y + offset.Z * offset.Z),
        mass * (offset.X * offset.X + offset.Z * offset.Z), mass * (offset.X * offset.X + offset.Y * offset.Y));
}
