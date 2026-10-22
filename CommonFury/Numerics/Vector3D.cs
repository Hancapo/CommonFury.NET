using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Double-precision coordinates for decoded data that <see cref="Vector3"/> would round.</summary>
public readonly record struct Vector3D(double X, double Y, double Z)
{
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y) && double.IsFinite(Z);
    /// <summary>May round, or overflow large values to infinity.</summary>
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
}
