using CommonFury.Resources;
using Xunit;

namespace CommonFury.Tests;

public sealed class ResourcePointerTests
{
    [Fact]
    public void DecodesTaggedSectionsAndRejectsOthers()
    {
        Assert.True(ResourcePointer.TryDecode(0x50000010, out var system));
        Assert.Equal((ResourceSection.System, 0x10u), (system.Section, system.Offset));
        Assert.Equal(0x50000010ul, system.Address);
        Assert.True(ResourcePointer.TryDecode(0x6FFFFFFF, out var graphics));
        Assert.Equal((ResourceSection.Graphics, 0x0FFFFFFFu), (graphics.Section, graphics.Offset));
        foreach (ulong invalid in new ulong[] { 0, 0x4FFFFFFF, 0x70000000, 0xF0000000 })
            Assert.False(ResourcePointer.TryDecode(invalid, out _));
        Assert.Throws<InvalidDataException>(() => ResourcePointer.Decode(0));
        Assert.Equal("0x50000010", system.ToString());
    }

    [Fact]
    public void UpperMetadataBitsAreAcceptedOnlyWhenRequested()
    {
        const ulong tagged = 0xABCD_0000_6000_0020;
        Assert.False(ResourcePointer.TryDecode(tagged, out _));
        Assert.True(ResourcePointer.TryDecode(tagged, out var pointer, ignoreUpperBits: true));
        Assert.Equal(new ResourcePointer(ResourceSection.Graphics, 0x20), pointer);
    }

    [Fact]
    public void ResolveChecksRangesAgainstTheOwnSectionOnly()
    {
        Assert.Equal(new ResourcePointer(ResourceSection.System, 8), ResourcePointer.Resolve(0x50000008, 8, systemSize: 16, graphicsSize: 0));
        Assert.Throws<InvalidDataException>(() => ResourcePointer.Resolve(0x50000008, 9, systemSize: 16, graphicsSize: 1000));
        Assert.Equal(new ResourcePointer(ResourceSection.Graphics, 16), ResourcePointer.Resolve(0x60000010, 0, systemSize: 0, graphicsSize: 16));
        Assert.Throws<InvalidDataException>(() => ResourcePointer.Resolve(0x60000010, 1, systemSize: 1000, graphicsSize: 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourcePointer(ResourceSection.System, 1).RequireWithin(-1, 16));
    }

    [Fact]
    public void ConstructionStaysInsideTheSectionAddressSpace()
    {
        Assert.Equal(0x60000000ul + 0x0FFFFFFF, new ResourcePointer(ResourceSection.Graphics, ResourcePointer.SectionSize - 1).Address);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourcePointer(ResourceSection.System, ResourcePointer.SectionSize));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourcePointer(ResourceSection.System, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourcePointer((ResourceSection)2, 0));
        Assert.Equal(0x50000018ul, new ResourcePointer(ResourceSection.System, 0x10).Add(8).Address);
        Assert.Throws<ArgumentOutOfRangeException>(() => new ResourcePointer(ResourceSection.System, ResourcePointer.SectionSize - 1).Add(1));
    }
}
