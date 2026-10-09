using System.Numerics;

namespace MainframeEngine.Trees;

/// <summary>Leaf-cluster cards (ADR 0172): engine extensions of the port, RNG-free.</summary>
public sealed partial class TreeGenerator
{
    /// <summary>
    /// The inputs of the twig a cluster card shows (ADR 0172): one branch (level 0 of a small tree) as long as
    /// <paramref name="leafSlots"/> leaf slots of <paramref name="tree"/>'s last level, carrying
    /// <c>leafSlots × leafDensity</c> of its leaves (same texture, size, angle and billboard), curled like that level,
    /// in Ez Tree units (scale 1). <paramref name="variant"/> picks the seed, so every variant is another twig of the kind.
    /// </summary>
    public static TreeParams TwigParams(TreeParams tree, int leafSlots, double leafDensity, int seed, int variant)
    {
        ArgumentNullException.ThrowIfNull(tree);
        tree.Validate();
        var level = tree.Levels;
        var count = Math.Max(1, tree.LeafCount);
        var spacing = tree.Length[level] * (1 - tree.LeafStart) / count;
        var length = Math.Clamp(spacing * Math.Max(1, leafSlots), 0.05 * tree.Length[level], tree.Length[level]);
        var twig = new TreeParams
        {
            Seed = unchecked(seed * 7919 + variant * 104729 + 17),
            Type = tree.Type,
            Levels = 0,
            Angle = [0, 0, 0, 0],
            Children = [0, 0, 0, 0],
            Gnarliness = [tree.Gnarliness[level], 0, 0, 0],
            Length = [length, 1, 1, 1],
            Radius = [Math.Max(0.02, 0.03 * length), 0.5, 0.5, 0.5],
            Sections = [6, 1, 1, 1],
            Segments = [3, 3, 3, 3],
            Start = [0, 0, 0, 0],
            Taper = [0.7, 0.7, 0.7, 0.7],
            Twist = [tree.Twist[level], 0, 0, 0],
            GrowthDirection = Vector3.UnitY,
            GrowthForce = 0,
            LeafBillboard = tree.LeafBillboard,
            LeafAngle = tree.LeafAngle,
            LeafCount = Math.Max(1, (int)Math.Round(Math.Max(1, leafSlots) * Math.Max(0.1, leafDensity))),
            LeafStart = 0,
            LeafSize = tree.LeafSize,
            LeafSizeVariance = tree.LeafSizeVariance,
            LeafRoundedNormals = false,
            Scale = 1,
        };
        return twig;
    }

    // One card pair per LeafSlots consecutive leaf slots of a branch: anchored at the group's first slot, along the
    // branch, turned about it by a hash, textured with a hashed atlas cell.
    private static TreeSurfaceData MeshClusters(TreeSkeleton skeleton, TreeParams parameters, int stride, double sizeScale, TreeBillboard billboard,
        out double error, List<int>? cardBranches = null)
    {
        var card = parameters.ClusterCard;
        var slots = card.LeafSlots;
        var leaves = skeleton.Leaves;

        // Group the slots: each branch's leaves (contiguous in the skeleton) by their distance from the branch base.
        var groups = new List<(int First, int Count)>();
        var order = new List<int>(leaves.Count);
        var i = 0;
        while (i < leaves.Count)
        {
            var branch = leaves[i].Branch;
            var end = i;
            while (end < leaves.Count && leaves[end].Branch == branch)
                end++;
            var start = order.Count;
            var origin = branch >= 0 && branch < skeleton.Branches.Count
                ? skeleton.Sections[skeleton.Branches[branch].FirstSection].Origin
                : leaves[i].Origin;
            for (var k = i; k < end; k++)
                order.Add(k);
            order.Sort(start, end - i, Comparer<int>.Create((a, b) =>
            {
                var c = leaves[a].Origin.DistanceTo(origin).CompareTo(leaves[b].Origin.DistanceTo(origin));
                return c != 0 ? c : a.CompareTo(b);
            }));
            for (var g = start; g < order.Count; g += slots)
                groups.Add((g, Math.Min(slots, order.Count - g)));
            i = end;
        }

        var kept = (groups.Count + stride - 1) / stride;
        var quads = billboard == TreeBillboard.Double ? 2 : 1;
        error = 0;
        if (kept == 0)
            return TreeSurfaceData.Empty;

        var vertexCount = kept * quads * 4;
        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var custom0 = new Vector4[vertexCount];
        var indices = new int[kept * quads * 6];

        var scale = parameters.Scale;
        var rounding = Math.Clamp(parameters.ClusterNormalRounding, 0, 1);
        var width = card.Width * sizeScale;
        var height = card.Height * sizeScale;
        int vertex = 0, index = 0;
        for (var g = 0; g < groups.Count; g += stride)
        {
            var (first, count) = groups[g];
            var anchorLeaf = leaves[order[first]];
            var anchor = anchorLeaf.Origin;

            // Along the branch: the mean of the slots' branch directions.
            var up = Vec3d.Zero;
            for (var k = 0; k < count; k++)
                up = up.Add(leaves[order[first + k]].BranchDirection);
            up = up.Length() > 1e-9 ? up.Normalize() : Vec3d.UnitY;

            var hash = (uint)(anchorLeaf.Branch * 2654435761u) ^ (uint)(g * 40503u + 0x9E3779B9u);
            var turn = Hash01(hash) * Math.PI;
            var cell = (int)(Hash01(hash ^ 0x68E31DA4u) * card.Variants) % card.Variants;
            var column = cell % card.Columns;
            var row = cell / card.Columns;
            var u0 = (float)column / card.Columns;
            var u1 = (float)(column + 1) / card.Columns;
            var v0 = (float)row / card.Rows;
            var v1 = (float)(row + 1) / card.Rows;

            // A side axis perpendicular to the branch, turned about it.
            var reference = Math.Abs(up.Y) < 0.95 ? Vec3d.UnitY : new Vec3d(1, 0, 0);
            var side = Vec3d.Cross(up, reference).Normalize().ApplyQuaternion(Quatd.FromAxisAngle(up, turn));
            var weight = (float)anchorLeaf.Weight;
            var phase = anchorLeaf.Phase;
            cardBranches?.Add(anchorLeaf.Branch);

            for (var quad = 0; quad < quads; quad++)
            {
                var x = quad == 0 ? side : Vec3d.Cross(side, up).Normalize().MultiplyScalar(-1); // the second card at 90°
                var face = Vec3d.Cross(x, up); // the side the baked twig faces (the cell's view)
                var center = anchor.Add(up.MultiplyScalar((0.5 - card.BaseV) * height));
                var outward = CanopyNormal(skeleton, center);
                if (face.Dot(outward) < 0)
                    face = face.MultiplyScalar(-1);

                for (var c = 0; c < 4; c++)
                {
                    // Ez Tree's corner order: top-left, bottom-left, bottom-right, top-right.
                    var cu = c is 0 or 1 ? -0.5 : 0.5;
                    var cv = c is 0 or 3 ? 0.0 : 1.0; // 0 at the card's top
                    var position = anchor.Add(x.MultiplyScalar(cu * width)).Add(up.MultiplyScalar((card.BaseV - cv) * height));
                    var round = CanopyNormal(skeleton, position);
                    var normal = face.MultiplyScalar(1 - rounding).Add(round.MultiplyScalar(rounding));
                    normal = normal.Length() > 1e-9 ? normal.Normalize() : face;

                    // Flutter grows from the stem base (none) to the card's top (full): Custom0.y in (0.75, 1) (wind.slang).
                    var tip = Math.Clamp((card.BaseV - cv) / Math.Max(card.BaseV, 1e-6), 0, 1);
                    positions[vertex + c] = Scaled(position, scale);
                    normals[vertex + c] = normal.ToFloat();
                    uvs[vertex + c] = new Vector2(cu < 0 ? u0 : u1, cv < 0.5 ? v0 : v1);
                    custom0[vertex + c] = new Vector4(weight, (float)(0.75 + 0.2489 * tip), phase, CanopyOcclusion(skeleton, position));
                }

                indices[index++] = vertex;
                indices[index++] = vertex + 1;
                indices[index++] = vertex + 2;
                indices[index++] = vertex;
                indices[index++] = vertex + 2;
                indices[index++] = vertex + 3;
                vertex += 4;
            }
        }

        if (stride > 1)
        {
            // Mean distance of a dropped card's anchor to the kept one before it.
            double sum = 0;
            var dropped = 0;
            for (var g = 0; g < groups.Count; g++)
            {
                var keptGroup = g - g % stride;
                if (keptGroup == g)
                    continue;
                sum += leaves[order[groups[g].First]].Origin.DistanceTo(leaves[order[groups[keptGroup].First]].Origin);
                dropped++;
            }

            error = dropped > 0 ? sum / dropped : 0;
        }

        return new TreeSurfaceData(positions, normals, uvs, custom0, [], indices);
    }

    /// <summary>The outward normal of the canopy ellipsoid around the leaf origins at <paramref name="position"/>.</summary>
    internal static Vec3d CanopyNormal(TreeSkeleton skeleton, Vec3d position)
    {
        var e = skeleton.CanopyExtent;
        if (e.X <= 0)
            return Vec3d.UnitY;
        var c = skeleton.CanopyCenter;
        var gradient = new Vec3d((position.X - c.X) / (e.X * e.X), (position.Y - c.Y) / (e.Y * e.Y), (position.Z - c.Z) / (e.Z * e.Z));
        return gradient.Length() > 1e-12 ? gradient.Normalize() : Vec3d.UnitY;
    }
}
