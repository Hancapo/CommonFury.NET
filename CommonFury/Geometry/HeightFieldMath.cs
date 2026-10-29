using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Height interpolation over the XY plane; Z is height.</summary>
public static class HeightFieldMath
{
    private const int MaximumBufferedSamples = 64;

    /// <summary>Interpolates a triangle's Z at an XY point. Returns false when the triangle's XY projection is degenerate
    /// or the point lies outside it by more than <paramref name="tolerance"/> (in barycentric units).</summary>
    public static bool TryTriangleHeight(Vector2 point, Vector3 a, Vector3 b, Vector3 c, out float height, float tolerance = 1e-5f)
    {
        VectorMath.RequireEpsilon(tolerance);
        if (!VectorMath.IsFinite(point) || !VectorMath.IsFinite(a) || !VectorMath.IsFinite(b) || !VectorMath.IsFinite(c))
            throw new ArgumentException("Height geometry must be finite.");
        var triangle = new BarycentricTriangle(a, b, c);
        height = 0;
        if (triangle.IsDegenerate || !triangle.Contains(point, tolerance, out double wa, out double wb, out double wc))
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
        int count = Math.Min(nearestCount, samples.Length);
        // Small counts keep the nearest samples in a sorted insertion buffer; large ones sort every sample.
        bool buffered = count <= MaximumBufferedSamples;
        Span<double> distances = buffered ? stackalloc double[count] : new double[count];
        Span<float> heights = buffered ? stackalloc float[count] : new float[count];
        var sorted = buffered ? null : new (double Distance, float Z, int Index)[samples.Length];
        int filled = 0, exact = -1;
        for (int i = 0; i < samples.Length; i++)
        {
            Vector3 sample = samples[i];
            if (!VectorMath.IsFinite(sample))
                throw new ArgumentException("Height samples must be finite.", nameof(samples));
            double dx = (double)sample.X - point.X, dy = (double)sample.Y - point.Y;
            double distance = dx * dx + dy * dy;
            if (exact < 0 && distance <= 1e-12)
                exact = i;
            if (sorted is not null)
                sorted[i] = (distance, sample.Z, i);
            else if (filled < count || distance < distances[count - 1])
            {
                // Equal distances stay in index order.
                int slot = filled < count ? filled++ : count - 1;
                for (; slot > 0 && distances[slot - 1] > distance; slot--)
                {
                    distances[slot] = distances[slot - 1];
                    heights[slot] = heights[slot - 1];
                }
                distances[slot] = distance;
                heights[slot] = sample.Z;
            }
        }
        if (exact >= 0)
            return samples[exact].Z;
        if (sorted is not null)
        {
            Array.Sort(sorted, (a, b) => a.Distance == b.Distance ? a.Index.CompareTo(b.Index) : a.Distance.CompareTo(b.Distance));
            for (int i = 0; i < count; i++)
                (distances[i], heights[i]) = (sorted[i].Distance, sorted[i].Z);
        }
        double weighted = 0, total = 0;
        double nearest = distances[0];
        for (int i = 0; i < count; i++)
        {
            double weight = Math.Pow(nearest / distances[i], power * 0.5);
            weighted += heights[i] * weight;
            total += weight;
        }
        return (float)(weighted / total);
    }
}
