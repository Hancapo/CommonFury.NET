using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Samples scalar curves given as ordered (input, output) keys.</summary>
public static class CurveMath
{
    /// <summary>Linear interpolation between keys whose inputs increase; inputs outside the keys return the end values.</summary>
    public static float PiecewiseLinear(IReadOnlyList<Vector2> keys, float input)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Count == 0)
            throw new ArgumentException("At least one curve key is required.", nameof(keys));
        if (input <= keys[0].X)
            return keys[0].Y;
        for (int i = 1; i < keys.Count; i++)
            if (input <= keys[i].X)
            {
                Vector2 a = keys[i - 1], b = keys[i];
                return float.Lerp(a.Y, b.Y, (input - a.X) / (b.X - a.X));
            }
        return keys[^1].Y;
    }
}
