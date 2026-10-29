using System.Buffers.Binary;
using System.Runtime.InteropServices;
using CommonFury.Binary;
using Xunit;

namespace CommonFury.Tests;

public sealed class BinaryTests
{
    [Fact]
    public void ArraysRoundTripInBothByteOrders()
    {
        foreach (ByteOrder order in Enum.GetValues<ByteOrder>())
        {
            var bytes = new byte[12];
            uint[] input = [1, 0x11223344, uint.MaxValue];
            BinaryArrays.Write<uint>(input, bytes, order);
            Assert.Equal(order == ByteOrder.Big ? 0x11 : 0x44, bytes[4]);
            uint[] output = new uint[3];
            BinaryArrays.Read(bytes, output.AsSpan(), order);
            Assert.Equal(input, output);
            var strided = new byte[20];
            for (int i = 0; i < 3; i++)
                bytes.AsSpan(i * 4, 4).CopyTo(strided.AsSpan(i * 8));
            Array.Clear(output);
            BinaryArrays.Read(strided, output.AsSpan(), order, stride: 8);
            Assert.Equal(input, output);
            Assert.Throws<InvalidDataException>(() => BinaryArrays.Read(strided.AsSpan(0, 19), output.AsSpan(), order, stride: 8));
        }
        Assert.ThrowsAny<NotSupportedException>(() => BinaryArrays.Write<decimal>([1], new byte[16], ByteOrder.Big));
    }

    [Fact]
    public void EndianScalarsUseTheRequestedOrder()
    {
        var bytes = new byte[8];
        Endian.Put32(bytes, 0x01020304, ByteOrder.Big);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes[..4]);
        Assert.Equal(0x04030201u, Endian.U32(bytes, ByteOrder.Little));
        Endian.Put16(bytes, 0x0102, ByteOrder.Little);
        Assert.Equal(0x0201, Endian.U16(bytes, ByteOrder.Big));
        Endian.Put64(bytes, 0x0102030405060708, ByteOrder.Big);
        Assert.Equal(0x0807060504030201ul, Endian.U64(bytes, ByteOrder.Little));
        Endian.PutFloat(bytes, -1.5f, ByteOrder.Big);
        Assert.Equal(-1.5f, Endian.F32(bytes, ByteOrder.Big));
    }

    [Fact]
    public void LayoutAlignsAndChecksRanges()
    {
        Assert.True(BinaryLayout.Align(17, 16) == 32 && BinaryLayout.Align(7, 3) == 9, "General alignment.");
        Assert.ThrowsAny<OverflowException>(() => BinaryLayout.Align(int.MaxValue, 16));
        Assert.Equal(4_294_967_808L, BinaryLayout.Align(4_294_967_297L, 512));
        Assert.Throws<OverflowException>(() => BinaryLayout.Align(long.MaxValue, 16));
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => BinaryLayout.Align(1, 0));
        BinaryLayout.RequireRange(10, 10, 20);
        Assert.Throws<InvalidDataException>(() => BinaryLayout.RequireRange(long.MaxValue, 10, 20));
        Assert.Throws<InvalidDataException>(() => BinaryLayout.RequireRange(11, 10, 20));
        Assert.Equal(2, BinaryLayout.Slice(new byte[4], 2, 2).Length);
        Assert.Throws<InvalidDataException>(() => BinaryLayout.Slice(new byte[4], 3, 2));
        Assert.Throws<InvalidDataException>(() => BinaryLayout.Require(false, "broken"));
    }

    [Fact]
    public void LayoutErrorsNameTheRejectedRange()
    {
        Assert.Equal("Binary range 3+2 exceeds 4 bytes.",
            Assert.Throws<InvalidDataException>(() => BinaryLayout.Slice(new byte[4], 3, 2)).Message);
        Assert.Equal("Binary range -1+0 exceeds 4 bytes.",
            Assert.Throws<InvalidDataException>(() => BinaryLayout.Slice(new byte[4], -1, 0)).Message);
        Assert.Equal("Binary range 11+10 lies outside its 20-byte allocation.",
            Assert.Throws<InvalidDataException>(() => BinaryLayout.RequireRange(11, 10, 20)).Message);
        Assert.Equal("broken", Assert.Throws<InvalidDataException>(() => BinaryLayout.Require(false, "broken")).Message);
        BinaryLayout.Require(true, "unused");
    }

    [Fact]
    public void StridedArraysMatchIndependentDecodingForEverySize()
    {
        var random = new Random(11);
        foreach (ByteOrder order in Enum.GetValues<ByteOrder>())
        {
            bool big = order == ByteOrder.Big;
            foreach (int extra in new[] { 1, 3, 8 })
            {
                CheckStrided<byte>(1, extra, order, random, b => b[0]);
                CheckStrided<sbyte>(1, extra, order, random, b => unchecked((sbyte)b[0]));
                CheckStrided<short>(2, extra, order, random, b => big ? BinaryPrimitives.ReadInt16BigEndian(b) : BinaryPrimitives.ReadInt16LittleEndian(b));
                CheckStrided<ushort>(2, extra, order, random, b => big ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b));
                CheckStrided<Half>(2, extra, order, random, b => BitConverter.UInt16BitsToHalf(big ? BinaryPrimitives.ReadUInt16BigEndian(b) : BinaryPrimitives.ReadUInt16LittleEndian(b)));
                CheckStrided<int>(4, extra, order, random, b => big ? BinaryPrimitives.ReadInt32BigEndian(b) : BinaryPrimitives.ReadInt32LittleEndian(b));
                CheckStrided<uint>(4, extra, order, random, b => big ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b));
                CheckStrided<float>(4, extra, order, random, b => BitConverter.UInt32BitsToSingle(big ? BinaryPrimitives.ReadUInt32BigEndian(b) : BinaryPrimitives.ReadUInt32LittleEndian(b)));
                CheckStrided<long>(8, extra, order, random, b => big ? BinaryPrimitives.ReadInt64BigEndian(b) : BinaryPrimitives.ReadInt64LittleEndian(b));
                CheckStrided<ulong>(8, extra, order, random, b => big ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b));
                CheckStrided<double>(8, extra, order, random, b => BitConverter.UInt64BitsToDouble(big ? BinaryPrimitives.ReadUInt64BigEndian(b) : BinaryPrimitives.ReadUInt64LittleEndian(b)));
            }
        }
    }

    private static void CheckStrided<T>(int size, int extra, ByteOrder order, Random random, Func<byte[], T> decode) where T : unmanaged
    {
        int stride = size + extra, count = 5;
        // One leading byte keeps elements unaligned; the last element ends exactly at the input end.
        var data = new byte[1 + (count - 1) * stride + size];
        random.NextBytes(data);
        var output = new T[count];
        BinaryArrays.Read(data.AsSpan(1), output.AsSpan(), order, stride);
        for (int i = 0; i < count; i++)
        {
            T expected = decode(data[(1 + i * stride)..(1 + i * stride + size)]);
            Assert.True(MemoryMarshal.AsBytes(new[] { expected }.AsSpan()).SequenceEqual(MemoryMarshal.AsBytes(output.AsSpan(i, 1))),
                $"{typeof(T).Name} element {i} with stride {stride} in {order} order.");
        }
        Assert.Throws<InvalidDataException>(() => BinaryArrays.Read(data.AsSpan(1, data.Length - 2), output.AsSpan(), order, stride));
        BinaryArrays.Read(data.AsSpan(1), Span<T>.Empty, order, stride);
    }

    [Fact]
    public void LsbBitReaderMatchesBitByBitDecodingAtEveryOffsetAndWidth()
    {
        var data = new byte[13];
        new Random(3).NextBytes(data);
        var reader = new BinaryBitReader(data);
        for (int width = 0; width <= 32; width++)
        {
            for (long position = 0; position <= reader.Length - width; position++)
            {
                uint expected = 0;
                for (int i = 0; i < width; i++)
                    expected |= (uint)((data[(position + i) >> 3] >> (int)((position + i) & 7)) & 1) << i;
                Assert.Equal(expected, reader.ReadUInt32At(position, width));
                reader.Seek(position);
                Assert.Equal(expected, reader.ReadUInt32(width));
                Assert.Equal(position + width, reader.Position);
            }
            long past = reader.Length - width + 1;
            Assert.Throws<InvalidDataException>(() => new BinaryBitReader(data).ReadUInt32At(past, width));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new BinaryBitReader(data).ReadUInt32At(0, 33));
    }

    [Fact]
    public void MsbBitReaderMatchesBitByBitDecodingAtEveryOffsetAndWidth()
    {
        var data = new byte[13];
        new Random(4).NextBytes(data);
        for (int width = 0; width <= 32; width++)
        {
            for (int position = 0; position <= data.Length * 8 - width; position++)
            {
                uint expected = 0;
                for (int i = position; i < position + width; i++)
                    expected = expected << 1 | (uint)((data[i >> 3] >> (7 - (i & 7))) & 1);
                // Starting the input at the field's first byte keeps its distance from the end unchanged.
                var reader = new MsbBitReader(data.AsSpan(position >> 3));
                reader.ReadUInt32(position & 7);
                Assert.Equal(expected, reader.ReadUInt32(width));
                Assert.Equal((position & 7) + width, reader.Position);
            }
            if (width == 0)
                continue;
            Assert.Throws<InvalidDataException>(() =>
            {
                var reader = new MsbBitReader(data.AsSpan(data.Length - 4));
                reader.ReadUInt32(33 - width);
                reader.ReadUInt32(width);
            });
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => new MsbBitReader(data).ReadUInt32(33));
    }

    [Fact]
    public void LsbBitWriterMatchesBitByBitEncodingAtEveryOffsetAndWidth()
    {
        var random = new Random(6);
        var dirty = new byte[13];
        random.NextBytes(dirty);
        for (int width = 0; width <= 32; width++)
        {
            for (long position = 0; position <= dirty.Length * 8L - width; position++)
            {
                uint value = width == 0 ? 0 : (uint)random.NextInt64(0, 1L << 32) >> (32 - width);
                byte[] expected = (byte[])dirty.Clone();
                for (int i = 0; i < width; i++)
                {
                    long bit = position + i;
                    int mask = 1 << (int)(bit & 7);
                    expected[bit >> 3] = (byte)(((value >> i) & 1) != 0 ? expected[bit >> 3] | mask : expected[bit >> 3] & ~mask);
                }
                byte[] actual = (byte[])dirty.Clone();
                var writer = new BinaryBitWriter(actual);
                writer.Seek(position);
                writer.WriteUInt32(value, width);
                Assert.Equal(expected, actual);
                Assert.Equal(position + width, writer.Position);
            }
            if (width == 0)
                continue;
            long past = dirty.Length * 8L - width + 1;
            byte[] untouched = (byte[])dirty.Clone();
            Assert.Throws<ArgumentException>(() => { var w = new BinaryBitWriter(untouched); w.Seek(past); w.WriteUInt32(0, width); });
            Assert.Equal(dirty, untouched);
        }
    }

    [Fact]
    public void LsbBitFieldsRoundTripAcrossBytes()
    {
        var bytes = new byte[8];
        var writer = new BinaryBitWriter(bytes);
        writer.WriteUInt32(0b101, 3);
        writer.WriteUInt32(0x1ABCD, 17);
        writer.WriteUInt32(uint.MaxValue, 32);
        Assert.Equal(52, writer.Position);
        Assert.Throws<ArgumentOutOfRangeException>(() => { var w = new BinaryBitWriter(new byte[1]); w.WriteUInt32(4, 2); });
        Assert.Throws<ArgumentException>(() => { var w = new BinaryBitWriter(new byte[1]); w.WriteUInt32(0, 9); });
        var reader = new BinaryBitReader(bytes);
        Assert.Equal(0b101u, reader.ReadUInt32(3));
        Assert.Equal(0x1ABCDu, reader.ReadUInt32(17));
        Assert.Equal(uint.MaxValue, reader.ReadUInt32(32));
        Assert.Equal(0x1ABCDu, reader.ReadUInt32At(3, 17));
        Assert.Equal(12, reader.Remaining);
        Assert.Throws<InvalidDataException>(() => { var r = new BinaryBitReader(new byte[1]); r.ReadUInt32(9); });
    }

    [Fact]
    public void WriterReplacesBitsInDirtyStorage()
    {
        // Unlike an OR-based writer, existing destination bits never leak into the written field.
        var bytes = new byte[] { 0xFF, 0xFF };
        var writer = new BinaryBitWriter(bytes);
        writer.WriteUInt32(0, 4);
        writer.WriteUInt32(0b1010, 4);
        Assert.Equal(0xA0, bytes[0]);
        Assert.Equal(0xFF, bytes[1]);
    }

    [Fact]
    public void MsbBitReaderReadsHighBitsFirst()
    {
        var reader = new MsbBitReader([0b1011_0010, 0b1100_0000]);
        Assert.Equal(0b101u, reader.ReadUInt32(3));
        Assert.Equal(0b10010_11u, reader.ReadUInt32(7));
        Assert.Equal(6, reader.Remaining);
        Assert.Throws<InvalidDataException>(() => { var r = new MsbBitReader([1]); r.ReadUInt32(9); });
    }

    [Fact]
    public void BitFieldsExtractReplaceAndReverse()
    {
        Assert.Equal(0xBu, BitField32.Extract(0xAB, 0, 4));
        Assert.Equal(0x1Bu, BitField32.Replace(0xAB, 4, 4, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BitField32.Replace(0, 0, 2, 4));
        Assert.Equal(6u, BitField32.ReverseLowBits(3, 3));
        var random = new Random(5);
        for (int i = 0; i < 200; i++)
        {
            uint value = (uint)random.NextInt64(0, 1L << 32);
            int width = random.Next(1, 33);
            Assert.Equal(TextbookReverse(value, width), BitField32.ReverseLowBits(value, width));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => BitField32.ReverseLowBits(1, 0));
    }

    private static uint TextbookReverse(uint value, int width)
    {
        uint result = 0;
        for (int i = 0; i < width; i++)
            result |= ((value >> i) & 1) << (width - 1 - i);
        return result;
    }
}
