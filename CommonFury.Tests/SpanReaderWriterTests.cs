using System.Numerics;
using System.Text;
using CommonFury.Binary;
using Xunit;

namespace CommonFury.Tests;

public sealed class SpanReaderWriterTests
{
    [Fact]
    public void ScalarsVectorsAndMatricesRoundTripInBothByteOrders()
    {
        foreach (ByteOrder order in Enum.GetValues<ByteOrder>())
        {
            var bytes = new byte[256];
            var writer = new BinarySpanWriter(bytes, order);
            writer.WriteSByte(-9);
            writer.WriteUInt16(0x1234);
            writer.WriteInt16(-1234);
            writer.WriteUInt32(0x89ABCDEF);
            writer.WriteInt32(int.MinValue);
            writer.WriteUInt64(ulong.MaxValue);
            writer.WriteInt64(long.MinValue);
            writer.WriteSingle(12.25f);
            writer.WriteDouble(-1.125);
            writer.WriteHalf((Half)1.5f);
            writer.WriteVector2(new(4, 5));
            writer.WriteVector3(new(1, 2, 3));
            writer.WriteVector4(new(6, 7, 8, 9));
            writer.WriteQuaternion(Quaternion.Identity);
            writer.WriteMatrix4x4(Matrix4x4.CreateTranslation(1, 2, 3));
            writer.WriteZeros(3);
            var reader = new BinarySpanReader(bytes, order);
            Assert.True(reader.ReadSByte() == -9 && reader.ReadUInt16() == 0x1234 && reader.ReadInt16() == -1234, "Small scalar roundtrip.");
            Assert.True(reader.ReadUInt32() == 0x89ABCDEF && reader.ReadInt32() == int.MinValue, "Int32 roundtrip.");
            Assert.True(reader.ReadUInt64() == ulong.MaxValue && reader.ReadInt64() == long.MinValue, "Int64 roundtrip.");
            Assert.True(reader.ReadSingle() == 12.25f && reader.ReadDouble() == -1.125 && reader.ReadHalf() == (Half)1.5f, "Floating scalars.");
            Assert.Equal(new Vector2(4, 5), reader.ReadVector2());
            Assert.Equal(new Vector3(1, 2, 3), reader.ReadVector3());
            Assert.Equal(new Vector4(6, 7, 8, 9), reader.ReadVector4());
            Assert.Equal(Quaternion.Identity, reader.ReadQuaternion());
            Assert.Equal(Matrix4x4.CreateTranslation(1, 2, 3), reader.ReadMatrix4x4());
            Assert.Equal(writer.Position, reader.Position + 3);
            Assert.True(bytes[1] == (order == ByteOrder.Big ? 0x12 : 0x34), "Independent endian bytes.");
            Assert.ThrowsAny<InvalidDataException>(() => { var r = new BinarySpanReader(new byte[3]); r.ReadUInt32(); });
            Assert.ThrowsAny<InvalidDataException>(() => { var r = new BinarySpanReader(new byte[3]); r.Seek(-1); });
            Assert.ThrowsAny<ArgumentException>(() => { var w = new BinarySpanWriter(new byte[3]); w.WriteUInt32(1); });
        }
    }

    [Fact]
    public void FixedStringsEitherStopAtNulOrKeepEveryByte()
    {
        byte[] field = [.. "name"u8, 0, .. "pad"u8, 0];
        var reader = new BinarySpanReader(field);
        Assert.Equal("name", reader.ReadString(field.Length, Encoding.UTF8));
        reader.Seek(0);
        Assert.Equal("name\0pad\0", reader.ReadStringVerbatim(field.Length, Encoding.UTF8));
        Assert.Equal(0, reader.Remaining);
        reader.Seek(0);
        Assert.Throws<InvalidDataException>(() => { var r = new BinarySpanReader(field); r.ReadStringVerbatim(field.Length + 1, Encoding.UTF8); });
    }

    [Fact]
    public void StreamWriterEncodesVectorsPadsAndAligns()
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryStreamWriter(stream))
        {
            writer.Write((byte)1);
            writer.Align(4);
            writer.Write(new Vector3(1, 2, 3));
            writer.FixedBytes("ab"u8, 4);
            Assert.Throws<InvalidDataException>(() => writer.FixedBytes("abcde"u8, 4));
        }
        Assert.True(stream.CanWrite, "The writer must leave its stream open.");
        byte[] bytes = stream.ToArray();
        Assert.Equal(20, bytes.Length);
        Assert.Equal(new byte[] { 1, 0, 0, 0 }, bytes[..4]);
        var reader = new BinarySpanReader(bytes.AsSpan(4));
        Assert.Equal(new Vector3(1, 2, 3), reader.ReadVector3());
        Assert.Equal(new byte[] { (byte)'a', (byte)'b', 0, 0 }, bytes[16..]);
    }
}
