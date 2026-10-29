using CommonFury.Audio;
using Xunit;
using static System.Buffers.Binary.BinaryPrimitives;

namespace CommonFury.Tests;

public sealed class PcmAudioTests
{
    private static PcmAudio Pcm(int rate, int channels, params short[] samples)
    {
        byte[] data = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
            WriteInt16LittleEndian(data.AsSpan(i * 2), samples[i]);
        return new(data, rate, channels);
    }

    private static short[] Samples(PcmAudio audio)
    {
        short[] result = new short[audio.Data.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = ReadInt16LittleEndian(audio.Data[(i * 2)..]);
        return result;
    }

    [Fact]
    public void WritesCanonicalWaveHeader()
    {
        byte[] wav = Pcm(22050, 2, 1, -2, 3, -4).WriteWav();
        Assert.Equal(
            "52494646" + "2C000000" + "57415645" + "666D7420" + "10000000" + "0100" + "0200" + "22560000" + "88580100" + "0400" + "1000"
            + "64617461" + "08000000" + "0100FEFF0300FCFF",
            Convert.ToHexString(wav));
        PcmAudio read = PcmAudio.ReadWav(wav);
        Assert.Equal((22050, 2, 2), (read.SampleRate, read.Channels, read.SampleCount));
        Assert.Equal([1, -2, 3, -4], Samples(read));
    }

    [Fact]
    public void ReadsExtensiblePcmAndSkipsUnrelatedChunks()
    {
        byte[] fmt = new byte[40];
        WriteUInt16LittleEndian(fmt, 0xfffe);
        WriteUInt16LittleEndian(fmt.AsSpan(2), 1);
        WriteUInt32LittleEndian(fmt.AsSpan(4), 8000);
        WriteUInt32LittleEndian(fmt.AsSpan(8), 16000);
        WriteUInt16LittleEndian(fmt.AsSpan(12), 2);
        WriteUInt16LittleEndian(fmt.AsSpan(14), 16);
        WriteUInt16LittleEndian(fmt.AsSpan(16), 22);
        WriteUInt16LittleEndian(fmt.AsSpan(18), 16);
        Convert.FromHexString("0100000000001000800000AA00389B71").CopyTo(fmt.AsSpan(24));
        byte[] wav = Riff(Chunk("LIST", [1, 2, 3]), Chunk("fmt ", fmt), Chunk("data", [0x34, 0x12]));
        PcmAudio audio = PcmAudio.ReadWav(wav);
        Assert.Equal((8000, 1), (audio.SampleRate, audio.Channels));
        Assert.Equal([0x1234], Samples(audio));
    }

    [Fact]
    public void RejectsMalformedWave()
    {
        byte[] fmt = Convert.FromHexString("0100010040" + "1F0000803E0000" + "02001000");
        Assert.Throws<InvalidDataException>(() => PcmAudio.ReadWav("RIFX"u8));
        Assert.Throws<InvalidDataException>(() => PcmAudio.ReadWav(Riff(Chunk("data", [0, 0]))));
        Assert.Throws<InvalidDataException>(() => PcmAudio.ReadWav(Riff(Chunk("fmt ", fmt), Chunk("data", [0]))));
        Assert.Throws<InvalidDataException>(() => PcmAudio.ReadWav(Riff(Chunk("fmt ", fmt[..12]), Chunk("data", [0, 0]))));
        Assert.Throws<InvalidDataException>(() => PcmAudio.ReadWav(Riff(Chunk("fmt ", fmt), Chunk("data", [0, 0]), Chunk("data", [0, 0]))));

        byte[] oversizedChunk = Riff(Chunk("fmt ", fmt), Chunk("data", [0, 0]));
        WriteUInt32LittleEndian(oversizedChunk.AsSpan(40), 0x7fff_fff0);
        Assert.Throws<InvalidDataException>(() => PcmAudio.ReadWav(oversizedChunk));
        byte[] oversizedRiff = Riff(Chunk("fmt ", fmt), Chunk("data", [0, 0]));
        WriteUInt32LittleEndian(oversizedRiff.AsSpan(4), 0xffff_fff0);
        Assert.Throws<InvalidDataException>(() => PcmAudio.ReadWav(oversizedRiff));

        byte[] float32 = (byte[])fmt.Clone();
        float32[0] = 3;
        float32[14] = 32;
        Assert.Throws<NotSupportedException>(() => PcmAudio.ReadWav(Riff(Chunk("fmt ", float32), Chunk("data", [0, 0, 0, 0]))));
    }

    [Fact]
    public void StreamingSizesExtendToTheEnd()
    {
        byte[] wav = Pcm(8000, 1, 5, 6).WriteWav();
        WriteUInt32LittleEndian(wav.AsSpan(4), uint.MaxValue);
        WriteUInt32LittleEndian(wav.AsSpan(40), uint.MaxValue);
        Assert.Equal([5, 6], Samples(PcmAudio.ReadWav(wav)));
    }

    [Fact]
    public void SplitsAndInterleavesChannels()
    {
        PcmAudio stereo = Pcm(8000, 2, 1, 2, 3, 4, 5, 6);
        PcmAudio[] channels = [.. stereo.SplitChannels().Select(c => new PcmAudio(c, 8000))];
        Assert.Equal([1, 3, 5], Samples(channels[0]));
        Assert.Equal([2, 4, 6], Samples(channels[1]));
        Assert.Equal(Samples(stereo), Samples(PcmAudio.Interleave(channels)));
        Assert.Throws<ArgumentException>(() => PcmAudio.Interleave([channels[0], Pcm(8000, 1, 1)]));
        Assert.Throws<ArgumentException>(() => new PcmAudio(new byte[3], 8000, 1));
    }

    [Fact]
    public void DerivesSaturatedPeaksPerBlock()
    {
        PcmAudio audio = Pcm(8000, 2, 100, -300, 7, 0, short.MinValue, 1);
        Assert.Equal([600, 65535], audio.DerivePeaks(2));
    }

    [Fact]
    public void ConvertsChannelsWithTheDocumentedRules()
    {
        PcmAudio stereo = Pcm(8000, 2, 1000, 3000, -32768, -32768, 32767, 1);
        Assert.Same(stereo, stereo.Convert(8000, 2));
        Assert.Equal([2000, -32768, 16384], Samples(stereo.Convert(channels: 1)));
        Assert.Equal([7, 7, -8, -8], Samples(Pcm(8000, 1, 7, -8).Convert(channels: 2)));
        Assert.Throws<NotSupportedException>(() => Pcm(8000, 3, 1, 2, 3).Convert(channels: 2));
        Assert.Throws<NotSupportedException>(() => stereo.Convert(channels: 4));
    }

    [Fact]
    public void FromFloatMixesBeforeClipping()
    {
        PcmAudio mono = PcmAudio.FromFloat([1.5f, 0.5f, -0.25f, -0.25f], 8000, 2, targetChannels: 1);
        Assert.Equal([32767, -8192], Samples(mono));
        Assert.Equal([32767, -32768, 0], Samples(PcmAudio.FromFloat([2f, -1f, 0f], 8000)));
        Assert.Throws<ArgumentException>(() => PcmAudio.FromFloat([float.NaN], 8000));
        Assert.Throws<ArgumentException>(() => PcmAudio.FromFloat([0f, 0f, 0f], 8000, 2));
    }

    private static byte[] Chunk(string id, byte[] body)
    {
        byte[] result = new byte[8 + body.Length + (body.Length & 1)];
        System.Text.Encoding.ASCII.GetBytes(id).CopyTo(result, 0);
        WriteUInt32LittleEndian(result.AsSpan(4), (uint)body.Length);
        body.CopyTo(result, 8);
        return result;
    }

    private static byte[] Riff(params byte[][] chunks)
    {
        byte[] body = [.. "WAVE"u8, .. chunks.SelectMany(c => c)];
        byte[] result = new byte[8 + body.Length];
        "RIFF"u8.CopyTo(result);
        WriteUInt32LittleEndian(result.AsSpan(4), (uint)body.Length);
        body.CopyTo(result, 8);
        return result;
    }
}
