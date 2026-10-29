using System.Numerics;
using System.Runtime.InteropServices;
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
        return ConvertCore(Samples(), SampleRate, Channels, targetRate, targetChannels);
    }

    public byte[][] SplitChannels()
    {
        // Moving whole 16-bit samples keeps their stored byte order on any host.
        int channels = Channels, frames = SampleCount;
        var samples = MemoryMarshal.Cast<byte, short>(data);
        byte[][] result = new byte[channels][];
        for (int c = 0; c < channels; c++)
        {
            result[c] = new byte[frames * 2];
            var plane = MemoryMarshal.Cast<byte, short>(result[c].AsSpan());
            for (int f = 0, i = c; f < frames; f++, i += channels)
                plane[f] = samples[i];
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
        int count = channels.Count;
        byte[] result = new byte[checked(samples * count * 2)];
        var interleaved = MemoryMarshal.Cast<byte, short>(result.AsSpan());
        for (int c = 0; c < count; c++)
        {
            var plane = MemoryMarshal.Cast<byte, short>(channels[c].data);
            for (int s = 0, i = c; s < samples; s++, i += count)
                interleaved[i] = plane[s];
        }
        return new(result, rate, count);
    }

    /// <summary>Twice the peak absolute amplitude of each block of <paramref name="blockSamples"/> frames, saturated to <see cref="ushort"/>.</summary>
    public ushort[] DerivePeaks(int blockSamples = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(blockSamples);
        ushort[] peaks = new ushort[(int)((SampleCount + (long)blockSamples - 1) / blockSamples)];
        var samples = Samples();
        int blockLength = (int)Math.Min((long)blockSamples * Channels, samples.Length);
        for (int b = 0; b < peaks.Length; b++)
        {
            var block = samples[..Math.Min(blockLength, samples.Length)];
            peaks[b] = (ushort)Math.Min(65535, PeakAmplitude(block) * 2);
            samples = samples[block.Length..];
        }
        return peaks;
    }

    private static int PeakAmplitude(ReadOnlySpan<short> samples)
    {
        // Both bounds start at zero because only the larger magnitude is returned.
        short min = 0, max = 0;
        int i = 0;
        if (Vector.IsHardwareAccelerated && samples.Length >= Vector<short>.Count)
        {
            Vector<short> low = Vector<short>.Zero, high = Vector<short>.Zero;
            foreach (var vector in MemoryMarshal.Cast<short, Vector<short>>(samples))
            {
                low = Vector.Min(low, vector);
                high = Vector.Max(high, vector);
            }
            for (int lane = 0; lane < Vector<short>.Count; lane++)
            {
                min = Math.Min(min, low[lane]);
                max = Math.Max(max, high[lane]);
            }
            i = samples.Length - samples.Length % Vector<short>.Count;
        }
        for (; i < samples.Length; i++)
        {
            min = Math.Min(min, samples[i]);
            max = Math.Max(max, samples[i]);
        }
        return Math.Max(-min, max);
    }

    private static PcmAudio ConvertCore<T>(ReadOnlySpan<T> samples, int sampleRate, int channels, int targetRate, int targetChannels)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(targetRate);
        RequireChannels(targetChannels, nameof(targetChannels));
        if (targetChannels != channels && (channels, targetChannels) is not ((2, 1) or (1, 2)))
            throw new NotSupportedException("Automatic channel conversion supports mono/stereo only; keep the original channels and arrange them explicitly for other layouts.");

        int frames = samples.Length / channels, planes = Math.Min(channels, targetChannels), outputFrames = frames;
        Resampler? resampler = null;
        if (targetRate != sampleRate)
        {
            resampler = new Resampler(sampleRate, targetRate);
            long resampled = resampler.OutputLength(frames);
            if (resampled * targetChannels * 2 > Array.MaxLength)
                throw new ArgumentOutOfRangeException(nameof(targetRate), "The resampled audio would not fit in one array.");
            outputFrames = (int)resampled;
        }
        else if ((long)frames * targetChannels * 2 > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(targetChannels), "The converted audio would not fit in one array.");

        // Mix down before resampling and duplicate after it, so only the needed channels are filtered. Planes go one at a
        // time through one buffer whose zero padding feeds the filter.
        float[] buffer = new float[resampler?.PaddedLength(frames) ?? frames];
        var plane = buffer.AsSpan(resampler?.Lead ?? 0, frames);
        Span<float> converted = resampler is null ? plane : new float[outputFrames];
        byte[] pcm = new byte[outputFrames * targetChannels * 2];
        var output = MemoryMarshal.Cast<byte, short>(pcm.AsSpan());
        for (int p = 0; p < planes; p++)
        {
            if (planes < channels)
                for (int f = 0; f < frames; f++)
                    plane[f] = (ToFloat(samples[f * 2]) + ToFloat(samples[f * 2 + 1])) * 0.5f;
            else
                for (int f = 0, i = p; f < frames; f++, i += channels)
                    plane[f] = ToFloat(samples[i]);
            resampler?.Process(buffer, converted);

            // The last plane also fills the channels that mono duplicates.
            int end = p == planes - 1 ? targetChannels : p + 1;
            for (int f = 0, i = p; f < outputFrames; f++, i += targetChannels)
            {
                short sample = Quantize(converted[f]);
                if (!BitConverter.IsLittleEndian)
                    sample = ReverseEndianness(sample);
                for (int c = 0; c < end - p; c++)
                    output[i + c] = sample;
            }
        }
        return new(pcm, targetRate, targetChannels);
    }

    // PCM16 samples scale by 1/32768; float samples pass through unchanged.
    private static float ToFloat<T>(T sample) where T : unmanaged
        => typeof(T) == typeof(short) ? (short)(object)sample / 32768f : (float)(object)sample;

    // Samples are stored little-endian; big-endian hosts read a byte-swapped copy.
    private ReadOnlySpan<short> Samples()
    {
        var samples = MemoryMarshal.Cast<byte, short>(data);
        if (BitConverter.IsLittleEndian)
            return samples;
        short[] swapped = new short[samples.Length];
        ReverseEndianness(samples, swapped);
        return swapped;
    }

    private static short Quantize(float sample) => (short)Math.Clamp(MathF.Round(sample * 32768f), short.MinValue, short.MaxValue);

    private static void RequireChannels(int channels, string name)
    {
        if (channels is < 1 or > ushort.MaxValue / 2)
            throw new ArgumentOutOfRangeException(name);
    }
}
