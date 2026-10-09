using System.Numerics;

namespace MainframeEngine.Trees;

/// <summary>
/// <see cref="TreeStyle.LowPoly"/>: the same skeleton (same seed, same silhouette) meshed as faceted bark with fewer
/// rings and sides, and the leaves merged into a few low-poly blobs. Flat shading comes from split normals (every
/// triangle has its own three vertices with the face normal), so it runs on any material that interpolates vertex
/// normals. Uses no RNG: blob jitter and shades are hashes.
/// </summary>
internal sealed class LowPolyMesher
{
    private static readonly Vec3d UnitY = new(0, 1, 0);

    // Unit icospheres: detail 0 (12 vertices, 20 triangles) and 1 (42 vertices, 80 triangles), outward CCW.
    private static readonly (Vec3d[] Vertices, int[] Triangles)[] Icospheres = BuildIcospheres();

    private readonly List<Vector3> _positions = [];
    private readonly List<Vector3> _normals = [];
    private readonly List<Vector2> _uvs = [];
    private readonly List<Vector4> _custom0 = [];
    private readonly List<Vector4> _colors = [];
    private readonly List<int> _indices = [];

    private readonly List<Vec3d> _ring = [];
    private readonly List<Vec3d> _rings = [];
    private readonly List<float> _ringWeights = [];
    private readonly List<double> _ringV = [];

    private readonly Dictionary<(long, long, long), int> _cells = [];
    private readonly List<Cluster> _clusters = [];
    private readonly List<int> _leafCluster = [];
    private readonly List<Blob> _blobs = [];
    private readonly List<Vec3d> _blobVertices = [];
    private readonly List<int> _blobTriangles = [];
    private int[] _nearest = [];
    private double[] _nearestDistance = [];
    private Vec3d[] _means = [];

    public TreeMeshData Mesh(TreeSkeleton skeleton, TreeParams parameters, in TreeMeshDetail detail, TreeTrunkCapsule trunk)
    {
        var bark = MeshBark(skeleton, parameters, detail, out var barkError);
        var leaves = MeshBlobs(skeleton, parameters, detail, out var leafError);
        return new TreeMeshData(bark, leaves, Math.Max(barkError, leafError) * parameters.Scale, trunk);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Faceted bark
    // ---------------------------------------------------------------------------------------------------------------

    private TreeSurfaceData MeshBark(TreeSkeleton skeleton, TreeParams parameters, in TreeMeshDetail detail, out double error)
    {
        Clear();
        var stride = Math.Max(1, detail.SectionStride);
        var scale = parameters.Scale;
        var weightScale = 1.0 / (skeleton.Levels + 1);
        double maxCentreError = 0, maxRadiusError = 0;

        foreach (var branch in skeleton.Branches)
        {
            // Branches thinner than the threshold would hide inside the blobs (the trunk always stays).
            if (branch.Level > 0 && branch.BaseRadius < parameters.LowPolyMinBranchRadius)
                continue;

            var sections = skeleton.SectionsOf(branch);
            var segments = TreeGenerator.SegmentsFor(branch.SegmentCount, detail.SegmentFactor);
            var wrapsX = Math.Max(1, ThreeMath.JsRound(branch.BaseRadius * parameters.BarkTextureScaleX));
            var ringCount = TreeGenerator.SampledRingCount(sections.Length, stride);
            var level = branch.Level / 4f;

            _rings.Clear();
            _ringWeights.Clear();
            _ringV.Clear();
            var previousKept = 0;
            for (var k = 0; k < ringCount; k++)
            {
                var s = k == ringCount - 1 ? sections.Length - 1 : k * stride;
                var section = sections[s];
                var q = Quatd.FromEuler(section.Orientation);
                for (var j = 0; j < segments; j++)
                {
                    var angle = 2.0 * Math.PI * j / segments;
                    _rings.Add(new Vec3d(Math.Cos(angle), 0, Math.Sin(angle)).MultiplyScalar(section.Radius).ApplyQuaternion(q).Add(section.Origin));
                }

                var t = (double)s / branch.SectionCount;
                _ringWeights.Add((float)((branch.WeightBase + (branch.WeightTip - branch.WeightBase) * t) * weightScale));
                _ringV.Add(t);

                if (k > 0 && s - previousKept > 1)
                {
                    for (var d = previousKept + 1; d < s; d++)
                    {
                        var expected = Vec3d.Lerp(sections[previousKept].Origin, section.Origin, (double)(d - previousKept) / (s - previousKept));
                        maxCentreError = Math.Max(maxCentreError, sections[d].Origin.DistanceTo(expected));
                    }
                }

                previousKept = s;
            }

            var full = Math.Max(3, branch.SegmentCount);
            if (segments != full)
                maxRadiusError = Math.Max(maxRadiusError, branch.BaseRadius * Math.Abs(Math.Cos(Math.PI / full) - Math.Cos(Math.PI / segments)));

            for (var i = 0; i < ringCount - 1; i++)
            {
                for (var j = 0; j < segments; j++)
                {
                    var j1 = (j + 1) % segments;
                    var a = _rings[i * segments + j];
                    var b = _rings[i * segments + j1];
                    var c = _rings[(i + 1) * segments + j];
                    var d = _rings[(i + 1) * segments + j1];
                    var u0 = (float)((double)j / segments * wrapsX);
                    var u1 = (float)((double)(j + 1) / segments * wrapsX);
                    var v0 = (float)_ringV[i];
                    var v1 = (float)_ringV[i + 1];
                    var c0 = new Vector4(_ringWeights[i], level, branch.Phase, 0);
                    var c1 = new Vector4(_ringWeights[i + 1], level, branch.Phase, 0);

                    // Ez Tree's (v1, v3, v2), (v2, v3, v4) winding.
                    EmitBarkTriangle(skeleton, a, c, b, new Vector2(u0, v0), new Vector2(u0, v1), new Vector2(u1, v0), c0, c1, c0, scale);
                    EmitBarkTriangle(skeleton, b, c, d, new Vector2(u1, v0), new Vector2(u0, v1), new Vector2(u1, v1), c0, c1, c1, scale);
                }
            }
        }

        error = maxCentreError + maxRadiusError;
        return Take(withColors: false);
    }

    private void EmitBarkTriangle(TreeSkeleton skeleton, Vec3d a, Vec3d b, Vec3d c, Vector2 ua, Vector2 ub, Vector2 uc,
        Vector4 ca, Vector4 cb, Vector4 cc, double scale)
    {
        var cross = Vec3d.Cross(b.Sub(a), c.Sub(a));
        var length = cross.Length();
        if (!(length > 1e-12))
            return; // degenerate (an evergreen's tip ring has radius 0)

        var normal = cross.DivideScalar(length).ToFloat();
        var first = _positions.Count;
        Emit(a, normal, ua, ca with { W = TreeGenerator.BarkOcclusion(a.Y, skeleton.Height) }, scale);
        Emit(b, normal, ub, cb with { W = TreeGenerator.BarkOcclusion(b.Y, skeleton.Height) }, scale);
        Emit(c, normal, uc, cc with { W = TreeGenerator.BarkOcclusion(c.Y, skeleton.Height) }, scale);
        _indices.Add(first);
        _indices.Add(first + 1);
        _indices.Add(first + 2);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Leaf blobs
    // ---------------------------------------------------------------------------------------------------------------

    private TreeSurfaceData MeshBlobs(TreeSkeleton skeleton, TreeParams parameters, in TreeMeshDetail detail, out double error)
    {
        Clear();
        error = 0;
        var leaves = skeleton.Leaves;
        if (leaves.Count == 0)
            return TreeSurfaceData.Empty;

        // 1. Bucket the leaf origins into a grid of BlobSize cells (clusters in first-appearance order).
        _cells.Clear();
        _clusters.Clear();
        _leafCluster.Clear();
        var cell = parameters.BlobSize > 1e-6 ? parameters.BlobSize : 1e-6;
        foreach (var leaf in leaves)
        {
            var key = ((long)Math.Floor(leaf.Origin.X / cell), (long)Math.Floor(leaf.Origin.Y / cell), (long)Math.Floor(leaf.Origin.Z / cell));
            if (!_cells.TryGetValue(key, out var index))
            {
                index = _clusters.Count;
                _cells.Add(key, index);
                _clusters.Add(default);
            }

            _clusters[index] = _clusters[index].Add(leaf);
            _leafCluster.Add(index);
        }

        // 2. Merge the closest pair of clusters until at most MaxBlobs remain (greedy, deterministic: ties by index).
        var maxBlobs = Math.Max(1, detail.MaxBlobs > 0 ? detail.MaxBlobs : parameters.MaxBlobs);
        MergeClusters(maxBlobs);

        // Clusters left alive become blobs.
        _blobs.Clear();
        var evergreen = parameters.Type == TreeType.Evergreen;
        var shape = evergreen ? BlobShape.Cone : parameters.BlobShape;
        for (var i = 0; i < _clusters.Count; i++)
        {
            var cluster = _clusters[i];
            if (cluster.Count == 0)
                continue;
            _blobs.Add(Blob.From(cluster, i, skeleton.Seed));
        }

        // Error: how far the leaves are, on average, from their blob's centre (rises as blobs merge).
        double spread = 0;
        for (var l = 0; l < leaves.Count; l++)
        {
            var root = Root(_leafCluster[l]);
            spread += leaves[l].Origin.DistanceTo(_clusters[root].Mean);
        }

        error = spread / leaves.Count;

        // 3–5. Build each blob, drop triangles inside another blob, split the normals.
        var scale = parameters.Scale;
        var blobDetail = Math.Clamp(detail.BlobDetail, 0, Icospheres.Length - 1);
        for (var b = 0; b < _blobs.Count; b++)
        {
            var blob = _blobs[b];
            BuildBlob(blob, shape, blobDetail, parameters.BlobJitter, skeleton.Seed);

            double minY = double.MaxValue, maxY = double.MinValue;
            foreach (var v in _blobVertices)
            {
                minY = Math.Min(minY, v.Y);
                maxY = Math.Max(maxY, v.Y);
            }

            var heightRange = maxY - minY > 1e-9 ? maxY - minY : 1;
            var shade = 1f + 0.08f * (TreeGenerator.Hash01(Mix((uint)skeleton.Seed, (uint)blob.Id, 0xB10Bu)) * 2f - 1f);
            var colour = new Vector4(shade, shade, shade, 1f);

            for (var t = 0; t < _blobTriangles.Count; t += 3)
            {
                var a = _blobVertices[_blobTriangles[t]];
                var bb = _blobVertices[_blobTriangles[t + 1]];
                var c = _blobVertices[_blobTriangles[t + 2]];
                if (InsideOtherBlob(a, b) && InsideOtherBlob(bb, b) && InsideOtherBlob(c, b))
                    continue; // merged: hidden inside a neighbour

                var cross = Vec3d.Cross(bb.Sub(a), c.Sub(a));
                var length = cross.Length();
                if (!(length > 1e-12))
                    continue;

                // Blobs are star-shaped around their centre: make every face point away from it.
                var centroid = new Vec3d((a.X + bb.X + c.X) / 3, (a.Y + bb.Y + c.Y) / 3, (a.Z + bb.Z + c.Z) / 3);
                if (cross.Dot(centroid.Sub(blob.Center)) < 0)
                {
                    (bb, c) = (c, bb);
                    cross = cross.MultiplyScalar(-1);
                }

                var normal = cross.DivideScalar(length).ToFloat();
                var first = _positions.Count;
                EmitBlobVertex(skeleton, a, normal, blob, minY, heightRange, colour, scale);
                EmitBlobVertex(skeleton, bb, normal, blob, minY, heightRange, colour, scale);
                EmitBlobVertex(skeleton, c, normal, blob, minY, heightRange, colour, scale);
                _indices.Add(first);
                _indices.Add(first + 1);
                _indices.Add(first + 2);
            }
        }

        return Take(withColors: true);
    }

    private void EmitBlobVertex(TreeSkeleton skeleton, Vec3d p, Vector3 normal, in Blob blob, double minY, double heightRange, Vector4 colour, double scale)
    {
        var h = (p.Y - minY) / heightRange;
        Emit(p, normal, new Vector2(0, (float)(1 - h)),
            new Vector4((float)blob.Weight, 1f, blob.Phase, TreeGenerator.CanopyOcclusion(skeleton, p)), scale);
        _colors.Add(colour);
    }

    private void MergeClusters(int maxBlobs)
    {
        var n = _clusters.Count;
        var alive = n;
        if (alive <= maxBlobs)
            return;

        if (_nearest.Length < n)
        {
            _nearest = new int[n];
            _nearestDistance = new double[n];
            _means = new Vec3d[n];
        }

        for (var i = 0; i < n; i++)
            _means[i] = _clusters[i].Mean;

        for (var i = 0; i < n; i++)
            FindNearest(i);

        while (alive > maxBlobs)
        {
            // The globally closest pair: the smallest nearest-neighbour distance (ties: the lowest index).
            var best = -1;
            for (var i = 0; i < n; i++)
            {
                if (_clusters[i].Count == 0)
                    continue;
                if (best < 0 || _nearestDistance[i] < _nearestDistance[best])
                    best = i;
            }

            var other = _nearest[best];
            var keep = Math.Min(best, other);
            var drop = Math.Max(best, other);
            _clusters[keep] = _clusters[keep].Merge(_clusters[drop]);
            _clusters[drop] = _clusters[drop] with { Count = 0, MergedInto = keep };
            _means[keep] = _clusters[keep].Mean;
            alive--;

            // Refresh the neighbours that pointed at either of the pair, and the merged cluster's own.
            FindNearest(keep);
            var mean = _means[keep];
            for (var k = 0; k < n; k++)
            {
                if (k == keep || _clusters[k].Count == 0)
                    continue;
                if (_nearest[k] == keep || _nearest[k] == drop)
                {
                    FindNearest(k);
                }
                else
                {
                    var d = _means[k].DistanceTo(mean);
                    if (d < _nearestDistance[k] || (d == _nearestDistance[k] && keep < _nearest[k]))
                    {
                        _nearest[k] = keep;
                        _nearestDistance[k] = d;
                    }
                }
            }
        }
    }

    private void FindNearest(int i)
    {
        var mean = _means[i];
        var best = -1;
        var bestDistance = double.MaxValue;
        for (var k = 0; k < _clusters.Count; k++)
        {
            if (k == i || _clusters[k].Count == 0)
                continue;
            var d = _means[k].DistanceTo(mean);
            if (d < bestDistance)
            {
                best = k;
                bestDistance = d;
            }
        }

        _nearest[i] = best;
        _nearestDistance[i] = bestDistance;
    }

    private int Root(int cluster)
    {
        while (_clusters[cluster].Count == 0)
            cluster = _clusters[cluster].MergedInto;
        return cluster;
    }

    private void BuildBlob(in Blob blob, BlobShape shape, int detail, double jitter, int seed)
    {
        _blobVertices.Clear();
        _blobTriangles.Clear();
        if (shape == BlobShape.Cone)
        {
            BuildCone(blob, detail, jitter, seed);
            return;
        }

        var (vertices, triangles) = Icospheres[detail];
        for (var v = 0; v < vertices.Length; v++)
        {
            var unit = vertices[v];
            if (shape == BlobShape.Hemisphere)
                unit = unit with { Y = Math.Max(unit.Y, -0.3) }; // a flat underside
            var push = 1 + (TreeGenerator.Hash01(Mix((uint)seed, (uint)blob.Id, (uint)v)) * 2 - 1) * jitter;
            _blobVertices.Add(new Vec3d(
                blob.Center.X + unit.X * blob.Radii.X * push,
                blob.Center.Y + unit.Y * blob.Radii.Y * push,
                blob.Center.Z + unit.Z * blob.Radii.Z * push));
        }

        _blobTriangles.AddRange(triangles);
    }

    /// <summary>
    /// Evergreens: a cone leaning half-way from up towards the mean direction of its leaves' branches, so the canopy
    /// reads as stacked cones.
    /// </summary>
    private void BuildCone(in Blob blob, int detail, double jitter, int seed)
    {
        var sides = detail == 0 ? 6 : 9;
        var axis = UnitY.Add(blob.BranchDirection.MultiplyScalar(0.5)).Normalize();
        var radius = Math.Max(blob.Radii.X, blob.Radii.Z);
        var height = Math.Max(2.2 * blob.Radii.Y, radius);

        // An orthonormal frame around the axis.
        var reference = Math.Abs(axis.X) < 0.9 ? new Vec3d(1, 0, 0) : new Vec3d(0, 0, 1);
        var tangent = Vec3d.Cross(axis, reference).Normalize();
        var bitangent = Vec3d.Cross(axis, tangent);

        var baseCentre = blob.Center.Sub(axis.MultiplyScalar(0.35 * height));
        var apex = blob.Center.Add(axis.MultiplyScalar(0.65 * height));
        for (var i = 0; i < sides; i++)
        {
            var angle = 2.0 * Math.PI * i / sides;
            var push = 1 + (TreeGenerator.Hash01(Mix((uint)seed, (uint)blob.Id, (uint)i)) * 2 - 1) * jitter;
            var offset = tangent.MultiplyScalar(Math.Cos(angle) * radius * push).Add(bitangent.MultiplyScalar(Math.Sin(angle) * radius * push));
            _blobVertices.Add(baseCentre.Add(offset));
        }

        var apexIndex = _blobVertices.Count;
        _blobVertices.Add(apex);
        var centreIndex = _blobVertices.Count;
        _blobVertices.Add(baseCentre);
        for (var i = 0; i < sides; i++)
        {
            var next = (i + 1) % sides;
            _blobTriangles.Add(i);
            _blobTriangles.Add(next);
            _blobTriangles.Add(apexIndex);
            _blobTriangles.Add(centreIndex);
            _blobTriangles.Add(next);
            _blobTriangles.Add(i);
        }
    }

    private bool InsideOtherBlob(Vec3d p, int self)
    {
        for (var b = 0; b < _blobs.Count; b++)
        {
            if (b == self)
                continue;
            var blob = _blobs[b];
            var dx = (p.X - blob.Center.X) / blob.Radii.X;
            var dy = (p.Y - blob.Center.Y) / blob.Radii.Y;
            var dz = (p.Z - blob.Center.Z) / blob.Radii.Z;
            if (dx * dx + dy * dy + dz * dz < 0.81) // well inside (0.9 of the radius): jitter cannot poke it out
                return true;
        }

        return false;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Output
    // ---------------------------------------------------------------------------------------------------------------

    private void Emit(Vec3d position, Vector3 normal, Vector2 uv, Vector4 custom0, double scale)
    {
        _positions.Add(TreeGenerator.Scaled(position, scale));
        _normals.Add(normal);
        _uvs.Add(uv);
        _custom0.Add(custom0);
    }

    private void Clear()
    {
        _positions.Clear();
        _normals.Clear();
        _uvs.Clear();
        _custom0.Clear();
        _colors.Clear();
        _indices.Clear();
    }

    private TreeSurfaceData Take(bool withColors) =>
        _positions.Count == 0
            ? TreeSurfaceData.Empty
            : new TreeSurfaceData([.. _positions], [.. _normals], [.. _uvs], [.. _custom0], withColors ? [.. _colors] : [], [.. _indices]);

    private static uint Mix(uint a, uint b, uint c) => a * 73856093u ^ b * 19349663u ^ c * 83492791u;

    private static (Vec3d[] Vertices, int[] Triangles)[] BuildIcospheres()
    {
        var t = (1 + Math.Sqrt(5)) / 2;
        List<Vec3d> vertices =
        [
            new(-1, t, 0), new(1, t, 0), new(-1, -t, 0), new(1, -t, 0),
            new(0, -1, t), new(0, 1, t), new(0, -1, -t), new(0, 1, -t),
            new(t, 0, -1), new(t, 0, 1), new(-t, 0, -1), new(-t, 0, 1),
        ];
        for (var i = 0; i < vertices.Count; i++)
            vertices[i] = vertices[i].Normalize();

        int[] faces =
        [
            0, 11, 5, 0, 5, 1, 0, 1, 7, 0, 7, 10, 0, 10, 11,
            1, 5, 9, 5, 11, 4, 11, 10, 2, 10, 7, 6, 7, 1, 8,
            3, 9, 4, 3, 4, 2, 3, 2, 6, 3, 6, 8, 3, 8, 9,
            4, 9, 5, 2, 4, 11, 6, 2, 10, 8, 6, 7, 9, 8, 1,
        ];
        var level0 = (vertices.ToArray(), Outward(vertices, faces));

        // One subdivision: split every edge at its (normalized) midpoint.
        var midpoints = new Dictionary<(int, int), int>();
        var subdivided = new List<int>(faces.Length * 4);
        for (var f = 0; f < faces.Length; f += 3)
        {
            int a = faces[f], b = faces[f + 1], c = faces[f + 2];
            var ab = Midpoint(a, b);
            var bc = Midpoint(b, c);
            var ca = Midpoint(c, a);
            subdivided.AddRange([a, ab, ca, b, bc, ab, c, ca, bc, ab, bc, ca]);
        }

        var level1 = (vertices.ToArray(), Outward(vertices, [.. subdivided]));
        return [level0, level1];

        int Midpoint(int a, int b)
        {
            var key = a < b ? (a, b) : (b, a);
            if (midpoints.TryGetValue(key, out var index))
                return index;
            var va = vertices[a];
            var vb = vertices[b];
            index = vertices.Count;
            vertices.Add(new Vec3d((va.X + vb.X) / 2, (va.Y + vb.Y) / 2, (va.Z + vb.Z) / 2).Normalize());
            midpoints.Add(key, index);
            return index;
        }
    }

    private static int[] Outward(List<Vec3d> vertices, int[] triangles)
    {
        var result = (int[])triangles.Clone();
        for (var i = 0; i < result.Length; i += 3)
        {
            var a = vertices[result[i]];
            var b = vertices[result[i + 1]];
            var c = vertices[result[i + 2]];
            if (Vec3d.Cross(b.Sub(a), c.Sub(a)).Dot(a.Add(b).Add(c)) < 0)
                (result[i + 1], result[i + 2]) = (result[i + 2], result[i + 1]);
        }

        return result;
    }

    /// <summary>Running sums of a cluster of leaves (merging adds them), and the cluster it was merged into.</summary>
    private readonly record struct Cluster(
        int Count,
        Vec3d Sum,
        Vec3d SumSquares,
        double SizeSum,
        double WeightSum,
        Vec3d DirectionSum,
        int MergedInto)
    {
        public Vec3d Mean => Count > 0 ? Sum.MultiplyScalar(1.0 / Count) : default;

        public Cluster Add(in SkeletonLeaf leaf) => new(
            Count + 1,
            Sum.Add(leaf.Origin),
            SumSquares.Add(new Vec3d(leaf.Origin.X * leaf.Origin.X, leaf.Origin.Y * leaf.Origin.Y, leaf.Origin.Z * leaf.Origin.Z)),
            SizeSum + leaf.Size,
            WeightSum + leaf.Weight,
            DirectionSum.Add(leaf.BranchDirection),
            -1);

        public Cluster Merge(in Cluster other) => new(
            Count + other.Count,
            Sum.Add(other.Sum),
            SumSquares.Add(other.SumSquares),
            SizeSum + other.SizeSum,
            WeightSum + other.WeightSum,
            DirectionSum.Add(other.DirectionSum),
            -1);
    }

    /// <summary>A blob: an ellipsoid sized from its leaves' spread plus half a leaf.</summary>
    private readonly record struct Blob(int Id, Vec3d Center, Vec3d Radii, double Weight, float Phase, Vec3d BranchDirection)
    {
        public static Blob From(in Cluster cluster, int id, int seed)
        {
            var n = cluster.Count;
            var mean = cluster.Mean;
            var sx = Math.Sqrt(Math.Max(0, cluster.SumSquares.X / n - mean.X * mean.X));
            var sy = Math.Sqrt(Math.Max(0, cluster.SumSquares.Y / n - mean.Y * mean.Y));
            var sz = Math.Sqrt(Math.Max(0, cluster.SumSquares.Z / n - mean.Z * mean.Z));

            // sqrt(3) σ is the half extent of a uniform spread; keep the blob from going flat on any axis.
            var root3 = Math.Sqrt(3);
            var iso = root3 * (sx + sy + sz) / 3;
            var halfLeaf = 0.5 * cluster.SizeSum / n;
            var radii = new Vec3d(
                Math.Max(root3 * sx, 0.5 * iso) + halfLeaf,
                Math.Max(root3 * sy, 0.5 * iso) + halfLeaf,
                Math.Max(root3 * sz, 0.5 * iso) + halfLeaf);

            var phase = TreeGenerator.Hash01(Mix((uint)seed, (uint)id, 0xFA5Eu));
            return new Blob(id, mean, radii, cluster.WeightSum / n, phase, cluster.DirectionSum.Normalize());
        }
    }
}
