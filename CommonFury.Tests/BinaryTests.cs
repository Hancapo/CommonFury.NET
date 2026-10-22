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
