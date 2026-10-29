using CommonFury.Audio;
using Xunit;
using static System.Buffers.Binary.BinaryPrimitives;

namespace CommonFury.Miniaudio.Tests;

// tone.flac and tone.ogg were encoded by ffmpeg from the PCM16 tone that Tone() generates: 11025 stereo frames at
// 44.1 kHz, left 440 Hz at amplitude 12000, right 1000 Hz at amplitude 8000. tone.mp3 holds 13 tagless mono MPEG-1
// Layer III frames at 44.1 kHz and 96 kbit/s with the bit reservoir disabled, encoded by ffmpeg (LAME) from 0.3 s of
// 1000 Hz at amplitude 12000.
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
    [InlineData(null)]
    public void DecodesTaglessMp3Frames(string? format)
    {
        PcmAudio audio = Decoder.Decode(Fixture("tone.mp3"), format);
        Assert.Equal((44100, 1, 13 * 1152), (audio.SampleRate, audio.Channels, audio.SampleCount));
        // Past the encoder delay and before the final frame, the output is the source tone.
        var (amplitude, residual) = FitSine(Samples(audio)[2048..12288], 1000, 44100);
        Assert.InRange(amplitude, 12000 * 0.95, 12000 * 1.05);
        Assert.True(10 * Math.Log10(amplitude * amplitude / 2 / residual) >= 30, "MP3 tone SINAD");

        byte[] tagged = [.. "ID3\u0004\0\0\0\0\0\0"u8, .. Fixture("tone.mp3")];
        Assert.Equal(audio.Data, Decoder.Decode(tagged).Data);
    }

    [Fact]
    public void DecodesShortMp3RunsIndependently()
    {
        byte[] mp3 = Fixture("tone.mp3");
        short[] continuous = Samples(Decoder.Decode(mp3, "mp3"));
        int[] offsets = Mp3FrameOffsets(mp3);
        foreach (var (first, count) in new[] { (0, 1), (5, 1), (12, 1), (5, 3) })
        {
            byte[] run = mp3[offsets[first]..offsets[first + count]];
            PcmAudio audio = Decoder.Decode(run, "mp3", 44100, 1);
            Assert.Equal(count * 1152, audio.SampleCount);
            Assert.Equal(audio.Data, Decoder.Decode(run).Data);
            // Only the first frame lacks the previous frame's overlap; later frames match the continuous decode.
            Assert.Equal(continuous.AsSpan((first + 1) * 1152, (count - 1) * 1152), Samples(audio).AsSpan(1152));
        }
    }

    [Theory]
    [InlineData(".m4a")]
    [InlineData("aac")]
    [InlineData("wma")]
    [InlineData("opus")]
    [InlineData("aiff")]
    public void RejectsUnsupportedFormatsWithAdvice(string format)
    {
        var exception = Assert.Throws<NotSupportedException>(() => Decoder.Decode(Fixture("tone.flac"), format));
        Assert.Contains("convert it to WAV, MP3, Ogg Vorbis or FLAC", exception.Message);
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
        Assert.Throws<NotSupportedException>(() => Decoder.Decode("\0\0\0\u0018ftypM4A "u8));
        Assert.Throws<NotSupportedException>(() => Decoder.Decode([0xff, 0xf1, 0x50, 0x80, 0, 0x1f, 0xfc]));
    }

    [Fact]
    public void RejectsCorruptInputAndExcessOutput()
    {
        Assert.Throws<InvalidDataException>(() => Decoder.Decode(new byte[100], "flac"));
        Assert.Throws<InvalidDataException>(() => Decoder.Decode("RIFF\u0004\0\0\0WAVEjunk"u8));
        Assert.Throws<InvalidDataException>(() => Decoder.Decode("ID3\u0004\0\0\0\0\0\0"u8));
        Assert.Throws<InvalidDataException>(() => Decoder.Decode(new byte[100], "mp3"));
        Assert.Throws<InvalidDataException>(() => new MiniaudioDecoder(Frames * 4 - 2).Decode(Fixture("tone.flac")));
        Assert.Throws<InvalidDataException>(() => new MiniaudioDecoder(Frames * 4).Decode(Fixture("tone.flac"), sampleRate: 48000));
        Assert.Throws<ArgumentException>(() => new MiniaudioDecoder(100).Decode(Fixture("tone.flac")));
        Assert.Throws<ArgumentException>(() => Decoder.Decode([]));
    }

    // Byte offsets of each MPEG-1 Layer III frame and of the end, from the header bitrate, rate and padding bits.
    private static int[] Mp3FrameOffsets(byte[] data)
    {
        int[] bitRates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320], sampleRates = [44100, 48000, 32000];
        var offsets = new List<int> { 0 };
        for (int offset = 0; offset < data.Length;)
        {
            uint header = ReadUInt32BigEndian(data.AsSpan(offset));
            Assert.Equal(0xfffbu, header >> 16 | 1);
            offset += 144000 * bitRates[(header >> 12) & 15] / sampleRates[(header >> 10) & 3] + (int)((header >> 9) & 1);
            offsets.Add(offset);
        }
        return [.. offsets];
    }

    // Least-squares fit of a·sin + b·cos at the known frequency; returns the amplitude and the mean residual power.
    private static (double Amplitude, double Residual) FitSine(short[] y, double frequency, int rate)
    {
        double ss = 0, cc = 0, sc = 0, ys = 0, yc = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double w = 2 * Math.PI * frequency * i / rate, s = Math.Sin(w), c = Math.Cos(w);
            (ss, cc, sc, ys, yc) = (ss + s * s, cc + c * c, sc + s * c, ys + y[i] * s, yc + y[i] * c);
        }
        double det = ss * cc - sc * sc, a = (ys * cc - yc * sc) / det, b = (yc * ss - ys * sc) / det, residual = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double w = 2 * Math.PI * frequency * i / rate, e = y[i] - a * Math.Sin(w) - b * Math.Cos(w);
            residual += e * e;
        }
        return (Math.Sqrt(a * a + b * b), residual / y.Length);
    }
}
