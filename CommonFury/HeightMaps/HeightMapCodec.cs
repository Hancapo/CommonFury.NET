using System.Numerics;
using CommonFury.Binary;
using CommonFury.IO;

namespace CommonFury.HeightMaps;

// Row-table offsets are signed and relative to the column: compactStart = dataOffset + start.
/// <summary>Reads and writes height grids with explicit cell types, byte order and payload layout.</summary>
public static class HeightMapCodec
{
    /// <summary>Default limit on decoded cells, protecting readers from oversized dimensions.</summary>
    public const int DefaultMaximumCells = 16 * 1024 * 1024;
    private const int HeaderSize = 44;
    private const int RowSize = 8;
    private readonly record struct RowRange(int Start, int Count, int CompactStart);

    /// <summary>Decodes a height grid into the supported requested cell type, detecting byte order from the header and
    /// recording the payload layout. Cells whose minimum exceeds their maximum are preserved unless
    /// <paramref name="rejectInvertedCells"/> is set.</summary>
    public static HeightMap<T> Read<T>(ReadOnlySpan<byte> data, int maximumCells = DefaultMaximumCells, bool rejectInvertedCells = false)
        where T : unmanaged, INumber<T>
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumCells);
        if (data.Length < HeaderSize)
            throw new InvalidDataException("Truncated height-map header.");
        ByteOrder order = data[..4].SequenceEqual("HMAP"u8) ? ByteOrder.Big
            : data[..4].SequenceEqual("PAMH"u8) ? ByteOrder.Little
            : throw new InvalidDataException("Invalid height-map tag.");
        int size = HeightCell<T>.Size;
        if (data[5] != size)
            throw new InvalidDataException("Cell size does not match the requested type.");
        var flags = (HeightMapFlags)Endian.U32(data[8..], order);
        int columns = Endian.U16(data[12..], order), rows = Endian.U16(data[14..], order);
        if (columns < 1 || rows < 1 || (long)columns * rows > maximumCells || (long)columns * rows * size * 2 > int.MaxValue)
            throw new InvalidDataException("Height-map dimensions are empty or exceed the decoded-cell limit.");
        int count = columns * rows;
        int maskSize = flags.HasFlag(HeightMapFlags.WaterMask) ? (count + 7) / 8 : 0;
        uint payloadSize = Endian.U32(data[40..], order);
        if ((long)HeaderSize + payloadSize + maskSize != data.Length)
            throw new InvalidDataException("Height-map size mismatch.");
        ReadOnlySpan<byte> payload = data.Slice(HeaderSize, (int)payloadSize);
        var minimum = new T[count];
        var maximum = new T[count];
        HeightMapLayout layout = HeightMapLayout.Dense;
        if (flags.HasFlag(HeightMapFlags.RleData))
        {
            int tableSize = rows * RowSize;
            if (payload.Length < tableSize || (payload.Length - tableSize) % (size * 2) != 0)
                throw new InvalidDataException("Truncated or misaligned RLE payload.");
            int compactCount = (payload.Length - tableSize) / (size * 2);
            bool full = true;
            int cursor = 0;
            for (int row = 0; row < rows; row++)
            {
                ReadOnlySpan<byte> entry = payload[(row * RowSize)..];
                int start = Endian.U16(entry, order), length = Endian.U16(entry[2..], order);
                long compactStart = (long)unchecked((int)Endian.U32(entry[4..], order)) + start;
                if (start > columns || length > columns - start || compactStart < 0 || compactStart + length > compactCount)
                    throw new InvalidDataException("RLE row points outside the grid or compact data.");
                int source = tableSize + (int)compactStart * size;
                int destination = row * columns + start;
                Endian.Decode(payload.Slice(source, length * size), maximum.AsSpan(destination, length), order);
                Endian.Decode(payload.Slice(source + compactCount * size, length * size), minimum.AsSpan(destination, length), order);
                full &= start == 0 && length == columns && compactStart == cursor;
                cursor += length;
            }
            // A non-canonical row table (unordered, overlapping or padded) keeps its cells but is rewritten trimmed.
            layout = full && cursor == compactCount ? HeightMapLayout.FullRows : HeightMapLayout.TrimmedRows;
        }
        else
        {
            if (payload.Length != count * size * 2)
                throw new InvalidDataException("Incorrect dense payload size.");
            Endian.Decode(payload[..(count * size)], maximum.AsSpan(), order);
            Endian.Decode(payload[(count * size)..], minimum.AsSpan(), order);
        }
        bool[]? water = null;
        if (maskSize != 0)
        {
            water = new bool[count];
            ReadOnlySpan<byte> mask = data[^maskSize..];
            for (int i = 0; i < count; i++)
                water[i] = (mask[i / 8] & (1 << (i % 8))) != 0;
        }
        var bounds = new HeightMapBounds(new(Endian.F32(data[16..], order), Endian.F32(data[20..], order), Endian.F32(data[24..], order)),
            new(Endian.F32(data[28..], order), Endian.F32(data[32..], order), Endian.F32(data[36..], order)));
        var boundsIssues = new List<Diagnostic>();
        bounds.Validate(boundsIssues, null);
        new ValidationReport(boundsIssues).ThrowIfInvalid();
        var result = new HeightMap<T>(columns, rows, bounds, minimum, maximum)
        {
            WaterCells = water,
            Version = data[4],
            Reserved = Endian.U16(data[6..], order),
            Flags = flags,
            ByteOrder = order,
            Layout = layout
        };
        // Only the inverted-cell rule is requested; flags and other target limits stay unchecked, as without the option.
        result.Validate(rejectInvertedCells ? new HeightMapExportLimits { RejectInvertedCells = true, SupportedFlags = (HeightMapFlags)uint.MaxValue } : null)
            .ThrowIfInvalid();
        return result;
    }

    /// <summary>Validates and encodes a height grid without modifying it. Layout and byte order default to the grid's own
    /// <see cref="HeightMap{T}.Layout"/> and <see cref="HeightMap{T}.ByteOrder"/>. Without <paramref name="limits"/> only the
    /// structural contract is enforced; pass the target's limits to reject data it cannot load.</summary>
    public static byte[] Write<T>(HeightMap<T> map, HeightMapExportLimits? limits = null, HeightMapLayout? layout = null,
        ByteOrder? byteOrder = null) where T : unmanaged, INumber<T>
    {
        ArgumentNullException.ThrowIfNull(map);
        ByteOrder order = byteOrder ?? map.ByteOrder;
        HeightMapLayout target = layout ?? map.Layout;
        if (order is not ByteOrder.Big and not ByteOrder.Little)
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        if (!Enum.IsDefined(target))
            throw new ArgumentOutOfRangeException(nameof(layout));
        map.Validate(limits).ThrowIfInvalid();
        int size = HeightCell<T>.Size;
        bool useRle = target != HeightMapLayout.Dense;
        RowRange[] ranges = useRle ? CompactRows(map, target == HeightMapLayout.FullRows) : [];
        int compactCount = useRle ? ranges[^1].CompactStart + ranges[^1].Count : map.MinimumCells.Length;
        int tableSize = useRle ? checked(map.Rows * RowSize) : 0;
        int payloadSize = checked(tableSize + compactCount * size * 2);
        int maskSize = map.WaterCells is null ? 0 : (map.WaterCells.Length + 7) / 8;
        byte[] result = new byte[checked(HeaderSize + payloadSize + maskSize)];
        Span<byte> header = result.AsSpan(0, HeaderSize);
        (order == ByteOrder.Big ? "HMAP"u8 : "PAMH"u8).CopyTo(header);
        header[4] = map.Version;
        header[5] = (byte)size;
        Endian.Put16(header[6..], map.Reserved, order);
        HeightMapFlags flags = map.Flags & ~(HeightMapFlags.RleData | HeightMapFlags.WaterMask);
        if (useRle)
            flags |= HeightMapFlags.RleData;
        if (map.WaterCells is not null)
            flags |= HeightMapFlags.WaterMask;
        Endian.Put32(header[8..], (uint)flags, order);
        Endian.Put16(header[12..], checked((ushort)map.Columns), order);
        Endian.Put16(header[14..], checked((ushort)map.Rows), order);
        Endian.PutFloat(header[16..], map.Bounds.Minimum.X, order);
        Endian.PutFloat(header[20..], map.Bounds.Minimum.Y, order);
        Endian.PutFloat(header[24..], map.Bounds.Minimum.Z, order);
        Endian.PutFloat(header[28..], map.Bounds.Maximum.X, order);
        Endian.PutFloat(header[32..], map.Bounds.Maximum.Y, order);
        Endian.PutFloat(header[36..], map.Bounds.Maximum.Z, order);
        Endian.Put32(header[40..], (uint)payloadSize, order);
        Span<byte> payload = result.AsSpan(HeaderSize, payloadSize);
        if (useRle)
        {
            for (int row = 0; row < map.Rows; row++)
            {
                RowRange range = ranges[row];
                Span<byte> entry = payload[(row * RowSize)..];
                Endian.Put16(entry, (ushort)range.Start, order);
                Endian.Put16(entry[2..], (ushort)range.Count, order);
                int offset = range.Count == 0 ? 0 : range.CompactStart - range.Start;
                Endian.Put32(entry[4..], unchecked((uint)offset), order);
                int source = row * map.Columns + range.Start;
                int destination = tableSize + range.CompactStart * size;
                Endian.Encode<T>(map.MaximumCells.AsSpan(source, range.Count), payload[destination..], order);
                Endian.Encode<T>(map.MinimumCells.AsSpan(source, range.Count), payload[(destination + compactCount * size)..], order);
            }
        }
        else
        {
            Endian.Encode<T>(map.MaximumCells, payload, order);
            Endian.Encode<T>(map.MinimumCells, payload[(compactCount * size)..], order);
        }
        if (map.WaterCells is not null)
        {
            Span<byte> mask = result.AsSpan(HeaderSize + payloadSize);
            for (int i = 0; i < map.WaterCells.Length; i++)
                if (map.WaterCells[i])
                    mask[i / 8] |= (byte)(1 << (i % 8));
        }
        return result;
    }

    /// <summary>Atomically saves <see cref="Write{T}"/> output; an existing destination requires <paramref name="overwrite"/>.</summary>
    public static void Save<T>(HeightMap<T> map, string path, bool overwrite = false, HeightMapExportLimits? limits = null)
        where T : unmanaged, INumber<T>
    {
        byte[] bytes = Write(map, limits);
        AtomicFile.Write(path, bytes, overwrite);
    }

    private static RowRange[] CompactRows<T>(HeightMap<T> map, bool fullRows) where T : unmanaged, INumber<T>
    {
        var ranges = new RowRange[map.Rows];
        int compactCount = 0;
        for (int row = 0; row < map.Rows; row++)
        {
            if (fullRows)
            {
                ranges[row] = new(0, map.Columns, compactCount);
                compactCount += map.Columns;
                continue;
            }
            ReadOnlySpan<T> lower = map.MinimumCells.AsSpan(row * map.Columns, map.Columns);
            ReadOnlySpan<T> upper = map.MaximumCells.AsSpan(row * map.Columns, map.Columns);
            int lowerStart = lower.IndexOfAnyExcept(T.Zero), upperStart = upper.IndexOfAnyExcept(T.Zero);
            int start = Math.Min(lowerStart < 0 ? map.Columns : lowerStart, upperStart < 0 ? map.Columns : upperStart);
            int stop = Math.Max(lower.LastIndexOfAnyExcept(T.Zero), upper.LastIndexOfAnyExcept(T.Zero)) + 1;
            int count = Math.Max(0, stop - start);
            ranges[row] = new(count == 0 ? 0 : start, count, compactCount);
            compactCount += count;
        }
        return ranges;
    }
}
