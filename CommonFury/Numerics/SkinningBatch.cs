using System.Numerics;
using System.Runtime.InteropServices;

namespace CommonFury.Numerics;

/// <summary>Validated copy of static skinning inputs, deformed repeatedly into caller-owned buffers.
/// Any positive influence count; row-vector matrices; normals use the blended linear transform, then normalization.</summary>
public sealed class SkinningBatch
{
    private readonly Vector3[] positions, normals;
    private readonly int[] indices;
    private readonly float[] weights;

    /// <summary>Copies the inputs. Influences are vertex-major, <paramref name="influencesPerVertex"/> per vertex;
    /// empty <paramref name="normals"/> skips normal deformation.</summary>
    public SkinningBatch(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, ReadOnlySpan<float> weights,
        int influencesPerVertex, ReadOnlySpan<Vector3> normals = default, bool normalizeWeights = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(influencesPerVertex);
        if (indices.Length != checked(positions.Length * influencesPerVertex) || weights.Length != indices.Length ||
            !normals.IsEmpty && normals.Length != positions.Length)
            throw new ArgumentException("Skinning input shapes do not match.");
        foreach (var value in positions)
            if (!VectorMath.IsFinite(value))
                throw new ArgumentException("Positions must be finite.");
        foreach (var value in normals)
            if (!VectorMath.IsFinite(value))
                throw new ArgumentException("Normals must be finite.");
        for (int i = 0; i < indices.Length; i++)
            if (indices[i] < 0 || !float.IsFinite(weights[i]) || weights[i] < 0)
                throw new ArgumentException("Influences need nonnegative indices and finite nonnegative weights.");
        this.positions = positions.ToArray();
        this.normals = normals.ToArray();
        this.indices = indices.ToArray();
        this.weights = weights.ToArray();
        InfluencesPerVertex = influencesPerVertex;
        NormalizeWeights = normalizeWeights;
    }

    public int VertexCount => positions.Length;
    public int InfluencesPerVertex { get; }
    /// <summary>Whether positive weights are divided by their sum during deformation.</summary>
    public bool NormalizeWeights { get; }
    public bool HasNormals => normals.Length != 0;

    /// <summary>Writes one position and, when present, one normal per vertex without allocating. Outputs must not overlap
    /// each other or <paramref name="matrices"/>; invalid input is rejected before anything is written. Vertices whose
    /// weights sum to at most <see cref="SkinningMath.MinimumWeightSum"/> keep their source values. Concurrent calls need
    /// disjoint outputs.</summary>
    public void Skin(ReadOnlySpan<Matrix4x4> matrices, Span<Vector3> outputPositions, Span<Vector3> outputNormals = default)
    {
        if (outputPositions.Length != VertexCount || outputNormals.Length != normals.Length)
            throw new ArgumentException("Output shapes must match this batch.");
        if (outputPositions.Overlaps(outputNormals) || MemoryMarshal.AsBytes(matrices).Overlaps(MemoryMarshal.AsBytes(outputPositions)) ||
            MemoryMarshal.AsBytes(matrices).Overlaps(MemoryMarshal.AsBytes(outputNormals)))
            throw new ArgumentException("Skinning outputs must not overlap matrices or each other.");
        foreach (var matrix in matrices)
            TransformMath.RequireFinite(matrix);
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            var vertexWeights = weights.AsSpan(vertex * InfluencesPerVertex, InfluencesPerVertex);
            if (SkinningMath.WeightSum(vertexWeights) <= SkinningMath.MinimumWeightSum)
                continue;
            for (int lane = 0; lane < InfluencesPerVertex; lane++)
                if (vertexWeights[lane] > 0 && indices[vertex * InfluencesPerVertex + lane] >= matrices.Length)
                    throw new ArgumentException("Weighted bone index is outside the matrix array.", nameof(matrices));
        }
        for (int vertex = 0; vertex < VertexCount; vertex++)
        {
            var vertexWeights = weights.AsSpan(vertex * InfluencesPerVertex, InfluencesPerVertex);
            if (SkinningMath.WeightSum(vertexWeights) <= SkinningMath.MinimumWeightSum)
            {
                outputPositions[vertex] = positions[vertex];
                if (HasNormals)
                    outputNormals[vertex] = normals[vertex];
                continue;
            }
            var transform = SkinningMath.Blend(vertexWeights, indices.AsSpan(vertex * InfluencesPerVertex, InfluencesPerVertex), matrices, NormalizeWeights);
            outputPositions[vertex] = Vector3.Transform(positions[vertex], transform);
            if (HasNormals)
            {
                var normal = Vector3.TransformNormal(normals[vertex], transform);
                float length = normal.Length();
                outputNormals[vertex] = length > SkinningMath.MinimumWeightSum ? normal / length : normal;
            }
        }
    }
}
