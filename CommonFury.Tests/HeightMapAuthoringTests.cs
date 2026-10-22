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
