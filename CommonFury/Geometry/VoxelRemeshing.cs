using System.Numerics;
using CommonFury.Numerics;

namespace CommonFury.Geometry;

/// <summary>Grid spacing and surface distance for <see cref="VoxelRemeshing.Shell"/>.</summary>
/// <param name="VoxelSize">Sample spacing in position units: details and gaps smaller than about one voxel are lost.</param>
/// <param name="Offset">Distance of the shell from the source surface, at least half a voxel. Gaps narrower than about twice the
/// offset are closed and every part grows by it.</param>
public readonly record struct VoxelRemeshOptions(float VoxelSize, float Offset);

/// <summary>A closed surface built by <see cref="VoxelRemeshing.Shell"/>.</summary>
public sealed class RemeshedMesh
{
    internal RemeshedMesh(Vector3[] positions, int[] indices)
    {
        Positions = positions;
        Indices = indices;
    }

    /// <summary>Owned vertex positions.</summary>
    public Vector3[] Positions { get; }

    /// <summary>Owned triangle list, wound so that normals face away from the enclosed volume.</summary>
    public int[] Indices { get; }
}

/// <summary>Rebuilds triangle soups as one closed outer surface, for distant stand-ins that replace many parts at once.</summary>
/// <remarks>
/// <para>The shell is the surface at a fixed distance from the source triangles, sampled on a grid and extracted by dual
/// contouring. Open, single-sided, overlapping and self-intersecting input is fine: thin sheets become slabs twice the offset thick
/// instead of vanishing, and interiors, hidden faces and enclosed pockets are dropped because only space reachable from outside
/// the bounds counts as outside. Vertices are placed from the directions to the nearest source points, which keeps flat faces flat
/// and edges sharp: around a convex edge or corner of the source the shell stays square, so its vertices there lie up to √2 or √3
/// times the offset away.</para>
/// <para>Distances are stored only in 8×8×8 blocks near the surface, so memory and time grow with surface area rather than volume,
/// plus one bit per block of the bounds.</para>
/// <para>The output has no open edges, and a cell that two parts of the shell pass through gets a vertex for each, so parts touching
/// within one voxel stay separate and the surface is manifold. The one exception is rare: where a single part winds through two
/// neighbouring cells and meets itself across their shared face twice, the edge between their vertices joins four triangles.</para>
/// <para>The output is dense, about two triangles per voxel of surface; pass it to <see cref="MeshSimplification"/> with an error
/// of about one voxel to reduce it.</para>
/// </remarks>
public static class VoxelRemeshing
{
    private const int Block = 8, BlockVolume = Block * Block * Block, MaximumAxisSamples = 1 << 16, MaximumBlocks = 1 << 27;

    // Cell corner c sits at (c & 1, c >> 1 & 1, c >> 2). Cell edge axis * 4 + s + 2t runs along the axis from the corner offset by s
    // and t across the other two axes, taken in the order (y, z) for x, (z, x) for y and (x, y) for z.
    private static readonly int[] EdgeCorners = BuildEdgeCorners();

    // The corners of each cell face in order around it, and the edge from each corner to the next.
    private static readonly int[] FaceCorners = BuildFaceCorners();
    private static readonly int[] FaceEdges = BuildFaceEdges();

    private static int[] BuildEdgeCorners()
    {
        var corners = new int[24];
        for (int edge = 0; edge < 12; edge++)
        {
            int axis = edge >> 2, s = edge & 1, t = edge >> 1 & 1;
            int start = axis switch { 0 => s << 1 | t << 2, 1 => t | s << 2, _ => s | t << 1 };
            corners[edge * 2] = start;
            corners[edge * 2 + 1] = start | 1 << axis;
        }
        return corners;
    }

    private static int[] BuildFaceCorners()
    {
        var corners = new int[24];
        ReadOnlySpan<(int U, int W)> around = [(0, 0), (1, 0), (1, 1), (0, 1)];
        for (int f = 0; f < 6; f++)
        {
            int axis = f >> 1, side = f & 1, u = (axis + 1) % 3, w = (axis + 2) % 3;
            for (int q = 0; q < 4; q++)
                corners[f * 4 + q] = side << axis | around[q].U << u | around[q].W << w;
        }
        return corners;
    }

    private static int[] BuildFaceEdges()
    {
        var edges = new int[24];
        for (int f = 0; f < 6; f++)
            for (int q = 0; q < 4; q++)
            {
                int a = FaceCorners[f * 4 + q], b = FaceCorners[f * 4 + (q + 1) % 4];
                for (int edge = 0; edge < 12; edge++)
                {
                    int x = EdgeCorners[edge * 2], y = EdgeCorners[edge * 2 + 1];
                    if (x == a && y == b || x == b && y == a)
                        edges[f * 4 + q] = edge;
                }
            }
        return edges;
    }

    /// <exception cref="ArgumentException">An index is out of range or a referenced position is not finite.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The voxel size or offset is out of range, or the grid would exceed 65536 samples
    /// along an axis or 2^27 blocks of 8×8×8 samples in all.</exception>
    public static RemeshedMesh Shell(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, VoxelRemeshOptions options)
    {
        MeshMath.ValidateTriangleIndices(indices, positions.Length);
        foreach (int index in indices)
            if (!VectorMath.IsFinite(positions[index]))
                throw new ArgumentException("Positions must be finite.", nameof(positions));
        if (!(options.VoxelSize > 0) || !float.IsFinite(options.VoxelSize))
            throw new ArgumentOutOfRangeException(nameof(options), "Voxel size must be positive and finite.");
        if (!(options.Offset >= options.VoxelSize / 2) || !float.IsFinite(options.Offset))
            throw new ArgumentOutOfRangeException(nameof(options), "The offset must be finite and at least half the voxel size.");
        if (indices.IsEmpty)
            return new RemeshedMesh([], []);
        return new Grid(positions, indices, options).Extract();
    }

    // Samples sit at origin + (i, j, k) * voxel. Lengths inside are in voxels.
    private sealed class Grid
    {
        private readonly float voxel, offset, band;
        private readonly Vector3 origin;
        private readonly int nx, ny, nz, bx, by, bz;
        private readonly Vector3[] corner, edge1, edge2;
        private readonly TriangleBvh source;
        private readonly Dictionary<long, float[]> distance = new();
        private readonly Dictionary<long, bool[]> outsideSamples = new();
        // One bit per block: set for blocks without distances that the outside reaches.
        private readonly ulong[] outsideBlocks;

        public Grid(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, VoxelRemeshOptions options)
        {
            voxel = options.VoxelSize;
            offset = options.Offset / voxel;
            band = offset + 2;
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (int index in indices)
            {
                min = Vector3.Min(min, positions[index]);
                max = Vector3.Max(max, positions[index]);
            }
            int pad = (int)MathF.Ceiling(band) + 1;
            origin = min - new Vector3(pad * voxel);
            Vector3 span = (max - origin) / voxel;
            double largest = Math.Max(span.X, Math.Max(span.Y, span.Z)) + pad + 1;
            if (largest >= MaximumAxisSamples)
                throw new ArgumentOutOfRangeException(nameof(options), "The grid would exceed 65536 samples along an axis.");
            nx = (int)MathF.Ceiling(span.X) + pad + 1;
            ny = (int)MathF.Ceiling(span.Y) + pad + 1;
            nz = (int)MathF.Ceiling(span.Z) + pad + 1;
            bx = (nx + Block - 1) / Block;
            by = (ny + Block - 1) / Block;
            bz = (nz + Block - 1) / Block;
            long blocks = (long)bx * by * bz;
            if (blocks > MaximumBlocks)
                throw new ArgumentOutOfRangeException(nameof(options), "The grid would exceed 2^27 blocks of 8x8x8 samples.");
            outsideBlocks = new ulong[(blocks + 63) / 64];

            int count = indices.Length / 3;
            corner = new Vector3[count];
            edge1 = new Vector3[count];
            edge2 = new Vector3[count];
            for (int t = 0; t < count; t++)
            {
                Vector3 a = (positions[indices[t * 3]] - origin) / voxel;
                corner[t] = a;
                edge1[t] = (positions[indices[t * 3 + 1]] - origin) / voxel - a;
                edge2[t] = (positions[indices[t * 3 + 2]] - origin) / voxel - a;
            }
            source = new TriangleBvh(positions, indices);
        }

        public RemeshedMesh Extract()
        {
            FillDistances();
            FloodOutside();
            return Contour();
        }

        // Unsigned distance to the nearest triangle, exact for samples within the band of some triangle's bounds.
        private void FillDistances()
        {
            for (int t = 0; t < corner.Length; t++)
            {
                Vector3 a = corner[t], b = a + edge1[t], c = a + edge2[t];
                Vector3 lo = Vector3.Min(a, Vector3.Min(b, c)) - new Vector3(band), hi = Vector3.Max(a, Vector3.Max(b, c)) + new Vector3(band);
                int i0 = Math.Max(0, (int)MathF.Ceiling(lo.X)), i1 = Math.Min(nx - 1, (int)MathF.Floor(hi.X));
                int j0 = Math.Max(0, (int)MathF.Ceiling(lo.Y)), j1 = Math.Min(ny - 1, (int)MathF.Floor(hi.Y));
                int k0 = Math.Max(0, (int)MathF.Ceiling(lo.Z)), k1 = Math.Min(nz - 1, (int)MathF.Floor(hi.Z));
                for (int k = k0; k <= k1; k++)
                    for (int j = j0; j <= j1; j++)
                        for (int i = i0; i <= i1; i++)
                        {
                            var p = new Vector3(i, j, k) - a;
                            var w = TriangleMath.ClosestPointWeights(p, edge1[t], edge2[t]);
                            float d = Vector3.Distance(p, edge1[t] * w.Y + edge2[t] * w.Z);
                            if (d >= band)
                                continue;
                            ref float stored = ref Distances(BlockKey(i >> 3, j >> 3, k >> 3))[Local(i, j, k)];
                            if (d < stored)
                                stored = d;
                        }
            }
        }

        private float[] Distances(long key)
        {
            if (!distance.TryGetValue(key, out var values))
            {
                values = new float[BlockVolume];
                Array.Fill(values, band);
                distance[key] = values;
            }
            return values;
        }

        // Breadth-first flood from the grid corner through samples farther than the offset. Blocks without stored distances are
        // entirely far, so they are crossed whole.
        private void FloodOutside()
        {
            var queue = new Queue<(long Block, int Sample)>();
            void Visit(int i, int j, int k)
            {
                if (i < 0 || j < 0 || k < 0 || i >= nx || j >= ny || k >= nz)
                    return;
                long key = BlockKey(i >> 3, j >> 3, k >> 3);
                if (!distance.TryGetValue(key, out var values))
                {
                    ref ulong word = ref outsideBlocks[key >> 6];
                    if ((word & 1UL << (int)(key & 63)) == 0)
                    {
                        word |= 1UL << (int)(key & 63);
                        queue.Enqueue((key, -1));
                    }
                    return;
                }
                int local = Local(i, j, k);
                if (values[local] <= offset)
                    return;
                if (!outsideSamples.TryGetValue(key, out var flags))
                    outsideSamples[key] = flags = new bool[BlockVolume];
                if (flags[local])
                    return;
                flags[local] = true;
                queue.Enqueue((key, local));
            }
            Visit(0, 0, 0);
            while (queue.Count > 0)
            {
                var (key, local) = queue.Dequeue();
                var (cx, cy, cz) = Unpack(key);
                if (local >= 0)
                {
                    int i = cx * Block + (local & 7), j = cy * Block + (local >> 3 & 7), k = cz * Block + (local >> 6);
                    Visit(i + 1, j, k); Visit(i - 1, j, k); Visit(i, j + 1, k); Visit(i, j - 1, k); Visit(i, j, k + 1); Visit(i, j, k - 1);
                    continue;
                }
                // An empty block reaches every sample on the faces of its neighbours.
                int x0 = cx * Block, y0 = cy * Block, z0 = cz * Block;
                for (int a = 0; a < Block; a++)
                    for (int b = 0; b < Block; b++)
                    {
                        Visit(x0 - 1, y0 + a, z0 + b); Visit(x0 + Block, y0 + a, z0 + b);
                        Visit(x0 + a, y0 - 1, z0 + b); Visit(x0 + a, y0 + Block, z0 + b);
                        Visit(x0 + a, y0 + b, z0 - 1); Visit(x0 + a, y0 + b, z0 + Block);
                    }
            }
        }

        // Positive outside, zero or negative inside: the distance beyond the offset, or a stand-in for enclosed space.
        private float Value(int i, int j, int k)
        {
            long key = BlockKey(i >> 3, j >> 3, k >> 3);
            if (!distance.TryGetValue(key, out var values))
                return (outsideBlocks[key >> 6] & 1UL << (int)(key & 63)) != 0 ? band - offset : -1;
            int local = Local(i, j, k);
            bool outside = outsideSamples.TryGetValue(key, out var flags) && flags[local];
            // Samples within the offset keep their true value so crossings interpolate exactly; enclosed samples beyond it only
            // need to be negative.
            return outside || values[local] <= offset ? values[local] - offset : -0.5f;
        }

        private RemeshedMesh Contour()
        {
            var positions = new List<Vector3>();
            var indices = new List<int>();
            var vertexOf = new Dictionary<long, (int First, int Pieces)>();
            Span<int> ring = stackalloc int[4];
            foreach (long key in distance.Keys.Order())
            {
                var (cx, cy, cz) = Unpack(key);
                for (int local = 0; local < BlockVolume; local++)
                {
                    int i = cx * Block + (local & 7), j = cy * Block + (local >> 3 & 7), k = cz * Block + (local >> 6);
                    if (i >= nx - 1 || j >= ny - 1 || k >= nz - 1 || i == 0 || j == 0 || k == 0)
                        continue;
                    bool lowerOutside = Value(i, j, k) > 0;
                    for (int axis = 0; axis < 3; axis++)
                    {
                        int ui = axis == 0 ? 1 : 0, uj = axis == 1 ? 1 : 0, uk = axis == 2 ? 1 : 0;
                        if (Value(i + ui, j + uj, k + uk) > 0 == lowerOutside)
                            continue;
                        // The four cells around the edge, wound positively about the axis; reversed when the outside lies below. In
                        // each, the edge is cell edge axis * 4 + s + 2t, with s and t its offsets across the other two axes.
                        for (int q = 0; q < 4; q++)
                        {
                            int du = q is 0 or 3 ? -1 : 0, dv = q < 2 ? -1 : 0;
                            int ci = i, cj = j, ck = k;
                            switch (axis)
                            {
                                case 0: cj += du; ck += dv; break;
                                case 1: ck += du; ci += dv; break;
                                default: ci += du; cj += dv; break;
                            }
                            ring[q] = CellVertex(ci, cj, ck, axis * 4 - du - 2 * dv, positions, vertexOf);
                        }
                        if (lowerOutside)
                            ring.Reverse();
                        indices.AddRange([ring[0], ring[1], ring[2], ring[0], ring[2], ring[3]]);
                    }
                }
            }
            return new RemeshedMesh([.. positions], [.. indices]);
        }

        // The vertex of the piece of surface that crosses cell edge `edge`. A cell holds one vertex per piece: crossings on each
        // face are paired by the segments the surface leaves on it, and linked crossings form one piece. Where a face has four
        // crossings, the segments cut off its outside corners, a choice both cells sharing the face make alike, so pieces meet
        // across faces one to one. Each vertex is the regularized least-squares fit to the planes at its piece's crossings, kept
        // inside the cell.
        private int CellVertex(int ci, int cj, int ck, int edge, List<Vector3> positions, Dictionary<long, (int First, int Pieces)> vertexOf)
        {
            long key = ((long)ck * ny + cj) * nx + ci;
            if (vertexOf.TryGetValue(key, out var cell))
                return cell.First + (cell.Pieces >> 2 * edge & 3);
            int outside = 0;
            for (int c = 0; c < 8; c++)
                if (Value(ci + (c & 1), cj + (c >> 1 & 1), ck + (c >> 2)) > 0)
                    outside |= 1 << c;
            // Union-find over crossed edges; -1 marks edges the surface does not cross.
            Span<int> piece = stackalloc int[12];
            for (int e = 0; e < 12; e++)
                piece[e] = Crosses(outside, e) ? e : -1;
            for (int f = 0; f < 6; f++)
            {
                int crossings = 0, first = -1;
                for (int q = 0; q < 4; q++)
                    if (piece[FaceEdges[f * 4 + q]] >= 0)
                    {
                        crossings++;
                        first = first < 0 ? FaceEdges[f * 4 + q] : first;
                    }
                if (crossings == 2)
                {
                    for (int q = 0; q < 4; q++)
                        if (piece[FaceEdges[f * 4 + q]] >= 0)
                            Join(piece, FaceEdges[f * 4 + q], first);
                }
                else if (crossings == 4)
                {
                    // Corner q lies between face edges q - 1 and q.
                    for (int q = 0; q < 4; q++)
                        if ((outside >> FaceCorners[f * 4 + q] & 1) != 0)
                            Join(piece, FaceEdges[f * 4 + (q + 3) % 4], FaceEdges[f * 4 + q]);
                }
            }
            // Number the pieces in edge order, two bits per edge, and fit one vertex to each.
            Span<int> label = stackalloc int[12];
            label.Fill(-1);
            int count = 0, labels = 0;
            for (int e = 0; e < 12; e++)
                if (piece[e] >= 0)
                {
                    int root = Find(piece, e);
                    if (label[root] < 0)
                        label[root] = count++;
                    labels |= label[root] << 2 * e;
                }
            int firstVertex = positions.Count;
            for (int n = 0; n < count; n++)
                positions.Add(Fit(ci, cj, ck, outside, labels, n));
            vertexOf[key] = (firstVertex, labels);
            return firstVertex + (labels >> 2 * edge & 3);
        }

        private static int Find(Span<int> parent, int e)
        {
            while (parent[e] != e)
                e = parent[e] = parent[parent[e]];
            return e;
        }

        private static void Join(Span<int> parent, int a, int b) => parent[Find(parent, a)] = Find(parent, b);

        private Vector3 Fit(int ci, int cj, int ck, int outside, int labels, int piece)
        {
            double a00 = 0, a01 = 0, a02 = 0, a11 = 0, a12 = 0, a22 = 0, b0 = 0, b1 = 0, b2 = 0;
            Vector3 mass = Vector3.Zero;
            int crossings = 0;
            for (int edge = 0; edge < 12; edge++)
            {
                if (!Crosses(outside, edge) || (labels >> 2 * edge & 3) != piece)
                    continue;
                int axis = edge >> 2, s = edge & 1, t = edge >> 1 & 1;
                int i = ci, j = cj, k = ck;
                switch (axis)
                {
                    case 0: j += s; k += t; break;
                    case 1: k += s; i += t; break;
                    default: i += s; j += t; break;
                }
                int ui = axis == 0 ? 1 : 0, uj = axis == 1 ? 1 : 0, uk = axis == 2 ? 1 : 0;
                float v0 = Value(i, j, k), v1 = Value(i + ui, j + uj, k + uk);
                var direction = new Vector3(ui, uj, uk);
                var point = new Vector3(i, j, k) + direction * (v0 / (v0 - v1));
                var normal = Normal(point, v0 > 0 ? -direction : direction);
                a00 += normal.X * normal.X; a01 += normal.X * normal.Y; a02 += normal.X * normal.Z;
                a11 += normal.Y * normal.Y; a12 += normal.Y * normal.Z; a22 += normal.Z * normal.Z;
                double d = Vector3.Dot(normal, point);
                b0 += normal.X * d; b1 += normal.Y * d; b2 += normal.Z * d;
                mass += point;
                crossings++;
            }
            mass /= crossings;
            const double Bias = 0.05;
            var solved = Solve(a00 + Bias, a01, a02, a11 + Bias, a12, a22 + Bias, b0 + Bias * mass.X, b1 + Bias * mass.Y, b2 + Bias * mass.Z) ?? mass;
            // Strictly inside the cell, so that neighbouring cells never share a vertex position, as they would where the surface
            // passes through a sample. The walls are computed alike by both cells beside them and moved in by at least one float
            // step, which a fixed fraction of a voxel is not far from the origin.
            var cell = new Vector3(ci, cj, ck);
            Vector3 low = origin + cell * voxel, high = origin + (cell + Vector3.One) * voxel, world = origin + solved * voxel;
            return new(Inside(world.X, low.X, high.X), Inside(world.Y, low.Y, high.Y), Inside(world.Z, low.Z, high.Z));
        }

        private float Inside(float value, float low, float high)
        {
            float margin = 1e-3f * voxel;
            float lower = MathF.Max(low + margin, MathF.BitIncrement(low)), upper = MathF.Min(high - margin, MathF.BitDecrement(high));
            return lower <= upper ? Math.Clamp(value, lower, upper) : (low + high) / 2;
        }

        // Whether the surface crosses cell edge `edge`, given which of the cell's corners lie outside.
        private static bool Crosses(int outside, int edge) =>
            (outside >> EdgeCorners[edge * 2] & 1) != (outside >> EdgeCorners[edge * 2 + 1] & 1);

        // The distance field's gradient at a crossing: away from the nearest source point, oriented outward along the edge.
        private Vector3 Normal(Vector3 point, Vector3 outward)
        {
            var world = origin + point * voxel;
            if (!source.FindNearest(world, (band + 1) * voxel, out var hit))
                return outward;
            var t = hit.Triangle;
            var nearest = corner[t] + edge1[t] * hit.Barycentric.Y + edge2[t] * hit.Barycentric.Z;
            var away = point - nearest;
            if (away.LengthSquared() < 1e-12f)
                return outward;
            away = Vector3.Normalize(away);
            return Vector3.Dot(away, outward) < 0 ? -away : away;
        }

        private static Vector3? Solve(double a00, double a01, double a02, double a11, double a12, double a22, double b0, double b1, double b2)
        {
            double c00 = a11 * a22 - a12 * a12, c01 = a02 * a12 - a01 * a22, c02 = a01 * a12 - a02 * a11;
            double determinant = a00 * c00 + a01 * c01 + a02 * c02;
            if (Math.Abs(determinant) < 1e-12)
                return null;
            double c11 = a00 * a22 - a02 * a02, c12 = a01 * a02 - a00 * a12, c22 = a00 * a11 - a01 * a01;
            return new Vector3((float)((c00 * b0 + c01 * b1 + c02 * b2) / determinant), (float)((c01 * b0 + c11 * b1 + c12 * b2) / determinant),
                (float)((c02 * b0 + c12 * b1 + c22 * b2) / determinant));
        }

        private long BlockKey(int x, int y, int z) => ((long)z * by + y) * bx + x;

        private (int X, int Y, int Z) Unpack(long key) => ((int)(key % bx), (int)(key / bx % by), (int)(key / ((long)bx * by)));

        private static int Local(int i, int j, int k) => (i & 7) | (j & 7) << 3 | (k & 7) << 6;
    }
}
