using System.Numerics;

namespace CommonFury.Geometry;

// The XY barycentric terms of one triangle, computed once for many point tests.
internal readonly struct BarycentricTriangle
{
    private readonly double bcY, cbX, caY, acX, denominator;
    private readonly float cX, cY;

    public BarycentricTriangle(Vector3 a, Vector3 b, Vector3 c)
    {
        bcY = (double)b.Y - c.Y;
        cbX = (double)c.X - b.X;
        caY = (double)c.Y - a.Y;
        acX = (double)a.X - c.X;
        denominator = bcY * (a.X - c.X) + cbX * (a.Y - c.Y);
        cX = c.X;
        cY = c.Y;
    }

    public bool IsDegenerate => Math.Abs(denominator) <= 1e-9;

    public bool Contains(Vector2 point, float tolerance, out double wa, out double wb, out double wc)
    {
        float x = point.X - cX, y = point.Y - cY;
        wa = (bcY * x + cbX * y) / denominator;
        wb = (caY * x + acX * y) / denominator;
        wc = 1 - wa - wb;
        return !(wa < -tolerance || wb < -tolerance || wc < -tolerance);
    }
}
