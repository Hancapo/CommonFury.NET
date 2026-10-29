using System.Numerics;
using System.Runtime.InteropServices;
using CommonFury.Binary;
using CommonFury.Numerics;

namespace CommonFury.HeightMaps;

/// <summary>Flags indicating row-compressed height data and an optional water mask. Unknown bits are preserved.</summary>
[Flags]
public enum HeightMapFlags : uint
{
    /// <summary>Dense payload without a water mask.</summary>
    None = 0,
    /// <summary>The payload starts with a row table and stores only part of each row.</summary>
    RleData = 1,
    /// <summary>A one-bit-per-cell water mask follows the payload.</summary>
    WaterMask = 2
}

/// <summary>Storage layout of the cell payload.</summary>
public enum HeightMapLayout
{
    /// <summary>Row-compressed; each row stores the span between its first and last non-empty cell.</summary>
    TrimmedRows,
    /// <summary>Row-compressed, with every row stored at full width in row order.</summary>
    FullRows,
    /// <summary>Uncompressed; all maxima followed by all minima.</summary>
    Dense
}

/// <summary>World-space extents of a height grid.</summary>
public readonly record struct HeightMapBounds(Vector3 Minimum, Vector3 Maximum)
{
    internal void Validate(List<Diagnostic> report, float? maximumMagnitude)
    {
        ReadOnlySpan<float> values = [Minimum.X, Minimum.Y, Minimum.Z, Maximum.X, Maximum.Y, Maximum.Z];
        bool finite = true, inRange = true;
        foreach (float value in values)
        {
            finite &= float.IsFinite(value);
            inRange &= maximumMagnitude is not { } limit || value >= -limit && value <= limit;
        }
        if (!finite)
            report.Add(new Diagnostic(HeightMapDiagnosticCodes.BoundsNonFinite, "Bounds must contain finite values.", "bounds"));
        if (Minimum.X >= Maximum.X)
            report.Add(new Diagnostic(HeightMapDiagnosticCodes.BoundsXInverted, "Minimum X must be below maximum X.", "bounds.min_x"));
        if (Minimum.Y >= Maximum.Y)
            report.Add(new Diagnostic(HeightMapDiagnosticCodes.BoundsYInverted, "Minimum Y must be below maximum Y.", "bounds.min_y"));
        if (Minimum.Z >= Maximum.Z)
            report.Add(new Diagnostic(HeightMapDiagnosticCodes.BoundsZInverted, "Minimum Z must be below maximum Z.", "bounds.min_z"));
        if (!inRange)
            report.Add(new Diagnostic(HeightMapDiagnosticCodes.BoundsRuntimeRange, $"Bounds must stay within -{maximumMagnitude} and {maximumMagnitude}.", "bounds"));
    }
}

/// <summary>Limits a target runtime imposes on exported height maps, supplied by the caller;
/// validation without limits checks only the structural contract.</summary>
public sealed record HeightMapExportLimits
{
    /// <summary>Smallest accepted column and row count.</summary>
    public int MinimumDimension { get; init; } = 1;
    /// <summary>Largest accepted column and row count.</summary>
    public int MaximumDimension { get; init; } = ushort.MaxValue;
    /// <summary>Largest accepted absolute value of any bounds coordinate.</summary>
    public float MaximumBoundsMagnitude { get; init; } = float.MaxValue;
    /// <summary>Version byte the target reads, or null to accept any version.</summary>
    public byte? RequiredVersion { get; init; }
    /// <summary>Whether the target reads only byte cells.</summary>
    public bool RequireByteCells { get; init; }
    /// <summary>Whether cells whose minimum exceeds their maximum are errors rather than warnings.</summary>
    public bool RejectInvertedCells { get; init; }
    /// <summary>Flag bits the target understands; other bits are reported as unsupported.</summary>
    public HeightMapFlags SupportedFlags { get; init; } = HeightMapFlags.RleData | HeightMapFlags.WaterMask;
}

internal static class HeightCell<T> where T : unmanaged, INumber<T>
{
    public static int Size => typeof(T) == typeof(byte) ? 1 : typeof(T) == typeof(ushort) ? 2
        : typeof(T) == typeof(float) ? 4 : throw new NotSupportedException("Height cells must be byte, ushort or float.");
}

// Typed arrays rule out invalid integer types/ranges without per-cell coercion.
/// <summary>Stores minimum and maximum height samples (heightmap.dat, HMAP/PAMH) on a rectangular grid with world bounds and an
/// optional water mask. Cells run along X; row zero is at minimum Y. A cell whose two samples are zero is empty.
/// Supported cell types are byte, ushort and float. Concurrent mutation is not supported.</summary>
/// <remarks>Reading preserves cells whose minimum exceeds their maximum, the version byte, unknown flag bits, the reserved header
/// word, the byte order and the payload layout, so a file read with a canonical trimmed, full-width or dense layout is rewritten
/// byte for byte by default.</remarks>
public sealed partial class HeightMap<T> where T : unmanaged, INumber<T>
{
    private HeightMapBounds bounds;
    private bool[]? waterCells;
    /// <summary>Creates an empty owned grid. Cell encodings are byte, ushort or float.</summary>
    public HeightMap(int columns, int rows, HeightMapBounds bounds, bool waterMask = false)
    {
        _ = HeightCell<T>.Size;
        if (columns is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(columns));
        if (rows is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(rows));
        int count = checked(columns * rows);
        Bounds = bounds;
        Columns = columns;
        Rows = rows;
        MinimumCells = new T[count];
        MaximumCells = new T[count];
        if (waterMask)
            waterCells = new bool[count];
    }
    /// <summary>Creates a grid by copying row-major encoded samples. Sample ranges are checked by Validate.</summary>
    public HeightMap(int columns, int rows, HeightMapBounds bounds, ReadOnlySpan<T> minimum, ReadOnlySpan<T> maximum)
        : this(columns, rows, bounds)
    {
        if (minimum.Length != CellCount || maximum.Length != CellCount)
            throw new ArgumentException("Sample buffers must match the grid dimensions.");
        minimum.CopyTo(MinimumCells);
        maximum.CopyTo(MaximumCells);
    }
    // Takes ownership of freshly decoded buffers whose dimensions and lengths the caller has checked.
    internal HeightMap(int columns, int rows, HeightMapBounds bounds, T[] minimum, T[] maximum, bool[]? water)
    {
        Bounds = bounds;
        Columns = columns;
        Rows = rows;
        MinimumCells = minimum;
        MaximumCells = maximum;
        waterCells = water;
    }
    /// <summary>Number of cells along X.</summary>
    public int Columns { get; }
    /// <summary>Number of cells along Y.</summary>
    public int Rows { get; }
    /// <summary>World extents used to interpret encoded samples; changing extents does not resample the grid.</summary>
    public HeightMapBounds Bounds
    {
        get => bounds;
        set
        {
            var issues = new List<Diagnostic>();
            value.Validate(issues, null);
            if (issues.Count != 0)
                throw new ArgumentException("Height-map bounds must be finite and ordered.", nameof(value));
            bounds = value;
        }
    }
    /// <summary>Owned editable row-major minimum samples; the array length cannot change.</summary>
    public T[] MinimumCells { get; }
    /// <summary>Editable row-major maximum samples matching MinimumCells in length and ordering.</summary>
    public T[] MaximumCells { get; }
    /// <summary>Editable row-major water mask. Assignment copies a matching buffer; null removes the mask.</summary>
    public bool[]? WaterCells
    {
        get => waterCells;
        set
        {
            if (value is not null && value.Length != CellCount)
                throw new ArgumentException("Water mask must match the grid dimensions.", nameof(value));
            waterCells = value?.ToArray();
        }
    }
    /// <summary>File version byte; shipped files use 1.</summary>
    public byte Version { get; set; } = 1;
    /// <summary>Header flags. Writing derives <see cref="HeightMapFlags.RleData"/> from the layout and
    /// <see cref="HeightMapFlags.WaterMask"/> from <see cref="WaterCells"/>, and preserves every other bit.</summary>
    public HeightMapFlags Flags { get; set; } = HeightMapFlags.RleData;
    /// <summary>Two header bytes after the cell size, preserved as read in the file's byte order. Shipped files store zero.</summary>
    public ushort Reserved { get; set; }
    /// <summary>Byte order used by default when writing; Read sets the source order. New grids default to big-endian (HMAP).</summary>
    public ByteOrder ByteOrder { get; set; } = ByteOrder.Big;
    /// <summary>Payload layout used by default when writing; Read sets the layout matching the source.</summary>
    public HeightMapLayout Layout { get; set; } = HeightMapLayout.TrimmedRows;

    /// <summary>Checks grid dimensions, sample arrays and bounds. Without limits only the structural contract is checked and
    /// inverted cells are warnings; with limits, the target's export constraints are checked too.</summary>
    public ValidationReport Validate(HeightMapExportLimits? limits = null)
    {
        int size = HeightCell<T>.Size;
        var diagnostics = new List<Diagnostic>();
        Bounds.Validate(diagnostics, limits?.MaximumBoundsMagnitude);
        if (Columns is < 1 or > ushort.MaxValue)
            diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.ColumnsRange, "Columns must fit a positive unsigned 16-bit integer.", "columns"));
        if (Rows is < 1 or > ushort.MaxValue)
            diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.RowsRange, "Rows must fit a positive unsigned 16-bit integer.", "rows"));
        if (!Enum.IsDefined(Layout))
            diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.LayoutUnknown, "Unknown payload layout.", "layout"));
        if (ByteOrder is not ByteOrder.Big and not ByteOrder.Little)
            diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.ByteOrderUnknown, "Unknown byte order.", "byte_order"));
        if (limits is not null)
        {
            if (Columns < limits.MinimumDimension || Columns > limits.MaximumDimension)
                diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.ColumnsRuntimeRange,
                    $"Expected {limits.MinimumDimension} to {limits.MaximumDimension} columns.", "columns"));
            if (Rows < limits.MinimumDimension || Rows > limits.MaximumDimension)
                diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.RowsRuntimeRange,
                    $"Expected {limits.MinimumDimension} to {limits.MaximumDimension} rows.", "rows"));
            if (limits.RequiredVersion is { } version && Version != version)
                diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.VersionRuntime, $"Expected version {version}.", "version"));
            if (limits.RequireByteCells && size != 1)
                diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.CellFormatRuntime, "The target requires UINT8 cells.", "cell_format"));
            if ((Flags & ~limits.SupportedFlags) != 0)
                diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.FlagsUnsupported, "Unsupported height-map flags.", "flags"));
        }
        DiagnosticSeverity inverted = limits?.RejectInvertedCells == true ? DiagnosticSeverity.Error : DiagnosticSeverity.Warning;
        long count = (long)Columns * Rows;
        if (MinimumCells.LongLength != count)
            diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.MinimumCellsCount, "Incorrect minimum cell count.", "minimum_cells"));
        if (MaximumCells.LongLength != count)
            diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.MaximumCellsCount, "Incorrect maximum cell count.", "maximum_cells"));
        if (WaterCells is not null && WaterCells.LongLength != count)
            diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.WaterCellsCount, "Incorrect water cell count.", "water_cells"));
        if (MinimumCells.LongLength == count && MaximumCells.LongLength == count)
        {
            // Integer arrays already guarantee finiteness; float blocks also check NaN and infinity.
            // Resume scalar reporting at the first invalid pair, retaining every diagnostic in order.
            int first = size == 4
                ? SpanMath.FirstInvalidRange(MemoryMarshal.Cast<T, float>(MinimumCells.AsSpan()), MemoryMarshal.Cast<T, float>(MaximumCells.AsSpan()))
                : SpanMath.FirstGreaterThan<T>(MinimumCells, MaximumCells);
            for (int i = first; i < MinimumCells.Length; i++)
            {
                T lower = MinimumCells[i], upper = MaximumCells[i];
                if (!T.IsFinite(lower) || !T.IsFinite(upper))
                    diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.CellNonFinite, "Cell must contain finite values.", $"cells[{i}]"));
                else if (lower > upper)
                    diagnostics.Add(new Diagnostic(HeightMapDiagnosticCodes.CellInverted, "Cell minimum exceeds its maximum.", $"cells[{i}]", inverted));
            }
        }
        return new ValidationReport(diagnostics);
    }
}
