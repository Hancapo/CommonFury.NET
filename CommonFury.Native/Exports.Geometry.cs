using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CommonFury.Binary;
using CommonFury.Collisions;
using CommonFury.HeightMaps;
using CommonFury.Numerics;
using CommonFury.Textures;
using CommonFury.Vertices;

namespace CommonFury.Native;

internal static unsafe partial class Exports
{
    [UnmanagedCallersOnly(EntryPoint = "cf_dds_read", CallConvs = [typeof(CallConvCdecl)])]
    internal static int DdsRead(byte* data, ulong length, TextureInfo* info, uint size, Buffer* payload)
    {
        try
        {
            Abi.Structure(info, size);
            Abi.RequireOutput(payload);
            *payload = default;
            TextureSurface surface = DdsCodec.Read(Abi.Input(data, length));
            *info = Info(surface);
            *payload = Abi.Copy(surface.Data.Span);
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_dds_write", CallConvs = [typeof(CallConvCdecl)])]
    internal static int DdsWrite(TextureInfo* info, uint size, byte* payload, ulong length, Buffer* result)
    {
        try
        {
            Abi.Structure(info, size);
            Abi.RequireOutput(result);
            *result = default;
            var surface = new TextureSurface(checked((int)info->Width), checked((int)info->Height), (TextureFormat)info->Format,
                checked((int)info->MipCount), Abi.Input(payload, length).ToArray(), Shape(info));
            *result = Abi.Copy(DdsCodec.Write(surface));
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_texture_data_size", CallConvs = [typeof(CallConvCdecl)])]
    internal static int TextureDataSize(TextureInfo* info, uint size, ulong* result)
    {
        try
        {
            Abi.Structure(info, size);
            Abi.RequireOutput(result);
            *result = (ulong)TextureLayout.DataSize(checked((int)info->Width), checked((int)info->Height), (TextureFormat)info->Format,
                checked((int)info->MipCount), Shape(info));
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_pixel_decode", CallConvs = [typeof(CallConvCdecl)])]
    internal static int PixelDecode(uint format, byte* source, ulong length, byte* rgba, ulong rgbaLength)
    {
        try { PixelCodec.Decode(Abi.Input(source, length), (TextureFormat)format, Abi.Output(rgba, rgbaLength)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_pixel_encode", CallConvs = [typeof(CallConvCdecl)])]
    internal static int PixelEncode(uint format, byte* rgba, ulong length, byte* destination, ulong destinationLength)
    {
        try { PixelCodec.Encode(Abi.Input(rgba, length), (TextureFormat)format, Abi.Output(destination, destinationLength)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_bc1_encode", CallConvs = [typeof(CallConvCdecl)])]
    internal static int Bc1Encode(byte* rgba, ulong length, uint width, uint height, byte* destination, ulong destinationLength)
    {
        try
        {
            Bc1.Encode(Abi.Input(rgba, length), checked((int)width), checked((int)height), Abi.Output(destination, destinationLength));
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_bc1_decode", CallConvs = [typeof(CallConvCdecl)])]
    internal static int Bc1Decode(byte* blocks, ulong length, uint width, uint height, Buffer* rgba)
    {
        try
        {
            Abi.RequireOutput(rgba);
            *rgba = default;
            *rgba = Abi.Copy(Bc1.Decode(Abi.Input(blocks, length), checked((int)width), checked((int)height)));
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_heightmap_read", CallConvs = [typeof(CallConvCdecl)])]
    internal static int HeightMapRead(byte* data, ulong length, uint rejectInvertedCells, HeightMapInfo* info, uint size, Buffer* minimum,
        Buffer* maximum, Buffer* water)
    {
        try
        {
            Abi.Structure(info, size);
            Abi.RequireOutput(minimum);
            Abi.RequireOutput(maximum);
            Abi.RequireOutput(water);
            *minimum = *maximum = *water = default;
            ReadOnlySpan<byte> input = Abi.Input(data, length);
            if (input.Length < 6)
                throw new InvalidDataException("Truncated height-map header.");
            try
            {
                switch (input[5])
                {
                    case 1: HeightMapRead(HeightMapCodec.Read<byte>(input, rejectInvertedCells: rejectInvertedCells != 0), info, minimum, maximum, water); break;
                    case 2: HeightMapRead(HeightMapCodec.Read<ushort>(input, rejectInvertedCells: rejectInvertedCells != 0), info, minimum, maximum, water); break;
                    case 4: HeightMapRead(HeightMapCodec.Read<float>(input, rejectInvertedCells: rejectInvertedCells != 0), info, minimum, maximum, water); break;
                    default: throw new InvalidDataException("Unsupported height-map cell size.");
                }
            }
            catch
            {
                Abi.Release(minimum->Handle);
                Abi.Release(maximum->Handle);
                Abi.Release(water->Handle);
                *minimum = *maximum = *water = default;
                throw;
            }
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_heightmap_write", CallConvs = [typeof(CallConvCdecl)])]
    internal static int HeightMapWrite(HeightMapInfo* info, uint size, byte* minimum, byte* maximum, ulong cellBytes, byte* water, ulong waterLength,
        Buffer* result)
    {
        try
        {
            Abi.Structure(info, size);
            Abi.RequireOutput(result);
            *result = default;
            ReadOnlySpan<byte> low = Abi.Input(minimum, cellBytes), high = Abi.Input(maximum, cellBytes);
            ReadOnlySpan<byte> mask = Abi.Input(water, waterLength);
            *result = Abi.Copy(info->CellSize switch
            {
                1 => HeightMapWrite<byte>(info, low, high, mask),
                2 => HeightMapWrite<ushort>(info, low, high, mask),
                4 => HeightMapWrite<float>(info, low, high, mask),
                _ => throw new ArgumentOutOfRangeException(nameof(info), "Unsupported height-map cell size.")
            });
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_vertex_decode", CallConvs = [typeof(CallConvCdecl)])]
    internal static int VertexDecode(uint dxgi, uint range, uint bigEndian, byte* data, ulong length, uint stride, float* values, ulong count)
    {
        try
        {
            VertexElementFormat format = VertexFormat(dxgi, range);
            int elementSize = VertexElementFormats.Size(format);
            ReadOnlySpan<byte> input = Abi.Input(data, length);
            Span<float> output = Abi.Output(values, checked(count * 4));
            CheckStride(stride, elementSize, count, input.Length);
            ByteOrder order = bigEndian != 0 ? ByteOrder.Big : ByteOrder.Little;
            VertexElementCodec.Read(input, VertexStride(stride), format, MemoryMarshal.Cast<float, Vector4>(output), order);
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_vertex_encode", CallConvs = [typeof(CallConvCdecl)])]
    internal static int VertexEncode(uint dxgi, uint range, float* values, ulong count, uint stride, byte* data, ulong length)
    {
        try
        {
            VertexElementFormat format = VertexFormat(dxgi, range);
            int elementSize = VertexElementFormats.Size(format);
            Span<float> input = Abi.Output(values, checked(count * 4));
            Span<byte> output = Abi.Output(data, length);
            CheckStride(stride, elementSize, count, output.Length);
            VertexElementCodec.Write(output, VertexStride(stride), format, MemoryMarshal.Cast<float, Vector4>(input));
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_vertex_format_size", CallConvs = [typeof(CallConvCdecl)])]
    internal static int VertexFormatSize(uint dxgi, uint range, uint* result)
    {
        try { Abi.RequireOutput(result); *result = (uint)VertexElementFormats.Size(VertexFormat(dxgi, range)); return Abi.Success(); }
        catch (Exception error) { return Abi.Fail(error); }
    }

    [UnmanagedCallersOnly(EntryPoint = "cf_bvh_build", CallConvs = [typeof(CallConvCdecl)])]
    internal static int BvhBuild(float* boxes, ulong count, uint leafSize, uint split, uint leafIds, Buffer* nodes, Buffer* order)
    {
        try
        {
            Abi.RequireOutput(nodes);
            Abi.RequireOutput(order);
            *nodes = *order = default;
            if (split > (uint)BvhSplit.LowerMedian || leafIds > (uint)BvhLeafIds.FirstItemIndex)
                throw new ArgumentOutOfRangeException(nameof(split));
            Span<float> input = Abi.Output(boxes, checked(count * 6));
            var items = new Aabb3[(int)count];
            for (int i = 0; i < items.Length; i++)
            {
                Span<float> box = input.Slice(i * 6, 6);
                items[i] = new(new Vector3(box[..3]), new Vector3(box[3..]));
            }
            BvhBuild build = BvhBuilder.Build(items, new BvhBuildOptions(checked((int)leafSize), (BvhSplit)split, (BvhLeafIds)leafIds));
            var packed = new Native.BvhNode[build.Nodes.Count];
            for (int i = 0; i < packed.Length; i++)
            {
                CommonFury.Collisions.BvhNode node = build.Nodes[i];
                fixed (float* minimum = packed[i].Minimum, maximum = packed[i].Maximum)
                {
                    node.Bounds.Minimum.CopyTo(new Span<float>(minimum, 3));
                    node.Bounds.Maximum.CopyTo(new Span<float>(maximum, 3));
                }
                packed[i].ItemId = node.ItemId;
                packed[i].ItemCount = node.ItemCount;
            }
            *nodes = Abi.Copy(MemoryMarshal.AsBytes(packed.AsSpan()));
            try { *order = Abi.Copy(MemoryMarshal.AsBytes(build.Order.AsSpan())); }
            catch { Abi.Release(nodes->Handle); *nodes = default; throw; }
            return Abi.Success();
        }
        catch (Exception error) { return Abi.Fail(error); }
    }

    private static TextureInfo Info(TextureSurface surface) => new()
    {
        Format = (uint)surface.Format,
        Width = (uint)surface.Width,
        Height = (uint)surface.Height,
        MipCount = (uint)surface.MipCount,
        Dimension = (uint)surface.Shape.Dimension,
        Depth = (uint)surface.Shape.Depth,
        ArraySize = (uint)surface.Shape.ArraySize
    };

    private static TextureShape Shape(TextureInfo* info)
    {
        var dimension = (TextureDimension)info->Dimension;
        if (!Enum.IsDefined(dimension))
            throw new ArgumentOutOfRangeException(nameof(info), "Unknown texture dimension.");
        return new(dimension, Math.Max(1, checked((int)info->Depth)), Math.Max(1, checked((int)info->ArraySize)));
    }

    private static void HeightMapRead<T>(HeightMap<T> map, HeightMapInfo* info, Buffer* minimum, Buffer* maximum, Buffer* water)
        where T : unmanaged, INumber<T>
    {
        *info = new()
        {
            Columns = (uint)map.Columns,
            Rows = (uint)map.Rows,
            CellSize = (uint)sizeof(T),
            Flags = (uint)map.Flags,
            Version = map.Version,
            Layout = (uint)map.Layout,
            ByteOrder = map.ByteOrder == ByteOrder.Little ? 1u : 0u,
            Reserved = map.Reserved,
            HasWaterMask = map.WaterCells is null ? 0u : 1u
        };
        map.Bounds.Minimum.CopyTo(new Span<float>(info->Minimum, 3));
        map.Bounds.Maximum.CopyTo(new Span<float>(info->Maximum, 3));
        *minimum = Abi.Copy(MemoryMarshal.AsBytes(map.MinimumCells.AsSpan()));
        *maximum = Abi.Copy(MemoryMarshal.AsBytes(map.MaximumCells.AsSpan()));
        if (map.WaterCells is { } cells)
            *water = Abi.Copy(MemoryMarshal.AsBytes(cells.AsSpan()));
    }

    private static byte[] HeightMapWrite<T>(HeightMapInfo* info, ReadOnlySpan<byte> minimum, ReadOnlySpan<byte> maximum, ReadOnlySpan<byte> water)
        where T : unmanaged, INumber<T>
    {
        int columns = checked((int)info->Columns), rows = checked((int)info->Rows);
        long cells = (long)columns * rows;
        if (minimum.Length != cells * sizeof(T) || maximum.Length != cells * sizeof(T))
            throw new ArgumentException("Cell buffers do not match the grid dimensions.");
        if (info->HasWaterMask != 0 ? water.Length != cells : !water.IsEmpty)
            throw new ArgumentException("Water mask does not match the grid dimensions.");
        var bounds = new HeightMapBounds(new Vector3(new ReadOnlySpan<float>(info->Minimum, 3)), new Vector3(new ReadOnlySpan<float>(info->Maximum, 3)));
        var map = new HeightMap<T>(columns, rows, bounds, MemoryMarshal.Cast<byte, T>(minimum), MemoryMarshal.Cast<byte, T>(maximum))
        {
            Version = checked((byte)info->Version),
            Flags = (HeightMapFlags)info->Flags,
            Reserved = checked((ushort)info->Reserved),
            ByteOrder = info->ByteOrder != 0 ? ByteOrder.Little : ByteOrder.Big,
            Layout = (HeightMapLayout)info->Layout
        };
        if (info->HasWaterMask != 0)
        {
            var mask = new bool[cells];
            for (int i = 0; i < water.Length; i++)
                mask[i] = water[i] != 0;
            map.WaterCells = mask;
        }
        return HeightMapCodec.Write(map);
    }

    private static VertexElementFormat VertexFormat(uint dxgi, uint range)
    {
        if (dxgi > byte.MaxValue || range > (uint)Rgb10A2Range.MinusOneToOne)
            throw new ArgumentOutOfRangeException(nameof(dxgi));
        return VertexElementFormats.FromDxgi((byte)dxgi, (Rgb10A2Range)range);
    }

    private static void CheckStride(uint stride, int elementSize, ulong count, int length)
    {
        if (stride < elementSize)
            throw new ArgumentOutOfRangeException(nameof(stride));
        if (count != 0 && (ulong)length < (count - 1) * stride + (ulong)elementSize)
            throw new ArgumentException("Vertex buffer is too small for the requested elements.");
    }

    // After CheckStride, a stride past int range is only possible with at most one element, which never steps by it.
    private static int VertexStride(uint stride) => (int)Math.Min(stride, int.MaxValue);
}
