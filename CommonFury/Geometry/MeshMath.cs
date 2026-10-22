using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Triangle-mesh validation and derived vertex normals and tangents.</summary>
public static class MeshMath
{
    /// <summary>Rejects an index count that is not a multiple of three and indices outside [0, <paramref name="vertexCount"/>).</summary>
    public static void ValidateTriangleIndices(ReadOnlySpan<int> indices, int vertexCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(vertexCount);
        if (indices.Length % 3 != 0)
            throw new ArgumentException("Triangle index count must be divisible by three.", nameof(indices));
        if (!indices.IsEmpty && (vertexCount == 0 || indices.ContainsAnyExceptInRange(0, vertexCount - 1)))
            throw new ArgumentException("Triangle references a vertex outside positions.", nameof(indices));
    }

    /// <summary>Area-weighted unit vertex normals; vertices without area get +Z.</summary>
    public static Vector3[] GenerateNormals(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, float epsilon = 1e-8f)
    {
        ValidatePositions(positions, indices);
        VectorMath.RequireEpsilon(epsilon);
        var normals = new Vector3[positions.Length];
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            Vector3 normal = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
            normals[a] += normal;
            normals[b] += normal;
            normals[c] += normal;
        }
        for (int i = 0; i < normals.Length; i++)
            normals[i] = VectorMath.NormalizeOr(normals[i], Vector3.UnitZ, epsilon);
        return normals;
    }

    /// <summary>Unit tangents orthogonalized against the normals, with handedness in W (+1 or −1). Triangles with
    /// degenerate UVs are skipped; vertices left without a tangent get an arbitrary direction perpendicular to the normal.</summary>
    public static Vector4[] GenerateTangents(ReadOnlySpan<Vector3> positions, ReadOnlySpan<Vector3> normals,
        ReadOnlySpan<Vector2> texcoords, ReadOnlySpan<int> indices, float epsilon = 1e-8f)
    {
        ValidatePositions(positions, indices);
        VectorMath.RequireEpsilon(epsilon);
        if (normals.Length != positions.Length || texcoords.Length != positions.Length)
            throw new ArgumentException("Attribute counts must match positions.");
        for (int i = 0; i < positions.Length; i++)
            if (!VectorMath.IsFinite(normals[i]) || !VectorMath.IsFinite(texcoords[i]))
                throw new ArgumentException("Attributes must be finite.");
        var tangent = new Vector3[positions.Length];
        var bitangent = new Vector3[positions.Length];
        for (int i = 0; i < indices.Length; i += 3)
        {
            int a = indices[i], b = indices[i + 1], c = indices[i + 2];
            if (!PlaneMath.TryUvBasis(positions[a], positions[b], positions[c], texcoords[a], texcoords[b], texcoords[c], out var basis, epsilon))
                continue;
            Vector3 t = basis.Width, bt = basis.Height;
            tangent[a] += t;
            tangent[b] += t;
            tangent[c] += t;
            bitangent[a] += bt;
            bitangent[b] += bt;
            bitangent[c] += bt;
        }
        var result = new Vector4[positions.Length];
        for (int i = 0; i < result.Length; i++)
        {
            Vector3 normal = VectorMath.NormalizeOr(normals[i], Vector3.UnitZ, epsilon);
            Vector3 fallback = JointFrameMath.OrthogonalAxes(normal).First;
            Vector3 t = VectorMath.NormalizeOr(tangent[i] - normal * Vector3.Dot(normal, tangent[i]), fallback, epsilon);
            result[i] = new(t, Vector3.Dot(Vector3.Cross(normal, t), bitangent[i]) >= 0 ? 1 : -1);
        }
        return result;
    }

    private static void ValidatePositions(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices)
    {
        ValidateTriangleIndices(indices, positions.Length);
        foreach (Vector3 point in positions)
            if (!VectorMath.IsFinite(point))
                throw new ArgumentException("Positions must be finite.", nameof(positions));
    }
}
