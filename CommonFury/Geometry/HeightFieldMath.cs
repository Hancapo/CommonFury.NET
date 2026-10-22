using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Height interpolation over the XY plane; Z is height.</summary>
public static class HeightFieldMath
{
    /// <summary>Interpolates a triangle's Z at an XY point. Returns false when the triangle's XY projection is degenerate
    /// or the point lies outside it by more than <paramref name="tolerance"/> (in barycentric units).</summary>
    public static bool TryTriangleHeight(Vector2 point, Vector3 a, Vector3 b, Vector3 c, out float height, float tolerance = 1e-5f)
    {
        VectorMath.RequireEpsilon(tolerance);
        if (!VectorMath.IsFinite(point) || !VectorMath.IsFinite(a) || !VectorMath.IsFinite(b) || !VectorMath.IsFinite(c))
            throw new ArgumentException("Height geometry must be finite.");
        double denominator = ((double)b.Y - c.Y) * (a.X - c.X) + ((double)c.X - b.X) * (a.Y - c.Y);
        height = 0;
        if (Math.Abs(denominator) <= 1e-9)
            return false;
        double wa = (((double)b.Y - c.Y) * (point.X - c.X) + ((double)c.X - b.X) * (point.Y - c.Y)) / denominator;
        double wb = (((double)c.Y - a.Y) * (point.X - c.X) + ((double)a.X - c.X) * (point.Y - c.Y)) / denominator;
        double wc = 1 - wa - wb;
        if (wa < -tolerance || wb < -tolerance || wc < -tolerance)
            return false;
        height = (float)(wa * a.Z + wb * b.Z + wc * c.Z);
        return true;
    }

    /// <summary>Inverse-distance-weighted height from the <paramref name="nearestCount"/> nearest samples in XY.
    /// A sample at exactly the point returns its height (the first such sample when several coincide).</summary>
    public static float InterpolateSamples(Vector2 point, ReadOnlySpan<Vector3> samples, int nearestCount = 4, float power = 2)
    {
        if (!VectorMath.IsFinite(point) || samples.IsEmpty)
            throw new ArgumentException("A finite point and nonempty samples are required.");
        if (nearestCount <= 0 || !float.IsFinite(power) || power <= 0)
            throw new ArgumentOutOfRangeException(nameof(nearestCount));
        var distances = new (double Distance, float Z, int Index)[samples.Length];
        for (int i = 0; i < samples.Length; i++)
        {
            Vector3 sample = samples[i];
            if (!VectorMath.IsFinite(sample))
                throw new ArgumentException("Height samples must be finite.", nameof(samples));
            double dx = (double)sample.X - point.X, dy = (double)sample.Y - point.Y;
            distances[i] = (dx * dx + dy * dy, sample.Z, i);
        }
        foreach (var value in distances)
            if (value.Distance <= 1e-12)
                return value.Z;
        Array.Sort(distances, (a, b) => a.Distance == b.Distance ? a.Index.CompareTo(b.Index) : a.Distance.CompareTo(b.Distance));
        double weighted = 0, total = 0;
        double nearest = distances[0].Distance;
        for (int i = 0; i < Math.Min(nearestCount, distances.Length); i++)
        {
            double weight = Math.Pow(nearest / distances[i].Distance, power * 0.5);
            weighted += distances[i].Z * weight;
            total += weight;
        }
        return (float)(weighted / total);
    }
}
