using System.Numerics;

namespace CommonFury.Geometry;

// Texel-center rasterization of 2D triangles with a fill rule that gives a texel center on an edge shared by two triangles to
// exactly one of them, so adjacent triangles never cover a texel twice.
internal static class Raster
{
    public delegate void Visitor(int x, int y, Vector3 barycentric);

    // Visits texels (x, y) of a width x height grid whose centers (x + 0.5, y + 0.5) lie inside triangle abc, either winding, with
    // the barycentric weights of a, b and c at the center.
    public static void Triangle(Vector2 a, Vector2 b, Vector2 c, int width, int height, Visitor visit)
    {
        double area = Edge(a, b, c);
        if (area == 0 || !float.IsFinite(a.X + a.Y + b.X + b.Y + c.X + c.Y))
            return;
        bool flipped = area < 0;
        if (flipped)
        {
            (b, c) = (c, b);
            area = -area;
        }
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) - 0.5f));
        int x1 = Math.Min(width - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)) - 0.5f));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)) - 0.5f));
        int y1 = Math.Min(height - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)) - 0.5f));
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                var p = new Vector2(x + 0.5f, y + 0.5f);
                double wa = Edge(b, c, p), wb = Edge(c, a, p), wc = Edge(a, b, p);
                if (!Inside(wa, b, c) || !Inside(wb, c, a) || !Inside(wc, a, b))
                    continue;
                var weights = new Vector3((float)(wa / area), (float)(wb / area), (float)(wc / area));
                visit(x, y, flipped ? new(weights.X, weights.Z, weights.Y) : weights);
            }
    }

    private static double Edge(Vector2 a, Vector2 b, Vector2 p) => ((double)b.X - a.X) * ((double)p.Y - a.Y) - ((double)b.Y - a.Y) * ((double)p.X - a.X);

    // A center exactly on edge a→b counts only for one direction of the edge, so the triangle on the other side excludes it.
    private static bool Inside(double weight, Vector2 a, Vector2 b) =>
        weight > 0 || weight == 0 && (b.Y < a.Y || b.Y == a.Y && b.X > a.X);
}
