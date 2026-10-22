using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Exact integer-grid coordinates; a fractional coordinate is rejected, never snapped.</summary>
public static class IntegerGrid
{
    /// <summary>Converts a value within <paramref name="tolerance"/> of an integer that fits Int32.</summary>
    public static int Coordinate(double value, double tolerance = 1e-9)
    {
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        double rounded = Math.Round(value);
        if (!double.IsFinite(value) || Math.Abs(value - rounded) > tolerance || rounded < int.MinValue || rounded > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(value), "Coordinate must fit the integer grid.");
        return (int)rounded;
    }

    /// <summary>Integer rectangle endpoints from a center and positive size; throws when an endpoint is off the grid.</summary>
    public static (int MinX, int MinY, int MaxX, int MaxY) CenteredRectangle(Vector2 center, Vector2 size)
    {
        if (!VectorMath.IsFinite(center) || !VectorMath.IsFinite(size) || size.X <= 0 || size.Y <= 0)
            throw new ArgumentException("Center and size must be finite, with positive size components.");
        return (Coordinate(center.X - (double)size.X / 2), Coordinate(center.Y - (double)size.Y / 2),
            Coordinate(center.X + (double)size.X / 2), Coordinate(center.Y + (double)size.Y / 2));
    }
}
