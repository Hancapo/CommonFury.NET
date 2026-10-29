using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace CommonFury.Audio;

// Polyphase windowed-sinc resampler. The Kaiser-windowed low-pass passes up to 0.9 and stops from 1.0 of the lower
// Nyquist frequency with about 90 dB of attenuation. Output sample n sits at input time n * from / to, so there is no delay.
internal sealed class Resampler
{
    private const double AttenuationDb = 90;
    private const int MaxRatio = 64;
    // Rational ratios with more phases than this interpolate linearly between adjacent phase rows.
    private const int MaxPhases = 1024;

    private readonly int step, phases, rows, taps, half;
    private readonly float[] table;

    internal Resampler(int fromRate, int toRate)
    {
        if (Math.Max(fromRate, toRate) / (double)Math.Min(fromRate, toRate) > MaxRatio)
            throw new ArgumentOutOfRangeException(nameof(toRate), $"Resampling supports rate ratios up to {MaxRatio}.");
        int divisor = GreatestCommonDivisor(fromRate, toRate);
        step = fromRate / divisor;
        phases = toRate / divisor;
        rows = Math.Min(phases, MaxPhases);

        // Frequencies in cycles per input sample.
        double nyquist = Math.Min(fromRate, toRate) / 2.0 / fromRate;
        double cutoff = 0.95 * nyquist, transition = 0.1 * nyquist;
        half = (int)Math.Ceiling((AttenuationDb - 7.95) / (14.36 * transition) / 2);
        taps = (2 * half + 3) & ~3;
        double beta = 0.1102 * (AttenuationDb - 8.7), scale = 1 / BesselI0(beta);

        table = new float[(rows + 1) * taps];
        for (int row = 0; row <= rows; row++)
        {
            var coefficients = table.AsSpan(row * taps, taps);
            double fraction = (double)row / rows, sum = 0;
            // Tap k reads input i + 1 - half + k for an output at time i + fraction.
            for (int k = 0; k < 2 * half; k++)
            {
                double t = fraction + half - 1 - k, x = t / half;
                double window = Math.Abs(x) >= 1 ? 0 : BesselI0(beta * Math.Sqrt(1 - x * x)) * scale;
                double value = 2 * cutoff * Sinc(2 * cutoff * t) * window;
                coefficients[k] = (float)value;
                sum += value;
            }
            for (int k = 0; k < 2 * half; k++)
                coefficients[k] = (float)(coefficients[k] / sum);
        }
    }

    internal long OutputLength(int inputFrames) => ((long)inputFrames * phases + step - 1) / step;

    internal float[] Process(ReadOnlySpan<float> input)
    {
        float[] output = new float[OutputLength(input.Length)];
        float[] padded = new float[input.Length + 2 * half + taps];
        input.CopyTo(padded.AsSpan(half));
        var source = padded.AsSpan();
        long whole = 0, phase = 0;
        for (int n = 0; n < output.Length; n++)
        {
            var window = source.Slice((int)whole + 1, taps);
            if (rows == phases)
                output[n] = Dot(window, table.AsSpan((int)phase * taps, taps));
            else
            {
                double position = (double)phase * rows / phases;
                int row = (int)position;
                float weight = (float)(position - row);
                float a = Dot(window, table.AsSpan(row * taps, taps)), b = Dot(window, table.AsSpan((row + 1) * taps, taps));
                output[n] = a + (b - a) * weight;
            }
            phase += step;
            whole += phase / phases;
            phase %= phases;
        }
        return output;
    }

    // Fixed-width vectors keep the summation order, and so the output, the same on every CPU.
    private static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var left = MemoryMarshal.Cast<float, Vector128<float>>(a);
        var right = MemoryMarshal.Cast<float, Vector128<float>>(b);
        var sum = Vector128<float>.Zero;
        for (int i = 0; i < left.Length; i++)
            sum += left[i] * right[i];
        return Vector128.Sum(sum);
    }

    private static int GreatestCommonDivisor(int a, int b)
    {
        while (b != 0)
            (a, b) = (b, a % b);
        return a;
    }

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, quarter = x * x / 4;
        for (int k = 1; term > sum * 1e-17; k++)
        {
            term *= quarter / ((double)k * k);
            sum += term;
        }
        return sum;
    }
}
