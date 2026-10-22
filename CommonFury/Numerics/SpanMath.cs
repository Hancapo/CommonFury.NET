using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Vectorized span checks with scalar tails; results never depend on hardware acceleration.</summary>
public static class SpanMath
{
    public static bool IsFinite(Vector<float> values) =>
        Vector.LessThanOrEqualAll(Vector.Abs(values), new Vector<float>(float.MaxValue));

    // Reports the first failure so callers can keep their exact diagnostic order.
    /// <summary>Index of the first nonfinite or inverted lower/upper pair, or the length when all are valid.</summary>
    public static int FirstInvalidRange(ReadOnlySpan<float> lower, ReadOnlySpan<float> upper)
    {
        if (lower.Length != upper.Length)
            throw new ArgumentException("Span lengths must match.");
        int i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            int width = Vector<float>.Count;
            for (; i <= lower.Length - width; i += width)
            {
                var minimum = new Vector<float>(lower.Slice(i, width));
                var maximum = new Vector<float>(upper.Slice(i, width));
                if (!IsFinite(minimum) || !IsFinite(maximum) || !Vector.LessThanOrEqualAll(minimum, maximum))
                    break;
            }
        }
        for (; i < lower.Length; i++)
            if (!float.IsFinite(lower[i]) || !float.IsFinite(upper[i]) || lower[i] > upper[i])
                return i;
        return lower.Length;
    }

    // NaN handling is deliberately separate: callers validating floating-point data check finiteness.
    /// <summary>Index of the first element where left exceeds right, or the length. NaN never compares greater.</summary>
    public static int FirstGreaterThan<T>(ReadOnlySpan<T> left, ReadOnlySpan<T> right)
        where T : unmanaged, INumber<T>
    {
        if (left.Length != right.Length)
            throw new ArgumentException("Span lengths must match.");
        int i = 0;
        if (Vector.IsHardwareAccelerated && Vector<T>.IsSupported)
        {
            int width = Vector<T>.Count;
            for (; i <= left.Length - width; i += width)
                if (Vector.GreaterThanAny(new Vector<T>(left.Slice(i, width)), new Vector<T>(right.Slice(i, width))))
                    break;
        }
        for (; i < left.Length; i++)
            if (left[i] > right[i])
                return i;
        return left.Length;
    }
}
