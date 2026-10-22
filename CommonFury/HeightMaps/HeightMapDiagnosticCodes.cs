namespace CommonFury.HeightMaps;

/// <summary>Stable diagnostic codes reported by height-map validation.</summary>
public static class HeightMapDiagnosticCodes
{
    public const string BoundsNonFinite = "heightmap.bounds.non_finite";
    /// <summary>A bounds coordinate exceeds the game's limit.</summary>
    public const string BoundsRuntimeRange = "heightmap.bounds.runtime_range";
    /// <summary>The X extent is empty or inverted.</summary>
    public const string BoundsXInverted = "heightmap.bounds.x.inverted";
    /// <summary>The Y extent is empty or inverted.</summary>
    public const string BoundsYInverted = "heightmap.bounds.y.inverted";
    /// <summary>The Z extent is empty or inverted.</summary>
    public const string BoundsZInverted = "heightmap.bounds.z.inverted";
    /// <summary>The cell type is not one the game reads.</summary>
    public const string CellFormatRuntime = "heightmap.cell_format.runtime";
    public const string CellInverted = "heightmap.cell.inverted";
    public const string CellNonFinite = "heightmap.cell.non_finite";
    /// <summary>The column count does not fit the file format.</summary>
    public const string ColumnsRange = "heightmap.columns.range";
    /// <summary>The column count is outside the game's limits.</summary>
    public const string ColumnsRuntimeRange = "heightmap.columns.runtime_range";
    /// <summary>The header has flag bits the game does not understand.</summary>
    public const string FlagsUnsupported = "heightmap.flags.unsupported";
    public const string MaximumCellsCount = "heightmap.maximum_cells.count";
    public const string MinimumCellsCount = "heightmap.minimum_cells.count";
    /// <summary>The row count does not fit the file format.</summary>
    public const string RowsRange = "heightmap.rows.range";
    /// <summary>The row count is outside the game's limits.</summary>
    public const string RowsRuntimeRange = "heightmap.rows.runtime_range";
    /// <summary>The version byte differs from the one the game reads.</summary>
    public const string VersionRuntime = "heightmap.version.runtime";
    public const string WaterCellsCount = "heightmap.water_cells.count";
    public const string LayoutUnknown = "heightmap.layout.unknown";
    public const string ByteOrderUnknown = "heightmap.byte_order.unknown";
}
