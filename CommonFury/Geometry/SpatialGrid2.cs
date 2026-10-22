using System.Numerics;

namespace CommonFury.Geometry;

/// <summary>Maps XY positions to cells of an integer grid, row-major with X varying fastest.</summary>
public readonly record struct SpatialGrid2(int MinimumX, int MaximumX, int MinimumY, int MaximumY, float CellSizeX, float CellSizeY)
{
    public int Width => checked(MaximumX - MinimumX + 1);
    public int Height => checked(MaximumY - MinimumY + 1);
    public long CellCount => (long)Width * Height;

    /// <summary>A grid covering the bounds, extended by 0.001 cells so boundary points are included.</summary>
    public static SpatialGrid2 FromBounds(Vector2 minimum, Vector2 maximum, float cellSize)
    {
        if (!float.IsFinite(cellSize) || cellSize <= 0 || !float.IsFinite(minimum.X) || !float.IsFinite(minimum.Y) ||
            !float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y) || minimum.X > maximum.X || minimum.Y > maximum.Y)
            throw new ArgumentException("Grid bounds must be finite and ordered, and cell size positive.");
        float inverse = 1 / cellSize;
        return new(checked((int)MathF.Floor(minimum.X * inverse - .001f)), checked((int)MathF.Floor(maximum.X * inverse + .001f)),
            checked((int)MathF.Floor(minimum.Y * inverse - .001f)), checked((int)MathF.Floor(maximum.Y * inverse + .001f)), cellSize, cellSize);
    }

    public int CellIndex(Vector2 position)
    {
        if (!TryCellIndex(position, out int index))
            throw new ArgumentOutOfRangeException(nameof(position));
        return index;
    }

    /// <summary>Returns false, with index −1, for an invalid grid or a position outside it.</summary>
    public bool TryCellIndex(Vector2 position, out int index)
    {
        index = -1;
        if (!float.IsFinite(CellSizeX) || !float.IsFinite(CellSizeY) || CellSizeX <= 0 || CellSizeY <= 0)
            return false;
        float x = MathF.Floor(position.X * (1 / CellSizeX)), y = MathF.Floor(position.Y * (1 / CellSizeY));
        if (!float.IsFinite(x) || !float.IsFinite(y) || (double)x < MinimumX || (double)x > MaximumX || (double)y < MinimumY || (double)y > MaximumY)
            return false;
        long width = (long)MaximumX - MinimumX + 1, height = (long)MaximumY - MinimumY + 1;
        if (width is <= 0 or > int.MaxValue || height is <= 0 or > int.MaxValue)
            return false;
        long value = ((long)(int)y - MinimumY) * width + (long)(int)x - MinimumX;
        if (value > int.MaxValue)
            return false;
        index = (int)value;
        return true;
    }
}
