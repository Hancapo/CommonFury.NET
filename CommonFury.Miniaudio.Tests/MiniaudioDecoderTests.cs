using CommonFury.Audio;
using Xunit;
using static System.Buffers.Binary.BinaryPrimitives;

namespace CommonFury.Miniaudio.Tests;

// tone.flac and tone.ogg were encoded by ffmpeg from the PCM16 tone that Tone() generates: 11025 stereo frames at
// 44.1 kHz, left 440 Hz at amplitude 12000, right 1000 Hz at amplitude 8000.
public sealed class MiniaudioDecoderTests
{
    private const int Frames = 11025;
    private static readonly MiniaudioDecoder Decoder = new();

    private static byte[] Fixture(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "ReferenceData", name));

    private static short[] Tone()
    {
        short[] result = new short[Frames * 2];
        for (int i = 0; i < Frames; i++)
        {
            result[i * 2] = (short)Math.Round(12000 * Math.Sin(2 * Math.PI * 440 * i / 44100));
            result[i * 2 + 1] = (short)Math.Round(8000 * Math.Sin(2 * Math.PI * 1000 * i / 44100));
        }
        return result;
    }

    private static short[] Samples(PcmAudio audio)
    {
        short[] result = new short[audio.Data.Length / 2];
        for (int i = 0; i < result.Length; i++)
            result[i] = ReadInt16LittleEndian(audio.Data[(i * 2)..]);
        return result;
    }

    [Theory]
    [InlineData("flac")]
    [InlineData(".FLAC")]
    [InlineData(null)]
    public void DecodesFlacExactly(string? format)
    {
        PcmAudio audio = Decoder.Decode(Fixture("tone.flac"), format);
        Assert.Equal((44100, 2, Frames), (audio.SampleRate, audio.Channels, audio.SampleCount));
        Assert.Equal(Tone(), Samples(audio));
    }

    [Fact]
    public void DecodesVorbisCloseToTheSource()
    {
        PcmAudio audio = Decoder.Decode(Fixture("tone.ogg"));
        Assert.Equal((44100, 2), (audio.SampleRate, audio.Channels));
        Assert.InRange(audio.SampleCount, Frames - 128, Frames);
        short[] expected = Tone(), actual = Samples(audio);
        double signal = 0, error = 0;
        for (int i = 2048; i < 2 * (audio.SampleCount - 1024); i++)
        {
            signal += (double)expected[i] * expected[i];
            error += (double)(expected[i] - actual[i]) * (expected[i] - actual[i]);
        }
        Assert.True(10 * Math.Log10(signal / error) >= 30, $"SNR {10 * Math.Log10(signal / error):F1} dB");
    }

    [Fact]
    public void DecodesWaveEncodings()
    {
        short[] tone = Tone();
        byte[] pcm = new byte[tone.Length * 2];
        for (int i = 0; i < tone.Length; i++)
            WriteInt16LittleEndian(pcm.AsSpan(i * 2), tone[i]);
        Assert.Equal(tone, Samples(Decoder.Decode(new PcmAudio(pcm, 44100, 2).WriteWav())));

        // IEEE float WAV: one mono frame per value.
        float[] values = [0.5f, -0.25f, 1.5f];
        byte[] wav = new byte[44 + values.Length * 4];
        "RIFF"u8.CopyTo(wav);
        WriteUInt32LittleEndian(wav.AsSpan(4), (uint)wav.Length - 8);
        "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        WriteUInt32LittleEndian(wav.AsSpan(16), 16);
        WriteUInt16LittleEndian(wav.AsSpan(20), 3);
        WriteUInt16LittleEndian(wav.AsSpan(22), 1);
        WriteUInt32LittleEndian(wav.AsSpan(24), 8000);
        WriteUInt32LittleEndian(wav.AsSpan(28), 32000);
        WriteUInt16LittleEndian(wav.AsSpan(32), 4);
        WriteUInt16LittleEndian(wav.AsSpan(34), 32);
        "data"u8.CopyTo(wav.AsSpan(36));
        WriteUInt32LittleEndian(wav.AsSpan(40), (uint)values.Length * 4);
        for (int i = 0; i < values.Length; i++)
            WriteSingleLittleEndian(wav.AsSpan(44 + i * 4), values[i]);
        PcmAudio audio = Decoder.Decode(wav, "wav");
        Assert.Equal((8000, 1), (audio.SampleRate, audio.Channels));
        Assert.Equal([16384, -8192, 32767], Samples(audio));
    }

    [Fact]
    public void ConvertsRateAndChannelsOnDecode()
    {
        PcmAudio audio = Decoder.Decode(Fixture("tone.flac"), "flac", 22050, 1);
        Assert.Equal((22050, 1, (Frames + 1) / 2), (audio.SampleRate, audio.Channels, audio.SampleCount));
        PcmAudio stereo = Decoder.Decode(Fixture("tone.ogg"), "ogg", 48000, 2);
        Assert.Equal((48000, 2), (stereo.SampleRate, stereo.Channels));
    }

    [Theory]
    [InlineData("mp3")]
    [InlineData(".m4a")]
    [InlineData("aac")]
    [InlineData("wma")]
    [InlineData("opus")]
    [InlineData("aiff")]
    public void RejectsUnsupportedFormatsWithAdvice(string format)
    {
        var exception = Assert.Throws<NotSupportedException>(() => Decoder.Decode(Fixture("tone.flac"), format));
        Assert.Contains("convert it to WAV, Ogg Vorbis or FLAC", exception.Message);
    }

    [Fact]
    public void RejectsUnrecognizedContent()
    {
        byte[] opus = new byte[64];
        "OggS"u8.CopyTo(opus);
        opus[26] = 1;
        opus[27] = 19;
        "OpusHead"u8.CopyTo(opus.AsSpan(28));
        Assert.Throws<NotSupportedException>(() => Decoder.Decode(opus));
        Assert.Throws<NotSupportedException>(() => Decoder.Decode("ID3\u0004\0\0\0\0\0\0"u8));
        Assert.Throws<NotSupportedException>(() => Decoder.Decode("\0\0\0\u0018ftypM4A "u8));
    }

    [Fact]
    public void RejectsCorruptInputAndExcessOutput()
    {
        Assert.Throws<InvalidDataException>(() => Decoder.Decode(new byte[100], "flac"));
        Assert.Throws<InvalidDataException>(() => Decoder.Decode("RIFF\u0004\0\0\0WAVEjunk"u8));
        Assert.Throws<InvalidDataException>(() => new MiniaudioDecoder(Frames * 4 - 2).Decode(Fixture("tone.flac")));
        Assert.Throws<InvalidDataException>(() => new MiniaudioDecoder(Frames * 4).Decode(Fixture("tone.flac"), sampleRate: 48000));
        Assert.Throws<ArgumentException>(() => new MiniaudioDecoder(100).Decode(Fixture("tone.flac")));
        Assert.Throws<ArgumentException>(() => Decoder.Decode([]));
    }
}
