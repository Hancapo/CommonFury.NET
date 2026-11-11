using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Targets and constraints for <see cref="MeshSimplification.Simplify"/>. Simplification stops at whichever target it
/// reaches first.</summary>
/// <param name="TargetTriangleCount">Stop once at most this many triangles remain; zero leaves only the error limit.</param>
/// <param name="TargetError">Largest error, in position units, that a collapse may reach; <see cref="float.PositiveInfinity"/>
/// leaves only the triangle target.</param>
/// <param name="LockBorders">Keep every vertex on an open boundary in place, as when simplifying one tile of a larger surface
/// whose edges must still meet its neighbours.</param>
public readonly record struct MeshSimplificationOptions(int TargetTriangleCount, float TargetError, bool LockBorders = false);

/// <summary>The triangles that remain after simplification, as indices into the source vertices.</summary>
public sealed class SimplifiedMesh
{
    internal SimplifiedMesh(int[] indices, float error)
    {
        Indices = indices;
        Error = error;
    }

    /// <summary>Owned triangle indices into the source vertex arrays, in source triangle order.</summary>
    public int[] Indices { get; }

    /// <summary>Largest error of any collapse, in position units: the area-weighted RMS distance of a kept vertex to the planes of
    /// the source triangles it replaced, with weighted attribute differences counted as distance.</summary>
    public float Error { get; }
}

/// <summary>Reduces triangle meshes by collapsing edges onto existing vertices in order of a quadric error metric, so the result
/// reuses source vertices and their attributes unchanged.</summary>
/// <remarks>
/// <para>Attributes come in two kinds. Measured attributes, such as texture coordinates, take part in the error metric, and each
/// has a weight that converts its differences into distance: with weight <c>w</c>, an attribute error of <c>1 / w</c> costs as much
/// as a distance of 1. Carried attributes, such as hard-edged normals, tangents or colours, never limit simplification: a corner
/// that moves takes the copy of its new vertex whose carried attributes are nearest to those it had.</para>
/// <para>Vertices with equal positions and measured attributes act as one vertex. Where measured attributes differ at a position,
/// the triangles between them form a seam: seam vertices only move along their seam, and both sides move together. Open-boundary
/// vertices only move along the boundary. Positions shared by more than two seam sides, boundary corners that are also seams, and
/// non-manifold vertices never move. Collapses that would turn a triangle by more than about 75° are rejected.</para>
/// <para>Values that are passed as neither kind are not seen: vertices that differ only in them are interchangeable.</para>
/// <para>Degenerate triangles, including those whose corners share a position, are removed. The output triangle count can stay
/// above the target when constraints or the error limit stop further collapses.</para>
/// </remarks>
public static partial class MeshSimplification
{
    /// <summary>Most per-vertex attributes that may take part in the error metric.</summary>
    public const int MaximumAttributes = 32;

    /// <summary>Simplifies a triangle list. Inputs are not retained.</summary>
    /// <param name="positions">Vertex positions; must be finite.</param>
    /// <param name="indices">Triangle list into <paramref name="positions"/>.</param>
    /// <param name="options">Targets and constraints.</param>
    /// <param name="attributes">Measured attributes, <paramref name="attributeWeights"/>.Length floats per vertex, or empty.</param>
    /// <param name="attributeWeights">One finite, nonnegative weight per measured attribute.</param>
    /// <param name="carriedAttributes">Carried attributes, the same number of finite floats for every vertex, or empty.</param>
    /// <param name="lockedVertices">Vertices that must keep their position, one flag per vertex, or empty.</param>
    /// <exception cref="ArgumentException">Lengths do not match, an index is out of range, or a value is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A target or weight is negative, or there are too many attributes.</exception>
    public static SimplifiedMesh Simplify(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, MeshSimplificationOptions options,
        ReadOnlySpan<float> attributes = default, ReadOnlySpan<float> attributeWeights = default, ReadOnlySpan<float> carriedAttributes = default,
        ReadOnlySpan<bool> lockedVertices = default)
    {
        if (positions.IsEmpty ? !carriedAttributes.IsEmpty : carriedAttributes.Length % positions.Length != 0)
            throw new ArgumentException("Carried attributes must hold the same number of values for every vertex.", nameof(carriedAttributes));
        for (int i = 0; i < carriedAttributes.Length; i++)
            if (!float.IsFinite(carriedAttributes[i]))
                throw new ArgumentException("Carried attributes must be finite.", nameof(carriedAttributes));
        MeshMath.ValidateTriangleIndices(indices, positions.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(options.TargetTriangleCount);
        if (float.IsNaN(options.TargetError) || options.TargetError < 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Target error must be nonnegative.");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attributeWeights.Length, MaximumAttributes, nameof(attributeWeights));
        if (attributes.Length != (long)positions.Length * attributeWeights.Length)
            throw new ArgumentException("Attributes must hold one value per vertex and weight.", nameof(attributes));
        if (!lockedVertices.IsEmpty && lockedVertices.Length != positions.Length)
            throw new ArgumentException("Locked vertex flags must cover every vertex.", nameof(lockedVertices));
        foreach (float weight in attributeWeights)
            if (!float.IsFinite(weight) || weight < 0)
                throw new ArgumentOutOfRangeException(nameof(attributeWeights), "Attribute weights must be finite and nonnegative.");
        for (int i = 0; i < positions.Length; i++)
            if (!VectorMath.IsFinite(positions[i]))
                throw new ArgumentException("Positions must be finite.", nameof(positions));
        for (int i = 0; i < attributes.Length; i++)
            if (!float.IsFinite(attributes[i]))
                throw new ArgumentException("Attributes must be finite.", nameof(attributes));
        return new Simplifier(positions, indices, options, attributes, attributeWeights, carriedAttributes, lockedVertices).Run();
    }

    // Symmetric 4x4 error quadric over homogeneous positions: p^T A p + 2 b·p + c.
    private struct Quadric
    {
        public double A00, A01, A02, A11, A12, A22, B0, B1, B2, C;

        // Squared distance to the plane n·p + d = 0, scaled by weight; n need not be unit length.
        public static Quadric FromPlane(double nx, double ny, double nz, double d, double weight) => new()
        {
            A00 = weight * nx * nx, A01 = weight * nx * ny, A02 = weight * nx * nz,
            A11 = weight * ny * ny, A12 = weight * ny * nz, A22 = weight * nz * nz,
            B0 = weight * nx * d, B1 = weight * ny * d, B2 = weight * nz * d, C = weight * d * d
        };

        public void Add(in Quadric other)
        {
            A00 += other.A00; A01 += other.A01; A02 += other.A02;
            A11 += other.A11; A12 += other.A12; A22 += other.A22;
            B0 += other.B0; B1 += other.B1; B2 += other.B2; C += other.C;
        }

        public readonly double Evaluate(double x, double y, double z) =>
            A00 * x * x + A11 * y * y + A22 * z * z + 2 * (A01 * x * y + A02 * x * z + A12 * y * z)
            + 2 * (B0 * x + B1 * y + B2 * z) + C;
    }
}
