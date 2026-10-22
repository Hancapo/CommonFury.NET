using System.Numerics;
using System.Text;

namespace CommonFury.Binary;

/// <summary>Little-endian <see cref="BinaryWriter"/> with vector, padding and alignment helpers. Leaves the stream open.</summary>
public class BinaryStreamWriter(Stream output) : BinaryWriter(output, Encoding.UTF8, leaveOpen: true)
{
    public void Write(Vector2 value) { Span<byte> data = stackalloc byte[8]; var writer = new BinarySpanWriter(data); writer.WriteVector2(value); Write(data); }
    public void Write(Vector3 value) { Span<byte> data = stackalloc byte[12]; var writer = new BinarySpanWriter(data); writer.WriteVector3(value); Write(data); }
    public void Write(Vector4 value) { Span<byte> data = stackalloc byte[16]; var writer = new BinarySpanWriter(data); writer.WriteVector4(value); Write(data); }
    /// <summary>Pads with zeros to a multiple of <paramref name="alignment"/>. Requires a seekable stream.</summary>
    public void Align(int alignment) => FixedBytes([], checked((int)(BinaryLayout.Align(BaseStream.Position, alignment) - BaseStream.Position)));
    /// <summary>Writes <paramref name="source"/> zero-padded to <paramref name="size"/> bytes; rejects a longer source.</summary>
    public void FixedBytes(ReadOnlySpan<byte> source, int size)
    {
        if (source.Length > size)
            throw new InvalidDataException("Fixed-width byte field exceeds its capacity.");
        Write(source);
        int remaining = size - source.Length;
        Span<byte> zeros = stackalloc byte[64];
        zeros.Clear();
        while (remaining > 0)
        { int count = Math.Min(remaining, zeros.Length); Write(zeros[..count]); remaining -= count; }
    }
}
