using System.Numerics;

namespace CommonFury.HeightMaps;

public sealed partial class HeightMap<T> where T : unmanaged, INumber<T>
{
    public int CellCount => MinimumCells.Length;
    /// <summary>World-space size of one cell along X and Y.</summary>
    public Vector2 CellSize => new((Bounds.Maximum.X - Bounds.Minimum.X) / Columns, (Bounds.Maximum.Y - Bounds.Minimum.Y) / Rows);
    public bool HasWaterMask => WaterCells is not null;

    /// <summary>Copies world-height grids indexed by row, column. Null pairs represent empty cells.</summary>
    public static HeightMap<T> FromHeightGrids(float?[,] minimum, HeightMapBounds bounds, float?[,]? maximum = null, bool[,]? water = null)
    {
        ArgumentNullException.ThrowIfNull(minimum);
        maximum ??= minimum;
        int rows = minimum.GetLength(0), columns = minimum.GetLength(1);
        if (maximum.GetLength(0) != rows || maximum.GetLength(1) != columns || water is not null && (water.GetLength(0) != rows || water.GetLength(1) != columns))
            throw new ArgumentException("All grids must have the same shape.");
        var result = new HeightMap<T>(columns, rows, bounds, water is not null);
        for (int row = 0; row < rows; row++)
            for (int column = 0; column < columns; column++)
            {
                if (minimum[row, column] is { } lower && maximum[row, column] is { } upper)
                    result.SetHeight(column, row, lower, upper);
                else if (minimum[row, column].HasValue || maximum[row, column].HasValue)
                    throw new ArgumentException("Each occupied cell needs both minimum and maximum heights.");
                if (water is not null)
                    result.waterCells![column + row * columns] = water[row, column];
            }
        return result;
    }

    private int Index(int column, int row)
    {
        if ((uint)column >= (uint)Columns || (uint)row >= (uint)Rows)
            throw new ArgumentOutOfRangeException(nameof(column), "Cell coordinates must lie within the grid.");
        return column + row * Columns;
    }

    /// <summary>Converts world coordinates to a cell. The maximum X/Y boundaries are exclusive.</summary>
    public bool TryGetCell(float x, float y, out int column, out int row)
    {
        column = row = -1;
        if (!(x >= Bounds.Minimum.X && x < Bounds.Maximum.X && y >= Bounds.Minimum.Y && y < Bounds.Maximum.Y))
            return false;
        column = Math.Min(Columns - 1, (int)Math.Floor(((double)x - Bounds.Minimum.X) * Columns / ((double)Bounds.Maximum.X - Bounds.Minimum.X)));
        row = Math.Min(Rows - 1, (int)Math.Floor(((double)y - Bounds.Minimum.Y) * Rows / ((double)Bounds.Maximum.Y - Bounds.Minimum.Y)));
        return true;
    }

    public bool TryGetCell(Vector2 position, out int column, out int row) => TryGetCell(position.X, position.Y, out column, out row);

    /// <summary>Returns the world-space cell rectangle as minimum and maximum corners.</summary>
    public (Vector2 Minimum, Vector2 Maximum) GetCellBounds(int column, int row)
    {
        _ = Index(column, row);
        Vector2 origin = new(Bounds.Minimum.X, Bounds.Minimum.Y);
        return (origin + new Vector2(column, row) * CellSize, origin + new Vector2(column + 1, row + 1) * CellSize);
    }

    public Vector2 GetCellCenter(int column, int row)
    {
        var rectangle = GetCellBounds(column, row);
        return rectangle.Minimum + (rectangle.Maximum - rectangle.Minimum) * 0.5f;
    }

    /// <summary>Reports whether both stored height samples are zero.</summary>
    public bool IsEmpty(int column, int row)
    {
        int index = Index(column, row);
        return MinimumCells[index] == T.Zero && MaximumCells[index] == T.Zero;
    }

    private T Quantize(float value, bool upper)
    {
        if (!float.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        if (typeof(T) == typeof(float))
            return T.CreateChecked(value);
        double limit = typeof(T) == typeof(byte) ? 255 : 65535;
        double sample = limit * ((double)value - Bounds.Minimum.Z) / ((double)Bounds.Maximum.Z - Bounds.Minimum.Z);
        return T.CreateChecked(Math.Clamp(upper ? Math.Ceiling(sample) : Math.Floor(sample), 0, limit));
    }

    private float Decode(T value) => typeof(T) == typeof(float) ? float.CreateChecked(value)
        : (float)(Bounds.Minimum.Z + ((double)Bounds.Maximum.Z - Bounds.Minimum.Z) * double.CreateChecked(value) / (typeof(T) == typeof(byte) ? 255 : 65535));

    /// <summary>Sets world heights, rounding integer minima down and maxima up and clamping to the vertical bounds.
    /// Two zero samples mean an empty cell, so an integer cell that would quantize to zero for both keeps a maximum of one step.
    /// Float cells store world heights directly; a cell at height zero for both is indistinguishable from an empty one.</summary>
    public void SetHeight(int column, int row, float minimum, float? maximum = null)
    {
        int index = Index(column, row);
        float upper = maximum ?? minimum;
        if (minimum > upper)
            throw new ArgumentException("Minimum height must not exceed maximum height.");
        T lowerCell = Quantize(minimum, false), upperCell = Quantize(upper, true);
        if (typeof(T) != typeof(float) && upperCell == T.Zero)
            upperCell = T.One;
        MinimumCells[index] = lowerCell;
        MaximumCells[index] = upperCell;
    }

    /// <summary>Sets heights at world coordinates; coordinates outside the map are rejected.</summary>
    public void SetHeightAt(float x, float y, float minimum, float? maximum = null)
    {
        if (!TryGetCell(x, y, out int column, out int row))
            throw new ArgumentOutOfRangeException(nameof(x));
        SetHeight(column, row, minimum, maximum);
    }

    /// <summary>Clears both heights and the water flag of a cell.</summary>
    public void ClearCell(int column, int row)
    {
        int index = Index(column, row);
        MinimumCells[index] = MaximumCells[index] = T.Zero;
        if (waterCells is not null)
            waterCells[index] = false;
    }

    /// <summary>Returns decoded world heights, or null when both samples are zero.</summary>
    public (float Minimum, float Maximum)? GetHeightRange(int column, int row)
    {
        int index = Index(column, row);
        return IsEmpty(column, row) ? null : (Decode(MinimumCells[index]), Decode(MaximumCells[index]));
    }

    /// <summary>Returns heights at world coordinates, or null outside the map or in an empty cell.</summary>
    public (float Minimum, float Maximum)? GetHeightRangeAt(float x, float y)
        => TryGetCell(x, y, out int column, out int row) ? GetHeightRange(column, row) : null;

    public (float Minimum, float Maximum)? GetHeightRangeAt(Vector2 position) => GetHeightRangeAt(position.X, position.Y);

    /// <summary>Aggregates occupied cells intersecting a world-space rectangle, clipping the query to the map.</summary>
    public (float Minimum, float Maximum)? GetHeightRangeInBounds(Vector2 minimum, Vector2 maximum)
    {
        if (!float.IsFinite(minimum.X) || !float.IsFinite(minimum.Y) || !float.IsFinite(maximum.X) || !float.IsFinite(maximum.Y) || minimum.X >= maximum.X || minimum.Y >= maximum.Y)
            throw new ArgumentException("Query bounds must be finite and ordered.");
        int Column(float x, bool upper) => (int)Math.Clamp(upper ? Math.Ceiling(((double)x - Bounds.Minimum.X) * Columns / ((double)Bounds.Maximum.X - Bounds.Minimum.X)) : Math.Floor(((double)x - Bounds.Minimum.X) * Columns / ((double)Bounds.Maximum.X - Bounds.Minimum.X)), 0, Columns);
        int Row(float y, bool upper) => (int)Math.Clamp(upper ? Math.Ceiling(((double)y - Bounds.Minimum.Y) * Rows / ((double)Bounds.Maximum.Y - Bounds.Minimum.Y)) : Math.Floor(((double)y - Bounds.Minimum.Y) * Rows / ((double)Bounds.Maximum.Y - Bounds.Minimum.Y)), 0, Rows);
        int firstRow = Row(minimum.Y, false), endRow = Row(maximum.Y, true);
        int firstColumn = Column(minimum.X, false), endColumn = Column(maximum.X, true);
        float lower = float.PositiveInfinity, upper = float.NegativeInfinity;
        for (int row = firstRow; row < endRow; row++)
            for (int index = row * Columns + firstColumn, end = row * Columns + endColumn; index < end; index++)
            {
                T low = MinimumCells[index], high = MaximumCells[index];
                if (low == T.Zero && high == T.Zero)
                    continue;
                lower = Math.Min(lower, Decode(low));
                upper = Math.Max(upper, Decode(high));
            }
        return float.IsPositiveInfinity(lower) ? null : (lower, upper);
    }

    /// <summary>Sets the cell's water flag, allocating the mask if needed.</summary>
    public void SetWater(int column, int row, bool enabled = true)
    {
        int index = Index(column, row);
        waterCells ??= new bool[CellCount];
        waterCells[index] = enabled;
    }

    /// <summary>Returns a cell's water flag, or false if there is no mask.</summary>
    public bool IsWater(int column, int row)
    {
        int index = Index(column, row);
        return waterCells?[index] ?? false;
    }

    /// <summary>Sets a water flag at world coordinates, rejecting coordinates outside the map.</summary>
    public void SetWaterAt(float x, float y, bool enabled = true)
    {
        if (!TryGetCell(x, y, out int column, out int row))
            throw new ArgumentOutOfRangeException(nameof(x));
        SetWater(column, row, enabled);
    }

    /// <summary>Returns false outside the map or where the water mask is unset.</summary>
    public bool IsWaterAt(float x, float y) => TryGetCell(x, y, out int column, out int row) && IsWater(column, row);

    /// <summary>Enumerates occupied cells in row-major order, reading the current editable samples.</summary>
    public IEnumerable<(int Column, int Row, float Minimum, float Maximum)> EnumerateOccupiedCells()
    {
        for (int row = 0; row < Rows; row++)
            for (int column = 0; column < Columns; column++)
                if (GetHeightRange(column, row) is { } range)
                    yield return (column, row, range.Minimum, range.Maximum);
    }
}
