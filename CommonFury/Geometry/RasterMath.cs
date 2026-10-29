using System.Numerics;

namespace CommonFury.Geometry;

/// <summary>Triangle coverage rasterization and scalar-grid filters. Grids are row-major.</summary>
public static class RasterMath
{
    private const int ColumnBlock = 8;

    /// <summary>Marks cells whose centers lie inside any triangle; points are in cell units.</summary>
    public static bool[] RasterizeTriangles(ReadOnlySpan<Vector2> points, ReadOnlySpan<int> indices, int width, int height)
    {
        MeshMath.ValidateTriangleIndices(indices, points.Length);
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        var mask = new bool[checked(width * height)];
        foreach (var p in points)
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
                throw new ArgumentException("Raster positions must be finite.");
        for (int i = 0; i < indices.Length; i += 3)
        {
            var a = points[indices[i]];
            var b = points[indices[i + 1]];
            var c = points[indices[i + 2]];
            double determinant = ((double)b.Y - c.Y) * (a.X - c.X) + ((double)c.X - b.X) * (a.Y - c.Y);
            if (Math.Abs(determinant) < 1e-12)
                continue;
            int x0 = (int)Math.Clamp(Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, width - 1);
            int x1 = (int)Math.Clamp(Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, width - 1);
            int y0 = (int)Math.Clamp(Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, height - 1);
            int y1 = (int)Math.Clamp(Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, height - 1);
            float bcY = b.Y - c.Y, caY = c.Y - a.Y;
            for (int y = y0; y <= y1; y++)
            {
                double uY = (c.X - b.X) * (y + 0.5 - c.Y), vY = (a.X - c.X) * (y + 0.5 - c.Y);
                for (int x = x0; x <= x1; x++)
                {
                    double u = (bcY * (x + 0.5 - c.X) + uY) / determinant;
                    double v = (caY * (x + 0.5 - c.X) + vY) / determinant;
                    if (u >= -1e-8 && v >= -1e-8 && u + v <= 1 + 1e-8)
                        mask[y * width + x] = true;
                }
            }
        }
        return mask;
    }

    /// <summary>Squared Euclidean distance from each cell to the nearest false cell (exact, separable).
    /// With no false cell, values are <c>width² + height² + 1</c>.</summary>
    public static double[] DistanceTransform(ReadOnlySpan<bool> mask, int width, int height)
    {
        if (width <= 0 || height <= 0 || mask.Length != checked(width * height))
            throw new ArgumentException("Invalid raster dimensions.");
        var result = new double[mask.Length];
        double infinity = (double)width * width + (double)height * height + 1;
        for (int i = 0; i < mask.Length; i++)
            result[i] = mask[i] ? infinity : 0;
        int longest = Math.Max(width, height);
        var output = new double[longest];
        var sites = new int[longest];
        var boundaries = new double[longest + 1];
        for (int y = 0; y < height; y++)
        {
            Span<double> row = result.AsSpan(y * width, width);
            Transform(row, output, sites, boundaries);
            output.AsSpan(0, width).CopyTo(row);
        }
        // Columns are gathered a block at a time so the grid is read and written along rows.
        int blockWidth = Math.Min(ColumnBlock, width);
        var columns = new double[blockWidth * height];
        for (int x0 = 0; x0 < width; x0 += blockWidth)
        {
            int count = Math.Min(blockWidth, width - x0);
            for (int y = 0; y < height; y++)
                for (int b = 0; b < count; b++)
                    columns[b * height + y] = result[y * width + x0 + b];
            for (int b = 0; b < count; b++)
            {
                Span<double> column = columns.AsSpan(b * height, height);
                Transform(column, output, sites, boundaries);
                output.AsSpan(0, height).CopyTo(column);
            }
            for (int y = 0; y < height; y++)
                for (int b = 0; b < count; b++)
                    result[y * width + x0 + b] = columns[b * height + y];
        }
        return result;
    }

    // One-dimensional squared distance by the lower envelope of parabolas; sites and boundaries are scratch.
    private static void Transform(ReadOnlySpan<double> input, Span<double> output, Span<int> sites, Span<double> boundaries)
    {
        int count = input.Length;
        int k = 0;
        sites[0] = 0;
        boundaries[0] = double.NegativeInfinity;
        boundaries[1] = double.PositiveInfinity;
        for (int q = 1; q < count; q++)
        {
            double separation;
            do
            {
                int p = sites[k];
                separation = ((input[q] + (double)q * q) - (input[p] + (double)p * p)) / (2 * q - 2 * p);
                if (separation > boundaries[k] || k == 0)
                    break;
                k--;
            } while (true);
            if (separation <= boundaries[k])
                separation = double.NegativeInfinity;
            sites[++k] = q;
            boundaries[k] = separation;
            boundaries[k + 1] = double.PositiveInfinity;
        }
        k = 0;
        for (int q = 0; q < count; q++)
        {
            while (boundaries[k + 1] < q)
                k++;
            int delta = q - sites[k];
            output[q] = (double)delta * delta + input[sites[k]];
        }
    }

    /// <summary>A new grid after <paramref name="passes"/> 3×3 box-blur passes, clamping at the edges.</summary>
    public static double[] BoxBlur(ReadOnlySpan<double> values, int width, int height, int passes)
    {
        if (width <= 0 || height <= 0 || values.Length != checked(width * height) || passes < 0)
            throw new ArgumentException("Invalid raster dimensions.");
        var current = values.ToArray();
        var next = new double[current.Length];
        for (int pass = 0; pass < passes; pass++)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                if (y == 0 || y == height - 1 || width < 3)
                {
                    for (int x = 0; x < width; x++)
                        next[row + x] = ClampedAverage(current, width, height, x, y);
                    continue;
                }
                next[row] = ClampedAverage(current, width, height, 0, y);
                // Interior cells read the same nine taps in the same order without clamping.
                for (int i = row + 1; i < row + width - 1; i++)
                {
                    int above = i - width, below = i + width;
                    double sum = 0;
                    sum += current[above - 1];
                    sum += current[above];
                    sum += current[above + 1];
                    sum += current[i - 1];
                    sum += current[i];
                    sum += current[i + 1];
                    sum += current[below - 1];
                    sum += current[below];
                    sum += current[below + 1];
                    next[i] = sum / 9;
                }
                next[row + width - 1] = ClampedAverage(current, width, height, width - 1, y);
            }
            (current, next) = (next, current);
        }
        return current;
    }

    private static double ClampedAverage(double[] values, int width, int height, int x, int y)
    {
        double sum = 0;
        for (int dy = -1; dy <= 1; dy++)
            for (int dx = -1; dx <= 1; dx++)
                sum += values[Math.Clamp(y + dy, 0, height - 1) * width + Math.Clamp(x + dx, 0, width - 1)];
        return sum / 9;
    }
}
