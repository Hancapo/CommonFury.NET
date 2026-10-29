using System.Numerics;
using CommonFury.HeightMaps;
using Xunit;

namespace CommonFury.Tests;

public sealed class HeightMapAuthoringTests
{
    [Theory]
    [InlineData(0f, 0f)] [InlineData(-5f, -1f)]
    public void FlatCellAtTheBottomOfTheBoundsStaysOccupied(float minimum, float maximum)
    {
        var map = new HeightMap<byte>(2, 1, new(Vector3.Zero, new(20, 10, 255)));
        map.SetHeight(0, 0, minimum, maximum);
        Assert.False(map.IsEmpty(0, 0));
        Assert.Equal(0f, map.GetHeightRange(0, 0)!.Value.Minimum);
        Assert.True(map.GetHeightRange(0, 0)!.Value.Maximum >= maximum);
        var reread = HeightMapCodec.Read<byte>(HeightMapCodec.Write(map));
        Assert.False(reread.IsEmpty(0, 0));
        Assert.True(reread.IsEmpty(1, 0));
    }

    [Fact]
    public void WorldQueriesConservativelyQuantizeAndClearHeightsAndWater()
    {
        var map = new HeightMap<byte>(10, 10, new(Vector3.Zero, new(100, 100, 255)));
        map.SetHeightAt(25, 35, 12.2f, 18.1f);
        map.SetWaterAt(25, 35);
        Assert.Equal((12f, 19f), map.GetHeightRange(2, 3));
        Assert.Equal(new Vector2(25, 35), map.GetCellCenter(2, 3));
        Assert.Equal(map.GetHeightRange(2, 3), map.GetHeightRangeInBounds(new(20, 30), new(30, 40)));
        Assert.False(map.TryGetCell(100, 35, out _, out _));
        Assert.True(map.IsWaterAt(25, 35));
        Assert.Single(map.EnumerateOccupiedCells());
        var copy = HeightMapCodec.Read<byte>(HeightMapCodec.Write(map));
        Assert.Equal(map.GetHeightRange(2, 3), copy.GetHeightRange(2, 3));
        copy.ClearCell(2, 3);
        Assert.Null(copy.GetHeightRangeAt(25, 35));
        Assert.False(copy.IsWater(2, 3));
    }

    [Fact]
    public void RangeQueriesAggregateOccupiedCellsInTheClippedRectangle()
    {
        var map = new HeightMap<ushort>(4, 3, new(Vector3.Zero, new(40, 30, 1000)));
        map.SetHeight(0, 0, 100, 200);
        map.SetHeight(3, 0, 50, 60);
        map.SetHeight(1, 2, 300, 900);
        map.SetHeight(2, 1, 400, 500);
        float Lower(int column, int row) => map.GetHeightRange(column, row)!.Value.Minimum;
        float Upper(int column, int row) => map.GetHeightRange(column, row)!.Value.Maximum;
        Assert.Equal((Lower(3, 0), Upper(1, 2)), map.GetHeightRangeInBounds(new(-100, -100), new(100, 100)));
        // Columns 1-2 of rows 1-2; the query edges fall inside cells, which are included.
        Assert.Equal((Lower(1, 2), Upper(1, 2)), map.GetHeightRangeInBounds(new(15, 15), new(25, 25)));
        Assert.Equal((Lower(2, 1), Upper(2, 1)), map.GetHeightRangeInBounds(new(20, 10), new(30, 20)));
        Assert.Equal((Lower(0, 0), Upper(0, 0)), map.GetHeightRangeInBounds(new(-5, -5), new(10, 10)));
        Assert.Null(map.GetHeightRangeInBounds(new(10, 0), new(30, 10)));
        Assert.Null(map.GetHeightRangeInBounds(new(50, 0), new(60, 10)));
    }

    [Fact]
    public void WaterMasksPackEightCellsPerByteFromBitZero()
    {
        var map = new HeightMap<byte>(11, 1, new(Vector3.Zero, new(11, 1, 255)));
        bool[] water = [true, false, true, true, false, false, false, true, false, true, true];
        map.WaterCells = water;
        byte[] bytes = HeightMapCodec.Write(map);
        Assert.Equal(new byte[] { 0x8D, 0x06 }, bytes[^2..]);
        Assert.Equal(water, HeightMapCodec.Read<byte>(bytes).WaterCells);
        // Padding bits after the last cell are ignored.
        bytes[^1] |= 0xF8;
        Assert.Equal(water, HeightMapCodec.Read<byte>(bytes).WaterCells);
    }

    [Fact]
    public void ExpandedGridRoundtripHasAnExplicitReadBudget()
    {
        var map = new HeightMap<ushort>(1001, 1, new(Vector3.Zero, new(1001, 1, 100)));
        map.SetHeight(1000, 0, 20, 30);
        byte[] bytes = HeightMapCodec.Write(map);
        Assert.Equal(map.GetHeightRange(1000, 0), HeightMapCodec.Read<ushort>(bytes).GetHeightRange(1000, 0));
        Assert.Throws<InvalidDataException>(() => HeightMapCodec.Read<ushort>(bytes, maximumCells: 1000));
        Assert.Throws<ValidationException>(() => HeightMapCodec.Write(map, new HeightMapExportLimits { MaximumDimension = 1000 }));
    }

    [Fact]
    public void GridConstructionAndFailedEditsPreserveStructure()
    {
        var bounds = new HeightMapBounds(Vector3.Zero, new(10, 10, 100));
        var map = HeightMap<float>.FromHeightGrids(new float?[,] { { 12, null } }, bounds);
        Assert.Equal((12f, 12f), map.GetHeightRange(0, 0));
        Assert.Null(map.GetHeightRange(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.SetHeight(0, 0, 15, float.NaN));
        Assert.Equal((12f, 12f), map.GetHeightRange(0, 0));
        Assert.Throws<ArgumentException>(() => new HeightMap<byte>(2, 1, bounds, [1], [1, 2]));
        Assert.Throws<ArgumentException>(() => map.WaterCells = new bool[1]);
    }
}
