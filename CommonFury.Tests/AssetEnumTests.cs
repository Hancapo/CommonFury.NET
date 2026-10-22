using CommonFury.Assets;
using Xunit;

namespace CommonFury.Tests;

// Stored values are binary data; these pin them so a rename cannot silently renumber.
public sealed class AssetEnumTests
{
    [Fact]
    public void BoneChannelBitsMatchSkeletonDescriptors()
    {
        Assert.Equal(0x77u, (uint)BoneChannelFlags.Animatable);
        Assert.Equal(0x700u, (uint)BoneChannelFlags.Scale);
        Assert.Equal(0x88u, (uint)(BoneChannelFlags.LimitRotation | BoneChannelFlags.LimitTranslation));
        Assert.Equal(0x7FFu, (uint)BoneChannelFlags.SharedMask);
        // Unnamed high bits survive a round trip.
        var raw = (BoneChannelFlags)0x8000_2007;
        Assert.Equal(0x8000_2007u, (uint)raw);
        Assert.Equal(BoneChannelFlags.Rotation, raw & BoneChannelFlags.SharedMask);
    }

    [Fact]
    public void MapFlagValuesMatchStoredData()
    {
        Assert.Equal(new uint[] { 0, 1, 2 }, Enum.GetValues<YmapFlags>().Select(v => (uint)v));
        uint[] content = Enum.GetValues<YmapContentFlags>().Select(v => (uint)v).ToArray();
        Assert.Equal(Enumerable.Range(0, 12).Select(bit => 1u << bit).Prepend(0u), content);
        Assert.Equal(0x800u, (uint)YmapContentFlags.NoAutomaticStreaming);
    }

    [Fact]
    public void DrawableLodOrderIsDescending()
    {
        Assert.Equal([DrawableLod.High, DrawableLod.Medium, DrawableLod.Low, DrawableLod.VeryLow], Enum.GetValues<DrawableLod>());
        Assert.Equal(3, (int)DrawableLod.VeryLow);
    }
}
