using System.Globalization;
using CommonFury.Binary;

namespace CommonFury.Resources;

/// <summary>Resource memory section.</summary>
public enum ResourceSection
{
    /// <summary>System (CPU) memory from 0x50000000; the virtual section in RSC8.</summary>
    System,
    /// <summary>Graphics (GPU) memory from 0x60000000; the physical section in RSC8.</summary>
    Graphics
}

/// <summary>A tagged resource pointer decoded into a section and offset. Page boundaries are the format's concern.</summary>
public readonly record struct ResourcePointer
{
    public const ulong SystemBase = 0x50000000;
    public const ulong GraphicsBase = 0x60000000;
    /// <summary>Addressable bytes per section (256 MiB).</summary>
    public const uint SectionSize = 0x10000000;

    public ResourceSection Section { get; }
    /// <summary>Below <see cref="SectionSize"/>.</summary>
    public uint Offset { get; }
    /// <summary>The tagged value stored in resource data.</summary>
    public ulong Address => BaseOf(Section) + Offset;

    /// <summary>Rejects offsets outside the 256 MiB section address space.</summary>
    public ResourcePointer(ResourceSection section, long offset)
    {
        if (section is not ResourceSection.System and not ResourceSection.Graphics)
            throw new ArgumentOutOfRangeException(nameof(section));
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offset, SectionSize);
        Section = section;
        Offset = (uint)offset;
    }

    public static ulong BaseOf(ResourceSection section) => section switch
    {
        ResourceSection.System => SystemBase,
        ResourceSection.Graphics => GraphicsBase,
        _ => throw new ArgumentOutOfRangeException(nameof(section))
    };

    /// <summary>Returns false for null and untagged addresses.</summary>
    /// <param name="address">Stored pointer value.</param>
    /// <param name="pointer">The decoded section and offset.</param>
    /// <param name="ignoreUpperBits">Decode from the low 32 bits only (RSC8, whose pointers carry metadata in the upper bits).
    /// Otherwise (RSC7) any upper bit makes the address invalid.</param>
    public static bool TryDecode(ulong address, out ResourcePointer pointer, bool ignoreUpperBits = false)
    {
        if (ignoreUpperBits)
            address = (uint)address;
        if (address >= SystemBase && address < SystemBase + SectionSize)
        {
            pointer = new(ResourceSection.System, (long)(address - SystemBase));
            return true;
        }
        if (address >= GraphicsBase && address < GraphicsBase + SectionSize)
        {
            pointer = new(ResourceSection.Graphics, (long)(address - GraphicsBase));
            return true;
        }
        pointer = default;
        return false;
    }

    /// <summary>Throws <see cref="InvalidDataException"/> for null or untagged addresses; see <see cref="TryDecode"/>.</summary>
    public static ResourcePointer Decode(ulong address, bool ignoreUpperBits = false) => TryDecode(address, out var pointer, ignoreUpperBits)
        ? pointer : throw new InvalidDataException($"Invalid resource pointer 0x{address.ToString("X16", CultureInfo.InvariantCulture)}.");

    /// <summary>Decodes and checks that <paramref name="length"/> bytes fit in the section's decoded size; see <see cref="TryDecode"/>.</summary>
    public static ResourcePointer Resolve(ulong address, int length, long systemSize, long graphicsSize, bool ignoreUpperBits = false)
    {
        var pointer = Decode(address, ignoreUpperBits);
        pointer.RequireWithin(length, pointer.Section == ResourceSection.System ? systemSize : graphicsSize);
        return pointer;
    }

    /// <summary>Throws <see cref="InvalidDataException"/> unless <paramref name="length"/> bytes fit in <paramref name="sectionSize"/>.</summary>
    public void RequireWithin(int length, long sectionSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        BinaryLayout.RequireRange(Offset, length, sectionSize);
    }

    /// <summary>Stays in the same section.</summary>
    public ResourcePointer Add(long bytes) => new(Section, checked(Offset + bytes));

    public override string ToString() => $"0x{Address.ToString("X8", CultureInfo.InvariantCulture)}";
}
