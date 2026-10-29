using CommonFury.Binary;
using CommonFury.IO;
using static System.Buffers.Binary.BinaryPrimitives;

namespace CommonFury.Audio;

public sealed partial class PcmAudio
{
    private static readonly Guid PcmSubformat = new("00000001-0000-0010-8000-00aa00389b71");

    /// <summary>Writes a canonical 44-byte-header PCM16 RIFF/WAVE file.</summary>
    public byte[] WriteWav()
    {
        byte[] result = new byte[checked(44 + data.Length)];
        var w = new BinarySpanWriter(result);
        w.WriteBytes("RIFF"u8);
        w.WriteUInt32((uint)result.Length - 8);
        w.WriteBytes("WAVEfmt "u8);
        w.WriteUInt32(16);
        w.WriteUInt16(1);
        w.WriteUInt16((ushort)Channels);
        w.WriteUInt32((uint)SampleRate);
        w.WriteUInt32(checked((uint)(SampleRate * (long)Channels * 2)));
        w.WriteUInt16((ushort)(Channels * 2));
        w.WriteUInt16(16);
        w.WriteBytes("data"u8);
        w.WriteUInt32((uint)data.Length);
        w.WriteBytes(data);
        return result;
    }

    /// <summary>Writes the WAVE file, replacing <paramref name="path"/> atomically where supported.</summary>
    public void SaveWav(string path) => AtomicFile.Write(path, WriteWav());

    /// <summary>Reads PCM16 RIFF/WAVE, including extensible PCM, and skips unrelated chunks.</summary>
    /// <remarks>A RIFF or data size of 0xFFFFFFFF, written by streaming encoders, extends to the end of the input.</remarks>
    /// <exception cref="InvalidDataException">The input is not well-formed WAVE.</exception>
    /// <exception cref="NotSupportedException">The samples are not PCM16; decode them with an <see cref="IAudioDecoder"/>.</exception>
    public static PcmAudio ReadWav(ReadOnlySpan<byte> source)
    {
        BinaryLayout.Require(source.Length >= 12 && source[..4].SequenceEqual("RIFF"u8) && source.Slice(8, 4).SequenceEqual("WAVE"u8), "Expected RIFF/WAVE audio.");
        uint riffSize = ReadUInt32LittleEndian(source[4..]);
        long declared = riffSize == uint.MaxValue ? source.Length : riffSize + 8L;
        BinaryLayout.Require(declared >= 12 && declared <= source.Length, "WAVE RIFF size is invalid.");
        var riff = source[..(int)declared];
        int channels = 0, rate = 0;
        ReadOnlySpan<byte> pcm = default;
        bool hasData = false;
        for (int offset = 12; offset < riff.Length;)
        {
            var header = BinaryLayout.Slice(riff, offset, 8);
            var id = header[..4];
            uint size = ReadUInt32LittleEndian(header[4..]);
            long available = riff.Length - offset - 8L;
            long count = size == uint.MaxValue && id.SequenceEqual("data"u8) ? available : size;
            BinaryLayout.Require(count <= available, $"WAVE chunk at {offset} exceeds the RIFF data.");
            var chunk = riff.Slice(offset + 8, (int)count);
            if (id.SequenceEqual("fmt "u8))
            {
                BinaryLayout.Require(channels == 0 && chunk.Length >= 16, "WAVE format chunk is duplicated or truncated.");
                var r = new BinarySpanReader(chunk);
                int tag = r.ReadUInt16();
                channels = r.ReadUInt16();
                uint samplesPerSecond = r.ReadUInt32(), bytesPerSecond = r.ReadUInt32();
                int frameSize = r.ReadUInt16(), bits = r.ReadUInt16();
                if (tag == 0xfffe)
                {
                    BinaryLayout.Require(chunk.Length >= 40 && ReadUInt16LittleEndian(chunk[16..]) >= 22, "Extensible WAVE format is truncated.");
                    tag = chunk.Slice(24, 16).SequenceEqual(PcmSubformat.ToByteArray()) && ReadUInt16LittleEndian(chunk[18..]) == 16 ? 1 : 0;
                }
                if (tag != 1 || bits != 16)
                    throw new NotSupportedException("Managed WAVE reading requires PCM16; decode other encodings with an IAudioDecoder.");
                BinaryLayout.Require(channels is > 0 and <= ushort.MaxValue / 2 && samplesPerSecond is > 0 and <= int.MaxValue
                    && frameSize == channels * 2 && bytesPerSecond == samplesPerSecond * (long)frameSize, "Inconsistent WAVE format.");
                rate = (int)samplesPerSecond;
            }
            else if (id.SequenceEqual("data"u8))
            {
                BinaryLayout.Require(!hasData, "Multiple WAVE data chunks are not supported.");
                pcm = chunk;
                hasData = true;
            }
            offset = (int)Math.Min(riff.Length, offset + 8 + count + (count & 1));
        }
        BinaryLayout.Require(channels > 0 && hasData && pcm.Length % (channels * 2) == 0, "WAVE is missing a format/data chunk or has partial frames.");
        return new(pcm, rate, channels);
    }
}
