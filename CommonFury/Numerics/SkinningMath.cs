using System.Numerics;

namespace CommonFury.Numerics;

/// <summary>Distance falloff of <see cref="SkinningMath.RadialWeight"/>.</summary>
public enum RadialFalloff
{
    /// <summary>Full strength anywhere inside the radius.</summary>
    Constant,
    /// <summary>Strength falls linearly to zero at the radius.</summary>
    Linear,
    /// <summary>Strength follows smoothstep, <c>t² (3 − 2t)</c>.</summary>
    Smooth
}

/// <summary>Bone influences and weighted transforms for CPU skinning.</summary>
public static class SkinningMath
{
    /// <summary>A vertex whose <see cref="WeightSum"/> is at or below this value has no bone influence: blending returns
    /// identity and batch skinning keeps the source vertex.</summary>
    public const float MinimumWeightSum = 1e-8f;

    /// <summary>Sum of the weights as given, accumulated in double precision. Nothing is validated: negative weights
    /// subtract and non-finite weights propagate, so a NaN sum fails a <c>&lt;= MinimumWeightSum</c> check.</summary>
    public static double WeightSum(ReadOnlySpan<float> weights)
    {
        double sum = 0;
        foreach (float weight in weights)
            sum += weight;
        return sum;
    }

    /// <summary>Weighted sum of the indexed transforms. A total weight at or below <see cref="MinimumWeightSum"/> returns
    /// identity; without <paramref name="normalizeWeights"/> the raw weights are used as given.</summary>
    public static Matrix4x4 Blend(ReadOnlySpan<float> weights, ReadOnlySpan<int> indices, ReadOnlySpan<Matrix4x4> transforms, bool normalizeWeights = true)
    {
        if (weights.Length == 0 || weights.Length != indices.Length)
            throw new ArgumentException("Weights and indices must have the same positive length.");
        for (int i = 0; i < weights.Length; i++)
            if (!float.IsFinite(weights[i]) || weights[i] < 0 || indices[i] < 0)
                throw new ArgumentException("Invalid skin influence.");
        double sum = WeightSum(weights);
        if (sum <= MinimumWeightSum)
            return Matrix4x4.Identity;
        Matrix4x4 result = default;
        for (int i = 0; i < weights.Length; i++)
        {
            if (weights[i] == 0)
                continue;
            if (indices[i] >= transforms.Length)
                throw new ArgumentException("Skin palette index out of range.");
            TransformMath.RequireFinite(transforms[indices[i]]);
            result += transforms[indices[i]] * (float)(weights[i] / (normalizeWeights ? sum : 1));
        }
        return result;
    }

    /// <summary>Four-influence form; indices are whole numbers stored as floats, read only where the weight is nonzero.</summary>
    public static Matrix4x4 Blend(Vector4 weights, Vector4 indices, ReadOnlySpan<Matrix4x4> transforms, bool normalizeWeights = true)
    {
        if (!VectorMath.IsFinite(weights) || !VectorMath.IsFinite(indices) || Vector4.Min(weights, Vector4.Zero) != Vector4.Zero)
            throw new ArgumentException("Invalid skin influences.");
        Span<int> ids = stackalloc int[4];
        ids.Clear();
        for (int i = 0; i < 4; i++)
        {
            if (weights[i] == 0)
                continue;
            if (indices[i] < 0 || indices[i] >= int.MaxValue || indices[i] != MathF.Truncate(indices[i]))
                throw new ArgumentException("Skin palette index out of range.");
            ids[i] = (int)indices[i];
        }
        return Blend([weights.X, weights.Y, weights.Z, weights.W], ids, transforms, normalizeWeights);
    }

    /// <summary>Sums repeated bones, keeps the <paramref name="maximum"/> strongest (ties by lower index) and normalizes them.
    /// No positive weight yields weight 1 on bone 0.</summary>
    public static (Vector4 Weights, Vector4 Indices) NormalizeInfluences(IEnumerable<KeyValuePair<int, float>> influences, int maximum = 4)
    {
        if (maximum is < 1 or > 4)
            throw new ArgumentOutOfRangeException(nameof(maximum));
        var values = influences.ToArray();
        if (values.Any(p => p.Key < 0 || !float.IsFinite(p.Value) || p.Value < 0))
            throw new ArgumentException("Invalid bone influence.");
        var ranked = values.GroupBy(p => p.Key).Select(g => new KeyValuePair<int, double>(g.Key, g.Sum(p => (double)p.Value)))
            .Where(p => p.Value > 0).OrderByDescending(p => p.Value).ThenBy(p => p.Key).Take(maximum).ToArray();
        double sum = ranked.Sum(p => p.Value);
        if (sum <= 0)
            return (new(1, 0, 0, 0), Vector4.Zero);
        Vector4 w = default, ids = default;
        for (int i = 0; i < ranked.Length; i++)
        {
            ids[i] = ranked[i].Key;
            w[i] = (float)(ranked[i].Value / sum);
        }
        return (w, ids);
    }

    /// <summary>Influence of a point inside a sphere, scaled by <paramref name="strength"/> and clamped to [0, 1].</summary>
    public static float RadialWeight(Vector3 point, Vector3 center, float radius, float strength, RadialFalloff falloff)
    {
        if (!VectorMath.IsFinite(point) || !VectorMath.IsFinite(center) || !float.IsFinite(radius) || radius <= 0 || !float.IsFinite(strength) || strength < 0)
            throw new ArgumentException("Invalid radial influence.");
        float t = Math.Clamp(1 - Vector3.Distance(point, center) / radius, 0, 1);
        float shape = falloff switch
        {
            RadialFalloff.Constant => 1,
            RadialFalloff.Linear => t,
            RadialFalloff.Smooth => t * t * (3 - 2 * t),
            _ => throw new ArgumentOutOfRangeException(nameof(falloff))
        };
        return t <= 0 ? 0 : Math.Clamp(shape * strength, 0, 1);
    }
}
