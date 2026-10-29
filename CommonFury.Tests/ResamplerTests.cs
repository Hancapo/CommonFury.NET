using CommonFury.Audio;
using Xunit;
using static System.Buffers.Binary.BinaryPrimitives;

namespace CommonFury.Tests;

// Resamples pure tones at half scale and fits the expected sine to the output. In-band tones must keep their gain with
// little residual (SINAD); tones above the new Nyquist frequency must disappear (anything left is aliasing).
public sealed class ResamplerTests
{
    private const double Amplitude = 16384;
    // Edge frames where the zero-padded filter rings are not measured.
    private const int Margin = 400;

    [Theory]
    [InlineData(1000, 44100, 22050)]
    [InlineData(5000, 44100, 22050)]
    [InlineData(9000, 44100, 22050)]
    [InlineData(9900, 44100, 22050)]
    [InlineData(1000, 44100, 48000)]
    [InlineData(9000, 44100, 48000)]
    [InlineData(19800, 44100, 48000)]
    [InlineData(19800, 48000, 44100)]
    [InlineData(1000, 32000, 22050)]
    [InlineData(9900, 32000, 22050)]
    [InlineData(3000, 8000, 44100)]
    [InlineData(5000, 44100, 44101)]
    public void KeepsInBandTonesFlatAndClean(int frequency, int from, int to)
    {
        double[] output = Resample(frequency, from, to);
        var (amplitude, residual) = FitSine(output, frequency, to);
        double gain = 20 * Math.Log10(amplitude / Amplitude), sinad = 10 * Math.Log10(amplitude * amplitude / 2 / residual);
        Assert.True(Math.Abs(gain) <= 0.5, $"gain {gain:F2} dB");
        Assert.True(sinad >= 70, $"SINAD {sinad:F1} dB");
    }

    [Theory]
    [InlineData(15000, 44100, 22050)]
    [InlineData(11500, 44100, 22050)]
    [InlineData(14000, 32000, 22050)]
    [InlineData(23000, 48000, 44100)]
    public void RejectsTonesAboveTheNewNyquist(int frequency, int from, int to)
    {
        double[] output = Resample(frequency, from, to);
        double power = output.Average(s => s * s);
        double alias = 10 * Math.Log10(power / (Amplitude * Amplitude / 2));
        Assert.True(alias <= -70, $"alias {alias:F1} dB");
    }

    [Fact]
    public void KeepsTimingAndLength()
    {
        PcmAudio impulse = PcmAudio.FromFloat([0, 0, 0, 0, 0.5f, 0, 0, 0, 0, 0], 8000);
        PcmAudio up = impulse.Convert(16000);
        Assert.Equal(20, up.SampleCount);
        short[] samples = [.. Enumerable.Range(0, 20).Select(i => ReadInt16LittleEndian(up.Data[(i * 2)..]))];
        Assert.Equal(8, Array.IndexOf(samples, samples.Max()));

        Assert.Equal(441, PcmAudio.FromFloat(new float[1000], 32000).Convert(14112).SampleCount);
        Assert.Equal(0, PcmAudio.FromFloat([], 44100).Convert(48000).SampleCount);
        Assert.Throws<ArgumentOutOfRangeException>(() => impulse.Convert(8000 * 65));
    }

    [Fact]
    public void ResamplesStereoChannelsIndependently()
    {
        float[] interleaved = new float[2 * 4410];
        for (int i = 0; i < 4410; i++)
        {
            interleaved[i * 2] = (float)(0.5 * Math.Sin(2 * Math.PI * 1000 * i / 44100));
            interleaved[i * 2 + 1] = 0;
        }
        PcmAudio stereo = PcmAudio.FromFloat(interleaved, 44100, 2, 48000);
        Assert.Equal((48000, 2, 4800), (stereo.SampleRate, stereo.Channels, stereo.SampleCount));
        for (int i = 0; i < stereo.SampleCount; i++)
            Assert.Equal(0, ReadInt16LittleEndian(stereo.Data[(i * 4 + 2)..]));
    }

    private static double[] Resample(int frequency, int from, int to)
    {
        float[] tone = new float[from];
        for (int i = 0; i < tone.Length; i++)
            tone[i] = (float)(Amplitude / 32768 * Math.Sin(2 * Math.PI * frequency * i / from));
        PcmAudio output = PcmAudio.FromFloat(tone, from).Convert(to);
        Assert.Equal(((long)tone.Length * to + from - 1) / from, output.SampleCount);
        return [.. Enumerable.Range(Margin, output.SampleCount - 2 * Margin).Select(i => (double)ReadInt16LittleEndian(output.Data[(i * 2)..]))];
    }

    // Least-squares fit of a·sin + b·cos + c at the known frequency; returns the amplitude and the mean residual power.
    private static (double Amplitude, double Residual) FitSine(double[] y, double frequency, int rate)
    {
        double[,] m = new double[3, 3];
        double[] v = new double[3];
        for (int i = 0; i < y.Length; i++)
        {
            double w = 2 * Math.PI * frequency * (i + Margin) / rate;
            double[] basis = [Math.Sin(w), Math.Cos(w), 1];
            for (int r = 0; r < 3; r++)
            {
                v[r] += y[i] * basis[r];
                for (int c = 0; c < 3; c++)
                    m[r, c] += basis[r] * basis[c];
            }
        }
        double[] x = Solve(m, v);
        double residual = 0;
        for (int i = 0; i < y.Length; i++)
        {
            double w = 2 * Math.PI * frequency * (i + Margin) / rate, e = y[i] - (x[0] * Math.Sin(w) + x[1] * Math.Cos(w) + x[2]);
            residual += e * e;
        }
        return (Math.Sqrt(x[0] * x[0] + x[1] * x[1]), residual / y.Length);
    }

    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        for (int k = 0; k < n; k++)
            for (int i = k + 1; i < n; i++)
            {
                double f = a[i, k] / a[k, k];
                for (int j = k; j < n; j++)
                    a[i, j] -= f * a[k, j];
                b[i] -= f * b[k];
            }
        double[] x = new double[n];
        for (int i = n - 1; i >= 0; i--)
        {
            double s = b[i];
            for (int j = i + 1; j < n; j++)
                s -= a[i, j] * x[j];
            x[i] = s / a[i, i];
        }
        return x;
    }
}
