namespace CommonFury.Textures;

/// <summary>A contiguous encoded texture: format, dimensions, topology, mip count and payload, as used by <see cref="DdsCodec"/>.</summary>
/// <remarks>The surface references <see cref="Data"/> without copying it and observes later changes to that storage.
/// Callers that need a snapshot pass an owned copy. Instances are immutable otherwise and need no disposal.</remarks>
public sealed class TextureSurface
{
    /// <summary>Checks the layout and references the payload, which must hold exactly the complete encoded chain.</summary>
    /// <exception cref="NotSupportedException">The format has no supported encoded layout.</exception>
    /// <exception cref="ArgumentException">Dimensions, topology or payload length are inconsistent.</exception>
    public TextureSurface(int width, int height, TextureFormat format, int mipCount, ReadOnlyMemory<byte> data, TextureShape? shape = null)
    {
        TextureShape selected = shape ?? TextureShape.TwoD;
        TextureSubresource[] layout = TextureLayout.CreateSubresources(width, height, format, mipCount, selected);
        if (checked(layout[^1].Mip.Offset + layout[^1].Mip.Length) != data.Length)
            throw new ArgumentException("Payload length must match the complete encoded mip chain.", nameof(data));
        Width = width;
        Height = height;
        Format = format;
        MipCount = mipCount;
        Shape = selected;
        Data = data;
        Subresources = Array.AsReadOnly(layout);
        Mips = Array.AsReadOnly(layout.Take(mipCount).Select(item => item.Mip).ToArray());
    }

    public int Width { get; }
    public int Height { get; }
    public TextureFormat Format { get; }
    /// <summary>Mips per array element and face.</summary>
    public int MipCount { get; }
    public TextureShape Shape { get; }
    /// <summary>Referenced, not copied; ordered by array element, face and mip.</summary>
    public ReadOnlyMemory<byte> Data { get; }
    /// <summary>In payload order.</summary>
    public IReadOnlyList<TextureSubresource> Subresources { get; }
    /// <summary>Mips of the first array element and face; volume mips include every slice.</summary>
    public IReadOnlyList<TextureMip> Mips { get; }

    /// <summary>Selects an array-face mip. Cube faces use +X, -X, +Y, -Y, +Z, -Z order.</summary>
    public TextureSubresource GetSubresource(int mipLevel = 0, int arrayIndex = 0, int face = 0)
    {
        if ((uint)mipLevel >= (uint)MipCount || (uint)arrayIndex >= (uint)Shape.ArraySize || (uint)face >= (uint)Shape.Faces)
            throw new ArgumentOutOfRangeException(nameof(mipLevel), "Subresource coordinates exceed the texture topology.");
        return Subresources[(arrayIndex * Shape.Faces + face) * MipCount + mipLevel];
    }

    /// <summary>Returns a view of one depth slice, not a copy.</summary>
    public ReadOnlyMemory<byte> GetEncodedSlice(int mipLevel = 0, int arrayIndex = 0, int face = 0, int depthSlice = 0)
    {
        var part = GetSubresource(mipLevel, arrayIndex, face);
        if ((uint)depthSlice >= (uint)part.Depth)
            throw new ArgumentOutOfRangeException(nameof(depthSlice));
        return Data.Slice(checked(part.Mip.Offset + depthSlice * part.SlicePitch), part.SlicePitch);
    }
}
