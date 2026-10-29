using static System.Buffers.Binary.BinaryPrimitives;

namespace CommonFury.Audio;

/// <summary>Owned, immutable, interleaved little-endian PCM16 audio.</summary>
public sealed partial class PcmAudio
{
    private readonly byte[] data;

    /// <summary>Copies <paramref name="pcm"/>, which must hold whole interleaved frames.</summary>
    public PcmAudio(ReadOnlySpan<byte> pcm, int sampleRate, int channels = 1)
        : this(pcm.ToArray(), sampleRate, channels)
    {
    }

    private PcmAudio(byte[] pcm, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        RequireChannels(channels, nameof(channels));
        if (pcm.Length % (channels * 2) != 0)
            throw new ArgumentException("PCM16 must contain complete interleaved frames.", nameof(pcm));
        data = pcm;
        SampleRate = sampleRate;
        Channels = channels;
    }

    public ReadOnlySpan<byte> Data => data;
    public int SampleRate { get; }
    public int Channels { get; }
    /// <summary>Samples per channel, equal to the frame count.</summary>
    public int SampleCount => data.Length / (Channels * 2);
    /// <summary>Duration in seconds.</summary>
    public double Duration => (double)SampleCount / SampleRate;

    /// <summary>Quantizes interleaved samples in [-1, 1] to PCM16, optionally changing the sample rate and channel count first.</summary>
    /// <remarks>
    /// Channel mixing and resampling run in floating point before quantization, so a mix of channels that exceed full scale
    /// is not clipped per channel. Samples outside [-1, 1] are clipped after the conversion. See <see cref="Convert"/> for the
    /// conversion rules.
    /// </remarks>
    /// <exception cref="ArgumentException">A sample is NaN or infinite, or the samples hold partial frames.</exception>
    public static PcmAudio FromFloat(ReadOnlySpan<float> samples, int sampleRate, int channels = 1, int? targetSampleRate = null, int? targetChannels = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        RequireChannels(channels, nameof(channels));
        if (samples.Length % channels != 0)
            throw new ArgumentException("Samples must contain complete interleaved frames.", nameof(samples));
        foreach (float sample in samples)
            if (!float.IsFinite(sample))
                throw new ArgumentException("Samples must be finite.", nameof(samples));
        return ConvertCore(samples, sampleRate, channels, targetSampleRate ?? sampleRate, targetChannels ?? channels);
    }

    /// <summary>Returns audio at <paramref name="sampleRate"/> with <paramref name="channels"/> channels, or this instance when both already match.</summary>
    /// <remarks>
    /// Stereo becomes mono by an equal-weight mix and mono becomes stereo by duplication; other channel-count changes throw
    /// <see cref="NotSupportedException"/>. Resampling uses a windowed-sinc low-pass filter that is flat to 90% of the lower
    /// Nyquist frequency and rejects content above it, with zero delay. The output has ceil(SampleCount × new rate / old rate) frames.
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The rate ratio exceeds 64, or the result would not fit in one array.</exception>
    public PcmAudio Convert(int? sampleRate = null, int? channels = null)
    {
        int targetRate = sampleRate ?? SampleRate, targetChannels = channels ?? Channels;
        if (targetRate == SampleRate && targetChannels == Channels)
            return this;
        float[] samples = new float[data.Length / 2];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = ReadInt16LittleEndian(data.AsSpan(i * 2)) / 32768f;
        return ConvertCore(samples, SampleRate, Channels, targetRate, targetChannels);
    }

    public byte[][] SplitChannels()
    {
        byte[][] result = new byte[Channels][];
        for (int c = 0; c < Channels; c++)
        {
            result[c] = new byte[SampleCount * 2];
            for (int s = 0; s < SampleCount; s++)
                data.AsSpan((s * Channels + c) * 2, 2).CopyTo(result[c].AsSpan(s * 2));
        }
        return result;
    }

    /// <summary>Interleaves mono inputs that share sample rate and sample count.</summary>
    public static PcmAudio Interleave(IReadOnlyList<PcmAudio> channels)
    {
        ArgumentNullException.ThrowIfNull(channels);
        if (channels.Count == 0)
            throw new ArgumentException("At least one channel is required.", nameof(channels));
        int samples = channels[0].SampleCount, rate = channels[0].SampleRate;
        foreach (var channel in channels)
            if (channel.Channels != 1 || channel.SampleCount != samples || channel.SampleRate != rate)
                throw new ArgumentException("Channels must be mono with matching duration and sample rate.", nameof(channels));
        byte[] result = new byte[checked(samples * channels.Count * 2)];
        for (int s = 0; s < samples; s++)
            for (int c = 0; c < channels.Count; c++)
                channels[c].Data.Slice(s * 2, 2).CopyTo(result.AsSpan((s * channels.Count + c) * 2));
        return new(result, rate, channels.Count);
    }

    /// <summary>Twice the peak absolute amplitude of each block of <paramref name="blockSamples"/> frames, saturated to <see cref="ushort"/>.</summary>
    public ushort[] DerivePeaks(int blockSamples = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSamples);
        ushort[] peaks = new ushort[(int)((SampleCount + (long)blockSamples - 1) / blockSamples)];
        for (int i = 0; i < data.Length; i += 2)
        {
            int block = i / 2 / Channels / blockSamples;
            int amplitude = Math.Min(65535, Math.Abs((int)ReadInt16LittleEndian(data.AsSpan(i))) * 2);
            peaks[block] = Math.Max(peaks[block], (ushort)amplitude);
        }
        return peaks;
    }

    private static PcmAudio ConvertCore(ReadOnlySpan<float> samples, int sampleRate, int channels, int targetRate, int targetChannels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);
        RequireChannels(targetChannels, nameof(targetChannels));
        if (targetChannels != channels && (channels, targetChannels) is not ((2, 1) or (1, 2)))
            throw new NotSupportedException("Automatic channel conversion supports mono/stereo only; keep the original channels and arrange them explicitly for other layouts.");

        // Mix down before resampling and duplicate after it, so only the needed channels are filtered.
        int frames = samples.Length / channels, planes = Math.Min(channels, targetChannels);
        var planar = new float[planes][];
        for (int c = 0; c < planes; c++)
            planar[c] = new float[frames];
        for (int f = 0; f < frames; f++)
            if (planes < channels)
                planar[0][f] = (samples[f * 2] + samples[f * 2 + 1]) * 0.5f;
            else
                for (int c = 0; c < channels; c++)
                    planar[c][f] = samples[f * channels + c];

        if (targetRate != sampleRate)
        {
            var resampler = new Resampler(sampleRate, targetRate);
            long outputFrames = resampler.OutputLength(frames);
            if (outputFrames * targetChannels * 2 > Array.MaxLength)
                throw new ArgumentOutOfRangeException(nameof(targetRate), "The resampled audio would not fit in one array.");
            for (int c = 0; c < planes; c++)
                planar[c] = resampler.Process(planar[c]);
            frames = (int)outputFrames;
        }
        else if ((long)frames * targetChannels * 2 > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(targetChannels), "The converted audio would not fit in one array.");

        byte[] pcm = new byte[frames * targetChannels * 2];
        for (int f = 0; f < frames; f++)
            for (int c = 0; c < targetChannels; c++)
                WriteInt16LittleEndian(pcm.AsSpan((f * targetChannels + c) * 2), Quantize(planar[Math.Min(c, planes - 1)][f]));
        return new(pcm, targetRate, targetChannels);
    }

    private static short Quantize(float sample) => (short)Math.Clamp(MathF.Round(sample * 32768f), short.MinValue, short.MaxValue);

    private static void RequireChannels(int channels, string name)
    {
        if (channels is < 1 or > ushort.MaxValue / 2)
            throw new ArgumentOutOfRangeException(name);
    }
}
