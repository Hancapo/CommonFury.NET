using System.Numerics;

namespace CommonFury.Geometry;

public static partial class MeshSimplification
{
    // Collapses run in passes: each pass ranks every allowed edge collapse by its error, then applies the cheapest ones whose
    // neighbourhoods no earlier collapse of the same pass touched, so the ranking it used stays valid.
    private sealed class Simplifier
    {
        // Boundary planes weigh against sliding a border or seam inward; scaled by squared edge length like triangle areas.
        private const double BoundaryWeight = 10;
        // A collapse may not turn a remaining triangle's normal by more than acos(0.25), about 75°.
        private const double FlipCosine = 0.25;

        private enum Kind : byte { Manifold, Border, Seam, Locked }

        private readonly int vertexCount, attributeCount, carriedCount, targetTriangles;
        private readonly double extent, errorLimit;
        private readonly bool lockBorders;
        private readonly double[] position;
        private readonly float[] attributes, carried;
        private readonly double[] attributeWeights;
        // Vertices equal in position and measured attributes share a canonical vertex; triangles use canonical vertices while
        // corners remember the source vertex each triangle corner started from.
        private readonly int[] canonical, variantNext, corners;
        private readonly int[] group, wedgeNext;
        private readonly bool[] lockedGroup;
        private readonly Quadric[] positionQuadric;
        private readonly double[] positionWeight;
        private readonly Quadric[] attributeQuadric;
        private readonly double[] gradient, attributeArea;
        private readonly int[] adjacencyStart, collapseTo;
        private readonly Kind[] kind;
        private readonly bool[] touched;
        private int[] adjacency = [];
        private int[] triangles;
        private int triangleCount;

        public Simplifier(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, MeshSimplificationOptions options,
            ReadOnlySpan<float> attributes, ReadOnlySpan<float> attributeWeights, ReadOnlySpan<float> carried, ReadOnlySpan<bool> lockedVertices)
        {
            vertexCount = positions.Length;
            attributeCount = attributeWeights.Length;
            carriedCount = vertexCount == 0 ? 0 : carried.Length / vertexCount;
            targetTriangles = options.TargetTriangleCount;
            lockBorders = options.LockBorders;
            this.attributes = attributes.ToArray();
            this.carried = carried.ToArray();

            canonical = new int[vertexCount];
            variantNext = new int[vertexCount];
            var firstVariant = new Dictionary<int, int>(vertexCount, new VertexComparer(positions.ToArray(), this.attributes, attributeCount));
            for (int i = 0; i < vertexCount; i++)
            {
                if (firstVariant.TryAdd(i, i))
                {
                    canonical[i] = i;
                    variantNext[i] = i;
                    continue;
                }
                int head = firstVariant[i];
                canonical[i] = head;
                variantNext[i] = variantNext[head];
                variantNext[head] = i;
            }
            corners = indices.ToArray();
            triangles = new int[indices.Length];
            for (int i = 0; i < indices.Length; i++)
                triangles[i] = canonical[indices[i]];
            triangleCount = indices.Length / 3;

            // Work in coordinates normalized to the referenced bounds, which keeps the quadrics well conditioned.
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            foreach (int index in indices)
            {
                min = Vector3.Min(min, positions[index]);
                max = Vector3.Max(max, positions[index]);
            }
            Vector3 size = indices.IsEmpty ? Vector3.Zero : max - min;
            extent = Math.Max(size.X, Math.Max(size.Y, size.Z));
            if (extent == 0)
                extent = 1;
            errorLimit = float.IsPositiveInfinity(options.TargetError) ? double.PositiveInfinity
                : (double)options.TargetError / extent * ((double)options.TargetError / extent);

            position = new double[vertexCount * 3];
            group = new int[vertexCount];
            wedgeNext = new int[vertexCount];
            lockedGroup = new bool[vertexCount];
            var first = new Dictionary<Vector3, int>(vertexCount);
            for (int i = 0; i < vertexCount; i++)
            {
                position[i * 3] = (positions[i].X - (double)min.X) / extent;
                position[i * 3 + 1] = (positions[i].Y - (double)min.Y) / extent;
                position[i * 3 + 2] = (positions[i].Z - (double)min.Z) / extent;
                // Vertices at one position form a ring of wedges headed by the first of them.
                if (first.TryAdd(positions[i], i))
                {
                    group[i] = i;
                    wedgeNext[i] = i;
                }
                else
                {
                    int head = first[positions[i]];
                    group[i] = head;
                    wedgeNext[i] = wedgeNext[head];
                    wedgeNext[head] = i;
                }
                if (!lockedVertices.IsEmpty && lockedVertices[i])
                    lockedGroup[group[i]] = true;
            }

            this.attributeWeights = new double[attributeCount];
            for (int j = 0; j < attributeCount; j++)
                this.attributeWeights[j] = attributeWeights[j] / extent;
            positionQuadric = new Quadric[vertexCount];
            positionWeight = new double[vertexCount];
            attributeQuadric = attributeCount == 0 ? [] : new Quadric[vertexCount];
            gradient = new double[vertexCount * attributeCount * 4];
            attributeArea = attributeCount == 0 ? [] : new double[vertexCount];
            adjacencyStart = new int[vertexCount + 1];
            collapseTo = new int[vertexCount];
            kind = new Kind[vertexCount];
            touched = new bool[vertexCount];
        }

        public SimplifiedMesh Run()
        {
            Array.Fill(collapseTo, -1);
            Rebuild();
            AddTriangleQuadrics();
            BuildAdjacency();
            AddBoundaryQuadrics();
            double error = 0;
            for (bool first = true; triangleCount > targetTriangles; first = false)
            {
                if (!first)
                    BuildAdjacency();
                Classify();
                if (Pass(ref error) == 0)
                    break;
                Rebuild();
            }
            var result = new int[triangleCount * 3];
            for (int i = 0; i < result.Length; i++)
                result[i] = Pick(triangles[i], corners[i]);
            return new SimplifiedMesh(result, (float)(Math.Sqrt(error) * extent));
        }

        // A corner that never moved keeps its source vertex; a moved corner takes the variant of its new canonical vertex whose
        // carried attributes are nearest to those of its source vertex, preferring the lowest index on ties.
        private int Pick(int vertex, int source)
        {
            if (canonical[source] == vertex)
                return source;
            int best = vertex;
            double bestDistance = double.PositiveInfinity;
            int v = vertex;
            do
            {
                double distance = 0;
                for (int j = 0; j < carriedCount; j++)
                {
                    double difference = carried[v * carriedCount + j] - carried[source * carriedCount + j];
                    distance += difference * difference;
                }
                if (distance < bestDistance || distance == bestDistance && v < best)
                    (best, bestDistance) = (v, distance);
                v = variantNext[v];
            }
            while (v != vertex);
            return best;
        }

        // Equality of position and measured attributes; negative zero equals zero.
        private sealed class VertexComparer(Vector3[] positions, float[] attributes, int attributeCount) : IEqualityComparer<int>
        {
            public bool Equals(int a, int b) => positions[a] == positions[b]
                && attributes.AsSpan(a * attributeCount, attributeCount).SequenceEqual(attributes.AsSpan(b * attributeCount, attributeCount));

            public int GetHashCode(int vertex)
            {
                var hash = new HashCode();
                hash.Add(positions[vertex].X + 0f);
                hash.Add(positions[vertex].Y + 0f);
                hash.Add(positions[vertex].Z + 0f);
                for (int j = 0; j < attributeCount; j++)
                    hash.Add(attributes[vertex * attributeCount + j] + 0f);
                return hash.ToHashCode();
            }
        }

        private void AddTriangleQuadrics()
        {
            for (int t = 0; t < triangleCount; t++)
            {
                int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                var (e1, e2, normal) = Edges(a, b, c);
                double length = normal.Length();
                if (length == 0)
                    continue;
                double area = length / 2;
                var unit = normal / length;
                var plane = Quadric.FromPlane(unit.X, unit.Y, unit.Z, -Dot(unit, a), area);
                foreach (int vertex in (ReadOnlySpan<int>)[a, b, c])
                {
                    positionQuadric[group[vertex]].Add(plane);
                    positionWeight[group[vertex]] += area;
                }
                if (attributeCount == 0)
                    continue;
                // Each attribute varies linearly over the triangle as g·p + d, with g in the triangle's plane.
                var towardB = Vec.Cross(e2, normal) / (length * length);
                var towardC = Vec.Cross(normal, e1) / (length * length);
                for (int j = 0; j < attributeCount; j++)
                {
                    double weight = area * attributeWeights[j] * attributeWeights[j];
                    if (weight == 0)
                        continue;
                    double value = Attribute(a, j);
                    var g = towardB * (Attribute(b, j) - value) + towardC * (Attribute(c, j) - value);
                    double d = value - Dot(g, a);
                    var term = Quadric.FromPlane(g.X, g.Y, g.Z, d, weight);
                    foreach (int vertex in (ReadOnlySpan<int>)[a, b, c])
                    {
                        attributeQuadric[vertex].Add(term);
                        int offset = (vertex * attributeCount + j) * 4;
                        gradient[offset] += weight * g.X;
                        gradient[offset + 1] += weight * g.Y;
                        gradient[offset + 2] += weight * g.Z;
                        gradient[offset + 3] += weight * d;
                    }
                }
                attributeArea[a] += area;
                attributeArea[b] += area;
                attributeArea[c] += area;
            }
        }

        // Planes through open and seam edges, perpendicular to their triangle, keep those edges from drifting.
        private void AddBoundaryQuadrics()
        {
            for (int t = 0; t < triangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = triangles[t * 3 + k], b = triangles[t * 3 + (k + 1) % 3];
                    if (HasGroupEdge(group[b], group[a]) && HasIndexEdge(b, a))
                        continue;
                    var (_, _, normal) = Edges(triangles[t * 3], triangles[t * 3 + 1], triangles[t * 3 + 2]);
                    var edge = Point(b) - Point(a);
                    var perpendicular = Vec.Cross(edge, normal);
                    double length = perpendicular.Length();
                    if (length == 0)
                        continue;
                    perpendicular /= length;
                    double weight = edge.LengthSquared() * BoundaryWeight;
                    var plane = Quadric.FromPlane(perpendicular.X, perpendicular.Y, perpendicular.Z, -Dot(perpendicular, a), weight);
                    foreach (int vertex in (ReadOnlySpan<int>)[a, b])
                    {
                        positionQuadric[group[vertex]].Add(plane);
                        positionWeight[group[vertex]] += weight;
                    }
                }
        }

        private void BuildAdjacency()
        {
            Array.Clear(adjacencyStart);
            for (int i = 0; i < triangleCount * 3; i++)
                adjacencyStart[group[triangles[i]] + 1]++;
            for (int i = 0; i < vertexCount; i++)
                adjacencyStart[i + 1] += adjacencyStart[i];
            if (adjacency.Length < triangleCount * 3)
                adjacency = new int[triangleCount * 3];
            Span<int> fill = vertexCount <= 1024 ? stackalloc int[vertexCount] : new int[vertexCount];
            adjacencyStart.AsSpan(0, vertexCount).CopyTo(fill);
            for (int i = 0; i < triangleCount * 3; i++)
                adjacency[fill[group[triangles[i]]]++] = i / 3;
        }

        // Kinds are per position. A position has one used wedge unless it lies on a seam, which needs exactly two wedges that
        // each meet the seam with one incoming and one outgoing open edge.
        private void Classify()
        {
            // Open edges out of and into the first and second wedge.
            Span<int> wedgeOpen = stackalloc int[4];
            for (int g = 0; g < vertexCount; g++)
            {
                if (group[g] != g || adjacencyStart[g] == adjacencyStart[g + 1])
                    continue;
                if (lockedGroup[g])
                {
                    kind[g] = Kind.Locked;
                    continue;
                }
                int openOut = 0, openIn = 0, wedge0 = -1, wedge1 = -1;
                wedgeOpen.Clear();
                bool manyWedges = false;
                foreach (int t in Triangles(g))
                    for (int k = 0; k < 3; k++)
                    {
                        int v = triangles[t * 3 + k];
                        if (group[v] != g)
                            continue;
                        int next = triangles[t * 3 + (k + 1) % 3], previous = triangles[t * 3 + (k + 2) % 3];
                        if (!HasGroupEdge(group[next], g))
                            openOut++;
                        if (!HasGroupEdge(g, group[previous]))
                            openIn++;
                        int slot;
                        if (wedge0 < 0 || wedge0 == v)
                            (wedge0, slot) = (v, 0);
                        else if (wedge1 < 0 || wedge1 == v)
                            (wedge1, slot) = (v, 2);
                        else
                        {
                            manyWedges = true;
                            continue;
                        }
                        if (!HasIndexEdge(next, v))
                            wedgeOpen[slot]++;
                        if (!HasIndexEdge(v, previous))
                            wedgeOpen[slot + 1]++;
                    }
                if (manyWedges)
                    kind[g] = Kind.Locked;
                else if (wedge1 < 0)
                    kind[g] = wedgeOpen[0] == 0 && wedgeOpen[1] == 0 ? Kind.Manifold
                        : openOut == 1 && openIn == 1 && wedgeOpen[0] == 1 && wedgeOpen[1] == 1 && !lockBorders ? Kind.Border : Kind.Locked;
                else
                    kind[g] = openOut == 0 && openIn == 0 && wedgeOpen[0] == 1 && wedgeOpen[1] == 1 && wedgeOpen[2] == 1 && wedgeOpen[3] == 1
                        ? Kind.Seam : Kind.Locked;
            }
        }

        private int Pass(ref double error)
        {
            var costs = new List<double>();
            var sources = new List<int>();
            var targets = new List<int>();
            for (int t = 0; t < triangleCount; t++)
                for (int k = 0; k < 3; k++)
                {
                    int a = triangles[t * 3 + k], b = triangles[t * 3 + (k + 1) % 3];
                    int ga = group[a], gb = group[b];
                    bool open = !HasGroupEdge(gb, ga);
                    // A closed edge appears in two triangles; rank it once.
                    if (!open && ga > gb)
                        continue;
                    bool seam = !open && !HasIndexEdge(b, a);
                    double forward = CanCollapse(a, b, open, seam) ? Cost(a, b) : double.PositiveInfinity;
                    double backward = CanCollapse(b, a, open, seam) ? Cost(b, a) : double.PositiveInfinity;
                    if (double.IsPositiveInfinity(Math.Min(forward, backward)))
                        continue;
                    costs.Add(Math.Min(forward, backward));
                    sources.Add(forward <= backward ? a : b);
                    targets.Add(forward <= backward ? b : a);
                }
            var order = new int[costs.Count];
            for (int i = 0; i < order.Length; i++)
                order[i] = i;
            var keys = costs.ToArray();
            Array.Sort(keys, order);

            Array.Clear(touched);
            int remaining = triangleCount;
            int collapsed = 0;
            foreach (int candidate in order)
            {
                double cost = costs[candidate];
                if (remaining <= targetTriangles || cost > errorLimit)
                    break;
                int u = sources[candidate], v = targets[candidate], gu = group[u], gv = group[v];
                if (touched[gu] || touched[gv] || Flips(gu, gv, out int removed))
                    continue;
                Collapse(u, v);
                foreach (int t in Triangles(gu))
                    for (int k = 0; k < 3; k++)
                        touched[group[triangles[t * 3 + k]]] = true;
                remaining -= removed;
                error = Math.Max(error, cost);
                collapsed++;
            }
            return collapsed;
        }

        // Border and seam vertices slide only along their border or seam, onto another such vertex or a locked one.
        private bool CanCollapse(int u, int v, bool open, bool seam) => kind[group[u]] switch
        {
            Kind.Manifold => true,
            Kind.Border => open && kind[group[v]] is Kind.Border or Kind.Locked,
            Kind.Seam => seam && kind[group[v]] is Kind.Seam or Kind.Locked && SeamPartner(u, v, out _, out _),
            _ => false
        };

        // The merged quadrics evaluated at the kept vertex, per unit of the area they cover.
        private double Cost(int u, int v)
        {
            int gu = group[u], gv = group[v];
            double x = position[v * 3], y = position[v * 3 + 1], z = position[v * 3 + 2];
            double sum = positionQuadric[gu].Evaluate(x, y, z) + positionQuadric[gv].Evaluate(x, y, z);
            if (attributeCount > 0)
            {
                sum += AttributeError(u, v, x, y, z);
                if (kind[gu] == Kind.Seam && SeamPartner(u, v, out int otherU, out int otherV))
                    sum += AttributeError(otherU, otherV, x, y, z);
            }
            double weight = positionWeight[gu] + positionWeight[gv];
            return weight > 0 ? Math.Max(0, sum / weight) : 0;
        }

        // The wedges on the far side of a seam edge u-v: u's other wedge and the wedge of v's position next to it.
        private bool SeamPartner(int u, int v, out int otherU, out int otherV)
        {
            otherU = OtherWedge(u);
            otherV = -1;
            foreach (int t in Triangles(group[u]))
                for (int k = 0; k < 3; k++)
                    if (triangles[t * 3 + k] == otherU)
                        for (int c = 0; c < 3; c++)
                            if (group[triangles[t * 3 + c]] == group[v])
                                otherV = triangles[t * 3 + c];
            return otherU != u && otherV >= 0;
        }

        private double AttributeError(int u, int v, double x, double y, double z)
        {
            double sum = attributeQuadric[u].Evaluate(x, y, z) + attributeQuadric[v].Evaluate(x, y, z);
            double area = attributeArea[u] + attributeArea[v];
            for (int j = 0; j < attributeCount; j++)
            {
                int ou = (u * attributeCount + j) * 4, ov = (v * attributeCount + j) * 4;
                double value = Attribute(v, j);
                double predicted = (gradient[ou] + gradient[ov]) * x + (gradient[ou + 1] + gradient[ov + 1]) * y
                    + (gradient[ou + 2] + gradient[ov + 2]) * z + gradient[ou + 3] + gradient[ov + 3];
                sum += value * (value * area * attributeWeights[j] * attributeWeights[j] - 2 * predicted);
            }
            return sum;
        }

        // Whether moving position gu onto gv turns a remaining triangle too far; also counts the triangles the move removes.
        private bool Flips(int gu, int gv, out int removed)
        {
            removed = 0;
            foreach (int t in Triangles(gu))
            {
                int a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                if (group[a] == gv || group[b] == gv || group[c] == gv)
                {
                    removed++;
                    continue;
                }
                var (_, _, before) = Edges(a, b, c);
                var p0 = group[a] == gu ? Point(gv) : Point(a);
                var p1 = group[b] == gu ? Point(gv) : Point(b);
                var p2 = group[c] == gu ? Point(gv) : Point(c);
                var after = Vec.Cross(p1 - p0, p2 - p0);
                // A triangle without area has no orientation to keep, so moving its corners is rejected too.
                if (Vec.Dot(before, after) <= FlipCosine * Math.Sqrt(before.LengthSquared() * after.LengthSquared()))
                    return true;
            }
            return false;
        }

        private void Collapse(int u, int v)
        {
            int gu = group[u], gv = group[v];
            positionQuadric[gv].Add(positionQuadric[gu]);
            positionWeight[gv] += positionWeight[gu];
            Merge(u, v);
            if (kind[gu] == Kind.Seam && SeamPartner(u, v, out int otherU, out int otherV))
                Merge(otherU, otherV);
        }

        private void Merge(int u, int v)
        {
            collapseTo[u] = v;
            if (attributeCount == 0)
                return;
            attributeQuadric[v].Add(attributeQuadric[u]);
            attributeArea[v] += attributeArea[u];
            int ou = u * attributeCount * 4, ov = v * attributeCount * 4;
            for (int i = 0; i < attributeCount * 4; i++)
                gradient[ov + i] += gradient[ou + i];
        }

        // Applies this pass's collapses and drops triangles whose corners now share a position.
        private void Rebuild()
        {
            int kept = 0;
            for (int t = 0; t < triangleCount; t++)
            {
                int a = Resolve(triangles[t * 3]), b = Resolve(triangles[t * 3 + 1]), c = Resolve(triangles[t * 3 + 2]);
                if (group[a] == group[b] || group[b] == group[c] || group[a] == group[c])
                    continue;
                triangles[kept * 3] = a;
                triangles[kept * 3 + 1] = b;
                triangles[kept * 3 + 2] = c;
                corners.AsSpan(t * 3, 3).CopyTo(corners.AsSpan(kept * 3));
                kept++;
            }
            triangleCount = kept;
            Array.Fill(collapseTo, -1);
        }

        private int Resolve(int vertex) => collapseTo[vertex] < 0 ? vertex : collapseTo[vertex];

        // The other wedge of a seam position that triangles still use.
        private int OtherWedge(int vertex)
        {
            for (int w = wedgeNext[vertex]; w != vertex; w = wedgeNext[w])
                foreach (int t in Triangles(group[vertex]))
                    if (triangles[t * 3] == w || triangles[t * 3 + 1] == w || triangles[t * 3 + 2] == w)
                        return w;
            return vertex;
        }

        private ReadOnlySpan<int> Triangles(int g) => adjacency.AsSpan(adjacencyStart[g], adjacencyStart[g + 1] - adjacencyStart[g]);

        // Whether some triangle runs from position ga to position gb.
        private bool HasGroupEdge(int ga, int gb)
        {
            foreach (int t in Triangles(ga))
                for (int k = 0; k < 3; k++)
                    if (group[triangles[t * 3 + k]] == ga && group[triangles[t * 3 + (k + 1) % 3]] == gb)
                        return true;
            return false;
        }

        // Whether some triangle runs from vertex a to vertex b.
        private bool HasIndexEdge(int a, int b)
        {
            foreach (int t in Triangles(group[a]))
                for (int k = 0; k < 3; k++)
                    if (triangles[t * 3 + k] == a && triangles[t * 3 + (k + 1) % 3] == b)
                        return true;
            return false;
        }

        private double Attribute(int vertex, int index) => attributes[vertex * attributeCount + index];

        private Vec Point(int vertex) => new(position[vertex * 3], position[vertex * 3 + 1], position[vertex * 3 + 2]);

        private double Dot(Vec direction, int vertex) => Vec.Dot(direction, Point(vertex));

        private (Vec E1, Vec E2, Vec Normal) Edges(int a, int b, int c)
        {
            Vec p = Point(a), e1 = Point(b) - p, e2 = Point(c) - p;
            return (e1, e2, Vec.Cross(e1, e2));
        }


        private readonly record struct Vec(double X, double Y, double Z)
        {
            public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
            public static Vec operator -(Vec a, Vec b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
            public static Vec operator *(Vec a, double scale) => new(a.X * scale, a.Y * scale, a.Z * scale);
            public static Vec operator /(Vec a, double scale) => new(a.X / scale, a.Y / scale, a.Z / scale);
            public static double Dot(Vec a, Vec b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
            public static Vec Cross(Vec a, Vec b) => new(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
            public double LengthSquared() => Dot(this, this);
            public double Length() => Math.Sqrt(LengthSquared());
        }
    }
}
