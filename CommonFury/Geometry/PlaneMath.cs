using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>A fitted plane: in-plane axes, normal, and the center and half-size of the projected points.</summary>
public readonly record struct PlaneBasis(Vector3 X, Vector3 Y, Vector3 Normal, Vector3 Center, Vector3 Extent);

/// <summary>Plane fitting and UV-derived planar axes.</summary>
public static class PlaneMath
{
    /// <summary>Principal-component plane fit. The normal is flipped so its largest component is positive. The in-plane
    /// axes are <c>X = normalize(up × normal)</c> and <c>Y = X × normal</c>; when the normal is within 0.999 of
    /// <paramref name="up"/>, <paramref name="fallbackUp"/> is used instead. Center and extent are in the (X, Y, normal) frame.</summary>
    public static PlaneBasis Fit(ReadOnlySpan<Vector3> points, Vector3 up, Vector3 fallbackUp)
    {
        if (points.Length < 3)
            throw new ArgumentException("Plane fit requires three points.");
        up = VectorMath.NormalizeOr(up, Vector3.Zero);
        if (up == Vector3.Zero || !VectorMath.IsFinite(fallbackUp) || fallbackUp == Vector3.Zero)
            throw new ArgumentException("Up directions must be finite and nonzero.");
        Vector3 mean = default;
        foreach (var p in points)
        {
            if (!VectorMath.IsFinite(p))
                throw new ArgumentException("Points must be finite.");
            mean += p / points.Length;
        }
        var a = new double[3, 3];
        var v = new double[3, 3];
        for (int i = 0; i < 3; i++)
            v[i, i] = 1;
        foreach (var p in points)
        {
            Vector3 d = p - mean;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    a[i, j] += (double)d[i] * d[j];
        }
        // Jacobi eigenvalue iteration on the 3×3 covariance; the smallest eigenvector is the normal.
        for (int iteration = 0; iteration < 32; iteration++)
        {
            int p = 0, q = 1;
            if (Math.Abs(a[0, 2]) > Math.Abs(a[p, q]))
                (p, q) = (0, 2);
            if (Math.Abs(a[1, 2]) > Math.Abs(a[p, q]))
                (p, q) = (1, 2);
            if (Math.Abs(a[p, q]) < 1e-14)
                break;
            double angle = 0.5 * Math.Atan2(2 * a[p, q], a[q, q] - a[p, p]), c = Math.Cos(angle), s = Math.Sin(angle);
            for (int k = 0; k < 3; k++)
            {
                double x = a[k, p], y = a[k, q];
                a[k, p] = c * x - s * y;
                a[k, q] = s * x + c * y;
            }
            for (int k = 0; k < 3; k++)
            {
                double x = a[p, k], y = a[q, k];
                a[p, k] = c * x - s * y;
                a[q, k] = s * x + c * y;
            }
            for (int k = 0; k < 3; k++)
            {
                double x = v[k, p], y = v[k, q];
                v[k, p] = c * x - s * y;
                v[k, q] = s * x + c * y;
            }
        }
        int smallest = a[0, 0] <= a[1, 1] ? 0 : 1;
        if (a[2, 2] < a[smallest, smallest])
            smallest = 2;
        Vector3 normal = Vector3.Normalize(new((float)v[0, smallest], (float)v[1, smallest], (float)v[2, smallest]));
        int dominant = MathF.Abs(normal.X) >= MathF.Abs(normal.Y) ? 0 : 1;
        if (MathF.Abs(normal.Z) > MathF.Abs(normal[dominant]))
            dominant = 2;
        if (normal[dominant] < 0)
            normal = -normal;
        Vector3 reference = MathF.Abs(Vector3.Dot(normal, up)) > 0.999f ? fallbackUp : up;
        Vector3 xAxis = Vector3.Normalize(Vector3.Cross(reference, normal)), yAxis = Vector3.Cross(xAxis, normal);
        if (!VectorMath.IsFinite(xAxis))
            throw new ArgumentException("The fallback up direction is parallel to the fitted normal.", nameof(fallbackUp));
        var projected = new Vector3[points.Length];
        for (int i = 0; i < points.Length; i++)
            projected[i] = new(Vector3.Dot(points[i], xAxis), Vector3.Dot(points[i], yAxis), Vector3.Dot(points[i], normal));
        var bounds = Aabb3.FromPoints(projected);
        if (bounds.Size.X <= 2e-6f || bounds.Size.Y <= 2e-6f)
            throw new ArgumentException("Degenerate plane fit.");
        return new(xAxis, yAxis, normal, bounds.Center, bounds.Size * 0.5f);
    }

    /// <summary>World-space directions of +U and +V and the world position of UV (0, 0) for a triangle; throws for
    /// degenerate UVs.</summary>
    public static (Vector3 Width, Vector3 Height, Vector3 Origin) UvBasis(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 uv0, Vector2 uv1, Vector2 uv2)
    {
        if (!TryUvBasis(p0, p1, p2, uv0, uv1, uv2, out var basis))
            throw new ArgumentException("Degenerate UV triangle.");
        return basis;
    }

    /// <summary>Returns false for degenerate UVs or a basis that does not fit single precision; throws for nonfinite input.</summary>
    public static bool TryUvBasis(Vector3 p0, Vector3 p1, Vector3 p2, Vector2 uv0, Vector2 uv1, Vector2 uv2,
        out (Vector3 Width, Vector3 Height, Vector3 Origin) basis, float epsilon = 1e-12f)
    {
        VectorMath.RequireEpsilon(epsilon);
        Vector3 e1 = p1 - p0, e2 = p2 - p0;
        Vector2 d1 = uv1 - uv0, d2 = uv2 - uv0;
        // Large finite UVs can overflow a single-precision determinant before the division cancels their scale.
        double det = (double)d1.X * d2.Y - (double)d1.Y * d2.X;
        if (!VectorMath.IsFinite(e1) || !VectorMath.IsFinite(e2) || !VectorMath.IsFinite(d1) || !VectorMath.IsFinite(d2))
            throw new ArgumentException("UV triangle must be finite.");
        if (Math.Abs(det) <= epsilon)
        {
            basis = default;
            return false;
        }
        Vector3 width = new((float)(((double)d2.Y * e1.X - (double)d1.Y * e2.X) / det),
            (float)(((double)d2.Y * e1.Y - (double)d1.Y * e2.Y) / det),
            (float)(((double)d2.Y * e1.Z - (double)d1.Y * e2.Z) / det));
        Vector3 height = new((float)(((double)d1.X * e2.X - (double)d2.X * e1.X) / det),
            (float)(((double)d1.X * e2.Y - (double)d2.X * e1.Y) / det),
            (float)(((double)d1.X * e2.Z - (double)d2.X * e1.Z) / det));
        basis = (width, height, p0 - width * uv0.X - height * uv0.Y);
        if (!VectorMath.IsFinite(basis.Width) || !VectorMath.IsFinite(basis.Height) || !VectorMath.IsFinite(basis.Origin))
        {
            basis = default;
            return false;
        }
        return true;
    }
}
