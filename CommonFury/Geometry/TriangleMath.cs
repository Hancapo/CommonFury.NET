using System.Numerics;

namespace CommonFury.Geometry;

internal static class TriangleMath
{
    // Barycentric weights of the point nearest p on the triangle with edges ab and ac from its first corner, p relative to that corner.
    public static Vector3 ClosestPointWeights(Vector3 p, Vector3 ab, Vector3 ac)
    {
        float d1 = Vector3.Dot(ab, p), d2 = Vector3.Dot(ac, p);
        if (d1 <= 0 && d2 <= 0)
            return new(1, 0, 0);
        Vector3 bp = p - ab;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3)
            return new(0, 1, 0);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float t = d1 / (d1 - d3);
            return new(1 - t, t, 0);
        }
        Vector3 cp = p - ac;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6)
            return new(0, 0, 1);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float t = d2 / (d2 - d6);
            return new(1 - t, 0, t);
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            float t = (d4 - d3) / (d4 - d3 + (d5 - d6));
            return new(0, 1 - t, t);
        }
        float denominator = 1 / (va + vb + vc);
        float v = vb * denominator, w = vc * denominator;
        return new(1 - v - w, v, w);
    }
}
