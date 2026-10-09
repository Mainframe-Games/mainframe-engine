// A port of Ez Tree's tree generator (src/lib/tree.js, rng.js; Ez Tree 1.1.0,
// https://github.com/dgreenheck/ez-tree at commit dcf309bd86bd521083d9c70f01f2de45fdc7c457).
//
// MIT License
//
// Copyright (c) 2024 Daniel Greenheck
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Numerics;

namespace MainframeEngine.Trees;

/// <summary>
/// Grows and meshes trees: a literal port of Ez Tree's generator (<c>tree.js</c>), so a seed and a preset give the same
/// tree as in Ez Tree (the same vertex and index counts, positions within 1e-4). Pure C#: it uses no engine nodes or
/// GPU objects, so it can run on any thread (one generator per thread).
/// </summary>
/// <remarks>
/// <para>
/// <see cref="GrowSkeleton"/> does all the RNG work (Ez Tree's <c>#generateSkeleton</c>); <see cref="Mesh"/> uses none
/// (<c>#meshSkeleton</c>), so the levels of detail of one tree are meshed from one skeleton. Everything runs in doubles
/// with three.js's formulas (<see cref="ThreeMath"/>), and positions are rounded to float only when written out, as
/// Ez Tree's <c>Float32Array</c> does.
/// </para>
/// <para>
/// Reuse a generator and a <see cref="TreeSkeleton"/>: after the first tree, growing and meshing allocate only the
/// output arrays (Realistic style).
/// </para>
/// </remarks>
public sealed class TreeGenerator
{
    /// <summary>Bumped whenever the output for the same inputs changes (bake staleness).</summary>
    public const int GeneratorVersion = 1;

    private static readonly Vec3d UnitX = new(1, 0, 0);
    private static readonly Vec3d UnitY = new(0, 1, 0);
    private static readonly Vec3d UnitZ = new(0, 0, 1);

    private readonly Queue<PendingBranch> _branchQueue = new();
    private readonly LowPolyMesher _lowPoly = new();
    private int[] _slots = new int[16];
    private double[] _barkV = new double[256]; // per branch: continuous V at the base, V per unit of length

    private TreeParams _options = null!;
    private TreeSkeleton _skeleton = null!;
    private EzRng _rng;

    /// <summary>
    /// Ez Tree's <c>Tree.defaultLODLevels</c> details: LOD0 full; LOD1 every 3rd ring, 0.75 × sides, every 2nd leaf at
    /// 1.25 × size; LOD2 every 6th ring, 0.4 × sides, every 2nd leaf at 1.3 × size, single billboards.
    /// </summary>
    public static IReadOnlyList<TreeMeshDetail> RealisticLods { get; } =
    [
        new TreeMeshDetail(),
        new TreeMeshDetail { SectionStride = 3, SegmentFactor = 0.75, LeafStride = 2, LeafScale = 1.25 },
        new TreeMeshDetail { SectionStride = 6, SegmentFactor = 0.4, LeafStride = 2, LeafScale = 1.3, Billboard = TreeBillboard.Single },
    ];

    /// <summary>The three levels of detail for <paramref name="style"/>: <see cref="RealisticLods"/>, or LowPoly's from the params.</summary>
    public static TreeMeshDetail[] DefaultLods(TreeParams parameters, TreeStyle style)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (style == TreeStyle.Realistic)
            return [.. RealisticLods];

        var stride = Math.Max(1, parameters.LowPolySectionStride);
        var factor = parameters.LowPolySegmentFactor;
        var blobs = Math.Max(1, parameters.MaxBlobs);
        return
        [
            new TreeMeshDetail { Style = style, SectionStride = stride, SegmentFactor = factor, BlobDetail = parameters.BlobDetail, MaxBlobs = blobs },
            new TreeMeshDetail { Style = style, SectionStride = stride * 2, SegmentFactor = factor * 0.75, BlobDetail = 0, MaxBlobs = Math.Max(1, blobs / 2) },
            new TreeMeshDetail { Style = style, SectionStride = stride * 4, SegmentFactor = factor * 0.5, BlobDetail = 0, MaxBlobs = Math.Max(1, blobs / 4) },
        ];
    }

    /// <summary>Grows the skeleton and meshes every level of <see cref="DefaultLods"/> for <paramref name="style"/>.</summary>
    public TreeMeshData[] Generate(TreeParams parameters, TreeStyle style = TreeStyle.Realistic, TreeSkeleton? skeleton = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        var grown = GrowSkeleton(parameters, parameters.Seed, skeleton);
        var lods = DefaultLods(parameters, style);
        var result = new TreeMeshData[lods.Length];
        for (var i = 0; i < lods.Length; i++)
            result[i] = Mesh(grown, parameters, lods[i]);
        return result;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Skeleton (Ez Tree's #generateSkeleton, #growBranch, generateChildBranches, generateLeaves, #recordLeaf)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Grows the skeleton for <paramref name="seed"/> (Ez Tree's <c>#generateSkeleton</c>): every RNG draw happens here,
    /// in Ez Tree's order. Fills <paramref name="into"/> when given (reusing its lists), else a new skeleton.
    /// </summary>
    public TreeSkeleton GrowSkeleton(TreeParams parameters, int seed, TreeSkeleton? into = null)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        parameters.Validate();

        var skeleton = into ?? new TreeSkeleton();
        skeleton.Clear();
        skeleton.Seed = seed;
        skeleton.Levels = parameters.Levels;

        _options = parameters;
        _skeleton = skeleton;
        _rng = new EzRng(seed);
        _branchQueue.Clear();

        try
        {
            // Create the trunk of the tree first
            _branchQueue.Enqueue(new PendingBranch(
                Vec3d.Zero,
                default,
                parameters.Length[0],
                parameters.Radius[0],
                0,
                parameters.Sections[0],
                parameters.Segments[0],
                WeightBase: 0,
                Phase: 0,
                Parent: -1,
                ParentStart: 0,
                Terminal: false));

            // FIFO: the queue order is the RNG order (Ez Tree's branchQueue.shift()).
            while (_branchQueue.Count > 0)
                GrowBranch(_branchQueue.Dequeue());

            FinishSkeleton(skeleton);
        }
        finally
        {
            _options = null!;
            _skeleton = null!;
        }

        return skeleton;
    }

    private void GrowBranch(in PendingBranch branch)
    {
        var options = _options;
        var level = branch.Level;
        var sectionOrientation = branch.Orientation;
        var sectionOrigin = branch.Origin;

        // Quirk kept: Ez Tree also divides by (levels - 1) when `type === 'Deciduous'` (tree.js:325), but types are lower
        // case ('deciduous'), so that divisor is always 1 and the port has none.
        var sectionLength = branch.Length / branch.SectionCount;

        var firstSection = _skeleton.Sections.Count;
        var target = ToDouble(options.GrowthDirection).Normalize();

        for (var i = 0; i <= branch.SectionCount; i++)
        {
            var sectionRadius = branch.Radius;

            // If final section of final level, set radius to effectively zero
            if (i == branch.SectionCount && level == options.Levels)
                sectionRadius = 0.001;
            else if (options.Type == TreeType.Deciduous)
                sectionRadius *= 1 - options.Taper[level] * ((double)i / branch.SectionCount);
            else if (options.Type == TreeType.Evergreen)
                sectionRadius *= 1 - ((double)i / branch.SectionCount); // evergreens taper to a point

            _skeleton.Sections.Add(new SkeletonSection(sectionOrigin, sectionOrientation, sectionRadius));

            sectionOrigin = sectionOrigin.Add(new Vec3d(0, sectionLength, 0).ApplyEuler(sectionOrientation));

            // Perturb the orientation of the next section randomly. The higher the gnarliness, the larger the potential
            // perturbation. (An evergreen's last ring has radius 0: the values below become infinite or NaN, as in Ez
            // Tree, but that orientation is never used; the draws still happen, so the RNG stays in step.)
            var gnarliness = Math.Max(1, 1 / Math.Sqrt(sectionRadius)) * options.Gnarliness[level];

            sectionOrientation = sectionOrientation with { X = sectionOrientation.X + _rng.Next(gnarliness, -gnarliness) };
            sectionOrientation = sectionOrientation with { Z = sectionOrientation.Z + _rng.Next(gnarliness, -gnarliness) };

            // Apply growth force to the branch
            var qSection = Quatd.FromEuler(sectionOrientation);
            var qTwist = Quatd.FromAxisAngle(UnitY, options.Twist[level]);
            qSection = qSection.Multiply(qTwist);

            // Rotate the section's growth direction toward the force direction (positive strength) or away from it.
            var sectionUp = UnitY.ApplyQuaternion(qSection);
            var axis = Vec3d.Cross(sectionUp, target);
            var sinFull = axis.Length();
            if (sinFull > 1e-6)
            {
                axis = axis.DivideScalar(sinFull);
                var fullAngle = Math.Atan2(sinFull, sectionUp.Dot(target));
                var step = options.GrowthForce / sectionRadius;
                var clamped = Math.Max(-fullAngle, Math.Min(fullAngle, step));
                qSection = qSection.Premultiply(Quatd.FromAxisAngle(axis, clamped));
            }

            // The trellis slot (Ez Tree's calculateTrellisForce is not ported).
            if (options.ExtraForce is { } force &&
                force.TryGetForce(sectionOrigin.ToFloat(), sectionRadius, out var direction, out var strength))
            {
                var qTrellis = Quatd.FromUnitVectors(UnitY, ToDouble(direction));
                qSection = qSection.RotateTowards(qTrellis, strength);
            }

            sectionOrientation = EulerXyz.FromQuaternion(qSection);
        }

        // Engine data (no RNG): the wind coordinate runs from the parent's value at the attachment point to the next
        // level (when a terminal branch continues this one) or to levels + 1 (a tip); terminal branches keep the phase.
        var continues = options.Type == TreeType.Deciduous && level < options.Levels;
        var weightTip = continues ? level + 1.0 : options.Levels + 1.0;
        var index = _skeleton.Branches.Count;
        var phase = branch.Terminal ? branch.Phase : Hash01((uint)index * 2654435761u + 0x9E3779B9u);
        var skeletonBranch = new SkeletonBranch(
            firstSection,
            branch.SectionCount,
            branch.SegmentCount,
            branch.Radius,
            level,
            branch.Length,
            branch.WeightBase,
            weightTip,
            phase,
            branch.Parent,
            branch.ParentStart,
            branch.Terminal);
        _skeleton.Branches.Add(skeletonBranch);

        var lastSection = _skeleton.Sections[^1];

        // Deciduous trees have a terminal branch that grows out of the end of the parent branch
        if (options.Type == TreeType.Deciduous)
        {
            if (level < options.Levels)
            {
                _branchQueue.Enqueue(new PendingBranch(
                    lastSection.Origin,
                    lastSection.Orientation,
                    options.Length[level + 1],
                    lastSection.Radius,
                    level + 1,
                    // Section count and segment count must be same as parent branch
                    // since the child branch is growing from the end of the parent branch
                    branch.SectionCount,
                    branch.SegmentCount,
                    WeightBase: weightTip,
                    Phase: phase,
                    Parent: index,
                    ParentStart: 1,
                    Terminal: true));
            }
            else
            {
                RecordLeaf(lastSection.Origin, lastSection.Orientation, skeletonBranch.WeightTip, phase, lastSection.Orientation);
            }
        }

        // If we are on the last branch level, generate leaves
        if (level == options.Levels)
            GenerateLeaves(index);
        else if (level < options.Levels)
            GenerateChildBranches(options.Children[level], level + 1, index);
    }

    private void GenerateChildBranches(int count, int level, int parentIndex)
    {
        var options = _options;
        var parent = _skeleton.Branches[parentIndex];
        var radialOffset = _rng.Next();
        var startMin = options.Start[level];
        var heightStep = (1.0 - startMin) / count;
        var angleSlots = ShuffledIndices(count);

        for (var i = 0; i < count; i++)
        {
            // Stratified sampling along the parent's length: jitter within slot [i, i+1]
            var childBranchStart = startMin + (i + _rng.Next()) * heightStep;

            Interpolate(parent, childBranchStart, out var childBranchOrigin, out var radiusAB, out var parentOrientation);

            var childBranchRadius = options.Radius[level] * radiusAB;

            // Stratified radial angle: each child gets a 2π/count slot, jittered ±½ slot (permuted slot assignment).
            var radialJitter = _rng.Next(0.5, -0.5);
            var radialAngle = 2.0 * Math.PI * (radialOffset + (angleSlots[i] + radialJitter) / count);
            var q1 = Quatd.FromAxisAngle(UnitX, options.Angle[level] / (180 / Math.PI));
            var q2 = Quatd.FromAxisAngle(UnitY, radialAngle);
            var q3 = Quatd.FromEuler(parentOrientation);

            // q3.multiply(q2.multiply(q1))
            var childBranchOrientation = EulerXyz.FromQuaternion(Quatd.Multiply(q3, Quatd.Multiply(q2, q1)));

            var childBranchLength = options.Length[level] * (options.Type == TreeType.Evergreen ? 1.0 - childBranchStart : 1.0);

            if (parentIndex == 0)
                _skeleton.TrunkFirstChildStart = Math.Min(_skeleton.TrunkFirstChildStart, childBranchStart);

            _branchQueue.Enqueue(new PendingBranch(
                childBranchOrigin,
                childBranchOrientation,
                childBranchLength,
                childBranchRadius,
                level,
                options.Sections[level],
                options.Segments[level],
                WeightBase: WeightAt(parent, childBranchStart),
                Phase: 0,
                Parent: parentIndex,
                ParentStart: childBranchStart,
                Terminal: false));
        }
    }

    private void GenerateLeaves(int branchIndex)
    {
        var options = _options;
        var branch = _skeleton.Branches[branchIndex];
        var radialOffset = _rng.Next();
        var count = options.LeafCount;
        var startMin = options.LeafStart;
        var heightStep = (1.0 - startMin) / count;
        var angleSlots = ShuffledIndices(count);

        for (var i = 0; i < count; i++)
        {
            // Stratified sampling along the parent's length.
            var leafStart = startMin + (i + _rng.Next()) * heightStep;

            Interpolate(branch, leafStart, out var leafOrigin, out _, out var parentOrientation);

            // Stratified radial angle with permuted slot assignment.
            var radialJitter = _rng.Next(0.5, -0.5);
            var radialAngle = 2.0 * Math.PI * (radialOffset + (angleSlots[i] + radialJitter) / count);
            var q1 = Quatd.FromAxisAngle(UnitX, options.LeafAngle / (180 / Math.PI));
            var q2 = Quatd.FromAxisAngle(UnitY, radialAngle);
            var q3 = Quatd.FromEuler(parentOrientation);

            var leafOrientation = EulerXyz.FromQuaternion(Quatd.Multiply(q3, Quatd.Multiply(q2, q1)));

            RecordLeaf(leafOrigin, leafOrientation, WeightAt(branch, leafStart), branch.Phase, parentOrientation);
        }
    }

    /// <summary>
    /// The point at <paramref name="start"/> (0–1) along a branch: Ez Tree lerps the origin and radius between the two
    /// sections around it, and slerps the orientation <b>from B to A</b> (<c>qB.slerp(qA, alpha)</c>: a quirk kept, it
    /// runs opposite to the origin lerp).
    /// </summary>
    private void Interpolate(in SkeletonBranch branch, double start, out Vec3d origin, out double radius, out EulerXyz orientation)
    {
        var sections = _skeleton.SectionsOf(branch);
        var last = sections.Length - 1;

        // Find which sections are on either side of the point
        var sectionIndex = (int)Math.Floor(start * last);
        var sectionA = sections[sectionIndex];
        var sectionB = sectionIndex == last ? sectionA : sections[sectionIndex + 1];

        // Find normalized distance from section A to section B (0 to 1)
        var alpha = (start - (double)sectionIndex / last) / (1.0 / last);

        origin = Vec3d.Lerp(sectionA.Origin, sectionB.Origin, alpha);
        radius = (1 - alpha) * sectionA.Radius + alpha * sectionB.Radius;

        var qA = Quatd.FromEuler(sectionA.Orientation);
        var qB = Quatd.FromEuler(sectionB.Orientation);
        orientation = EulerXyz.FromQuaternion(qB.Slerp(qA, alpha));
    }

    /// <summary>Records a leaf; the size variance is drawn here so meshing stays RNG-free (Ez Tree's <c>#recordLeaf</c>).</summary>
    private void RecordLeaf(Vec3d origin, EulerXyz orientation, double weight, float phase, EulerXyz branchOrientation)
    {
        var options = _options;
        var size = options.LeafSize * (1 + _rng.Next(options.LeafSizeVariance, -options.LeafSizeVariance));
        var branchDirection = UnitY.ApplyEuler(branchOrientation).Normalize();
        _skeleton.Leaves.Add(new SkeletonLeaf(origin, orientation, size, weight / (options.Levels + 1), phase, branchDirection));
    }

    /// <summary>Fisher-Yates shuffle of [0..count-1] with the tree's RNG (Ez Tree's <c>shuffledIndices</c>), in a reused buffer.</summary>
    private int[] ShuffledIndices(int count)
    {
        if (_slots.Length < count)
            _slots = new int[Math.Max(count, _slots.Length * 2)];
        var arr = _slots;
        for (var k = 0; k < count; k++)
            arr[k] = k;
        for (var k = count - 1; k > 0; k--)
        {
            var r = (int)Math.Floor(_rng.Next() * (k + 1));
            (arr[k], arr[r]) = (arr[r], arr[k]);
        }

        return arr;
    }

    private static double WeightAt(in SkeletonBranch branch, double t) => branch.WeightBase + (branch.WeightTip - branch.WeightBase) * t;

    private static void FinishSkeleton(TreeSkeleton skeleton)
    {
        var height = 1e-6;
        foreach (var section in skeleton.Sections)
            height = Math.Max(height, section.Origin.Y);

        if (skeleton.Leaves.Count > 0)
        {
            double cx = 0, cy = 0, cz = 0;
            foreach (var leaf in skeleton.Leaves)
            {
                cx += leaf.Origin.X;
                cy += leaf.Origin.Y;
                cz += leaf.Origin.Z;
                height = Math.Max(height, leaf.Origin.Y);
            }

            var n = skeleton.Leaves.Count;
            var center = new Vec3d(cx / n, cy / n, cz / n);
            double ex = 1e-3, ey = 1e-3, ez = 1e-3;
            foreach (var leaf in skeleton.Leaves)
            {
                ex = Math.Max(ex, Math.Abs(leaf.Origin.X - center.X));
                ey = Math.Max(ey, Math.Abs(leaf.Origin.Y - center.Y));
                ez = Math.Max(ez, Math.Abs(leaf.Origin.Z - center.Z));
            }

            skeleton.CanopyCenter = center;
            skeleton.CanopyExtent = new Vec3d(ex, ey, ez);
        }

        skeleton.Height = height;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Meshing (Ez Tree's #meshSkeleton, #meshBranch, #meshLeaf)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Meshes <paramref name="skeleton"/> at <paramref name="detail"/> (Ez Tree's <c>#meshSkeleton</c>). Uses no RNG, so
    /// every level of detail of a tree comes from one skeleton. Positions are scaled by <see cref="TreeParams.Scale"/>.
    /// </summary>
    public TreeMeshData Mesh(TreeSkeleton skeleton, TreeParams parameters, in TreeMeshDetail detail)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(parameters);

        var trunk = TrunkCapsule(skeleton, parameters);
        if (detail.Style == TreeStyle.LowPoly)
            return _lowPoly.Mesh(skeleton, parameters, detail, trunk);

        var sectionStride = Math.Max(1, detail.SectionStride);
        var segmentFactor = detail.SegmentFactor;
        var leafStride = Math.Max(1, detail.LeafStride);
        var leafScale = detail.LeafScale;
        var billboard = detail.Billboard ?? parameters.LeafBillboard;

        var bark = MeshBark(skeleton, parameters, sectionStride, segmentFactor, out var barkError);
        var leaves = MeshLeaves(skeleton, parameters, leafStride, leafScale, billboard, out var leafError);
        return new TreeMeshData(bark, leaves, Math.Max(barkError, leafError) * parameters.Scale, trunk);
    }

    private TreeSurfaceData MeshBark(TreeSkeleton skeleton, TreeParams parameters, int sectionStride, double segmentFactor, out double error)
    {
        // Count first, so the output arrays are allocated once at their exact size.
        int vertexCount = 0, indexCount = 0;
        foreach (var branch in skeleton.Branches)
        {
            var rings = SampledRingCount(branch.SectionCount + 1, sectionStride);
            var segments = SegmentsFor(branch.SegmentCount, segmentFactor);
            vertexCount += rings * (segments + 1);
            indexCount += (rings - 1) * segments * 6;
        }

        if (vertexCount == 0)
        {
            error = 0;
            return TreeSurfaceData.Empty;
        }

        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var custom0 = new Vector4[vertexCount];
        var indices = new int[indexCount];

        if (_barkV.Length < 2 * skeleton.Branches.Count)
            _barkV = new double[Math.Max(2 * skeleton.Branches.Count, _barkV.Length * 2)];

        var scale = parameters.Scale;
        var weightScale = 1.0 / (skeleton.Levels + 1);
        int vertex = 0, index = 0;
        double maxCentreError = 0, maxRadiusError = 0;

        for (var b = 0; b < skeleton.Branches.Count; b++)
        {
            var branch = skeleton.Branches[b];
            var sections = skeleton.SectionsOf(branch);

            // Terminal branches inherit the parent's segmentCount, so parent and child resolve to the same reduced count
            // and junctions stay sealed.
            var segments = SegmentsFor(branch.SegmentCount, segmentFactor);

            // Number of texture wraps around the branch's circumference (constant along the branch).
            var wrapsX = Math.Max(1, ThreeMath.JsRound(branch.BaseRadius * parameters.BarkTextureScaleX));

            // Continuous V: arc length × (wraps / circumference at the base), starting where the parent is at the
            // attachment (parents are always meshed before their children: the skeleton is in queue order). Every
            // section step has the same length, so the arc length at t is t × length.
            var vPerUnit = branch.BaseRadius > 1e-9 ? wrapsX / (2 * Math.PI * branch.BaseRadius) : 0;
            var vStart = 0.0;
            if (branch.Parent >= 0)
            {
                var parent = skeleton.Branches[branch.Parent];
                vStart = _barkV[2 * branch.Parent] + branch.ParentStart * parent.Length * _barkV[2 * branch.Parent + 1];
            }

            _barkV[2 * b] = vStart;
            _barkV[2 * b + 1] = vPerUnit;

            var level = branch.Level / 4f;
            var indexOffset = vertex;
            var sampledCount = SampledRingCount(sections.Length, sectionStride);
            var previousKept = 0;

            // Sample every Nth ring, always keeping the first and last so branch endpoints stay put across detail levels.
            for (var k = 0; k < sampledCount; k++)
            {
                var s = k == sampledCount - 1 ? sections.Length - 1 : k * sectionStride;
                var section = sections[s];
                var q = Quatd.FromEuler(section.Orientation);
                var t = (double)s / branch.SectionCount;
                var weight = (float)((branch.WeightBase + (branch.WeightTip - branch.WeightBase) * t) * weightScale);
                var v = parameters.BarkUv == BarkUvMode.EzTree
                    ? (k % 2 == 0 ? 0.0 : 1.0) // alternates by sampled ring, so skipping keeps the 0/1 tiling
                    : vStart + t * branch.Length * vPerUnit;

                var first = vertex;
                for (var j = 0; j < segments; j++)
                {
                    var angle = 2.0 * Math.PI * j / segments;
                    var ring = new Vec3d(Math.Cos(angle), 0, Math.Sin(angle));

                    var position = ring.MultiplyScalar(section.Radius).ApplyQuaternion(q).Add(section.Origin);
                    var normal = ring.ApplyQuaternion(q).Normalize();

                    positions[vertex] = Scaled(position, scale);
                    normals[vertex] = normal.ToFloat();
                    uvs[vertex] = new Vector2((float)((double)j / segments * wrapsX), (float)v);
                    custom0[vertex] = new Vector4(weight, level, branch.Phase, BarkOcclusion(position.Y, skeleton.Height));
                    vertex++;
                }

                // Duplicate the first vertex so there is continuity in the UV mapping (u = wrapsX samples like u = 0).
                positions[vertex] = positions[first];
                normals[vertex] = normals[first];
                uvs[vertex] = new Vector2((float)wrapsX, (float)v);
                custom0[vertex] = custom0[first];
                vertex++;

                // Geometric error: dropped ring centres against the line between the kept rings.
                if (k > 0 && s - previousKept > 1)
                {
                    var a = sections[previousKept].Origin;
                    var c = section.Origin;
                    for (var d = previousKept + 1; d < s; d++)
                    {
                        var expected = Vec3d.Lerp(a, c, (double)(d - previousKept) / (s - previousKept));
                        maxCentreError = Math.Max(maxCentreError, sections[d].Origin.DistanceTo(expected));
                    }
                }

                previousKept = s;
            }

            if (segments != branch.SegmentCount)
            {
                // Radius lost to fewer sides: the change of the polygon's sagitta.
                var full = Math.Max(3, branch.SegmentCount);
                maxRadiusError = Math.Max(maxRadiusError,
                    branch.BaseRadius * Math.Abs(Math.Cos(Math.PI / full) - Math.Cos(Math.PI / segments)));
            }

            // Build geometry for each section of the branch (cylinder without end caps)
            var n = segments + 1;
            for (var i = 0; i < sampledCount - 1; i++)
            {
                for (var j = 0; j < segments; j++)
                {
                    var v1 = indexOffset + i * n + j;
                    var v2 = indexOffset + i * n + (j + 1);
                    var v3 = v1 + n;
                    var v4 = v2 + n;
                    indices[index++] = v1;
                    indices[index++] = v3;
                    indices[index++] = v2;
                    indices[index++] = v2;
                    indices[index++] = v3;
                    indices[index++] = v4;
                }
            }
        }

        error = maxCentreError + maxRadiusError;
        return new TreeSurfaceData(positions, normals, uvs, custom0, [], indices);
    }

    private static TreeSurfaceData MeshLeaves(TreeSkeleton skeleton, TreeParams parameters, int leafStride, double leafScale,
        TreeBillboard billboard, out double error)
    {
        var leafCount = (skeleton.Leaves.Count + leafStride - 1) / leafStride;
        var quads = billboard == TreeBillboard.Double ? 2 : 1;
        var vertexCount = leafCount * quads * 4;
        error = 0;
        if (vertexCount == 0)
            return TreeSurfaceData.Empty;

        var positions = new Vector3[vertexCount];
        var normals = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        var custom0 = new Vector4[vertexCount];
        var indices = new int[leafCount * quads * 6];

        var scale = parameters.Scale;
        var rounded = parameters.LeafRoundedNormals;
        int vertex = 0, index = 0;
        for (var i = 0; i < skeleton.Leaves.Count; i += leafStride)
        {
            var leaf = skeleton.Leaves[i];
            var origin = leaf.Origin;
            var qOrientation = Quatd.FromEuler(leaf.Orientation);

            // Width and length of the leaf quad
            var leafSize = leaf.Size * leafScale;
            var w = leafSize;
            var l = leafSize;

            // The normal of the leaf: rounded normals average it with the directions to the vertices (a rounded canopy).
            var n = UnitZ.ApplyQuaternion(qOrientation);

            for (var quad = 0; quad < quads; quad++)
            {
                var rotation = quad == 0 ? 0 : Math.PI / 2;
                var qRotation = Quatd.FromEuler(new EulerXyz(0, rotation, 0));
                for (var c = 0; c < 4; c++)
                {
                    var corner = c switch
                    {
                        0 => new Vec3d(-w / 2, l, 0),
                        1 => new Vec3d(-w / 2, 0, 0),
                        2 => new Vec3d(w / 2, 0, 0),
                        _ => new Vec3d(w / 2, l, 0),
                    };

                    var position = corner.ApplyQuaternion(qRotation).ApplyQuaternion(qOrientation).Add(origin);
                    var normal = rounded ? n.Add(position).Sub(origin).Normalize() : n;

                    positions[vertex + c] = Scaled(position, scale);
                    normals[vertex + c] = normal.ToFloat();
                    custom0[vertex + c] = new Vector4((float)leaf.Weight, 1f, leaf.Phase, CanopyOcclusion(skeleton, position));
                }

                // Ez Tree's UVs are (0,1), (0,0), (1,0), (1,1) with the origin bottom-left; the engine's is top-left.
                uvs[vertex] = new Vector2(0, 0);
                uvs[vertex + 1] = new Vector2(0, 1);
                uvs[vertex + 2] = new Vector2(1, 1);
                uvs[vertex + 3] = new Vector2(1, 0);

                indices[index++] = vertex;
                indices[index++] = vertex + 1;
                indices[index++] = vertex + 2;
                indices[index++] = vertex;
                indices[index++] = vertex + 2;
                indices[index++] = vertex + 3;
                vertex += 4;
            }
        }

        if (leafStride > 1)
        {
            // Mean spacing of the dropped leaves: each one's distance to the kept leaf before it.
            double sum = 0;
            var dropped = 0;
            for (var i = 0; i < skeleton.Leaves.Count; i++)
            {
                var kept = i - i % leafStride;
                if (kept == i)
                    continue;
                sum += skeleton.Leaves[i].Origin.DistanceTo(skeleton.Leaves[kept].Origin);
                dropped++;
            }

            error = dropped > 0 ? sum / dropped : 0;
        }

        return new TreeSurfaceData(positions, normals, uvs, custom0, [], indices);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Shared helpers (also used by the LowPoly mesher)
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Rings kept by Ez Tree's sampling of <paramref name="frames"/> section frames at <paramref name="stride"/>.</summary>
    internal static int SampledRingCount(int frames, int stride)
    {
        var count = (frames - 1) / stride + 1;
        if ((frames - 1) % stride != 0)
            count++;
        return count;
    }

    /// <summary><c>max(3, round(segmentCount × segmentFactor))</c> with JavaScript rounding.</summary>
    internal static int SegmentsFor(int segmentCount, double segmentFactor) =>
        (int)Math.Max(3, ThreeMath.JsRound(segmentCount * segmentFactor));

    internal static Vector3 Scaled(Vec3d v, double scale) => new((float)(v.X * scale), (float)(v.Y * scale), (float)(v.Z * scale));

    /// <summary>Bark AO: 0.6 at the ground to 1 at the top of the tree.</summary>
    internal static float BarkOcclusion(double y, double height) => (float)(0.6 + 0.4 * Math.Clamp(y / height, 0, 1));

    /// <summary>Leaf AO: 0.4 at the canopy's centre to 1 on its hull (the ellipsoid around the leaf origins).</summary>
    internal static float CanopyOcclusion(TreeSkeleton skeleton, Vec3d position)
    {
        var e = skeleton.CanopyExtent;
        if (e.X <= 0)
            return 1;
        var c = skeleton.CanopyCenter;
        var dx = (position.X - c.X) / e.X;
        var dy = (position.Y - c.Y) / e.Y;
        var dz = (position.Z - c.Z) / e.Z;
        var r = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        return (float)(0.4 + 0.6 * Math.Clamp(r, 0, 1));
    }

    /// <summary>A stable hash of <paramref name="x"/> in [0, 1) (no RNG: phases and jitter must not disturb parity).</summary>
    internal static float Hash01(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352du;
        x ^= x >> 15;
        x *= 0x846ca68bu;
        x ^= x >> 16;
        return (x >> 8) * (1f / 16777216f);
    }

    internal static Vec3d ToDouble(Vector3 v) => new(v.X, v.Y, v.Z);

    // ---------------------------------------------------------------------------------------------------------------
    // Trunk capsule
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The trunk's collision capsule in engine units: the trunk radius 1 m above the ground, and a height up to the first
    /// side branch (at least 2 m or the trunk's height, whichever is lower, and at least the capsule's diameter).
    /// </summary>
    public static TreeTrunkCapsule TrunkCapsule(TreeSkeleton skeleton, TreeParams parameters)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        ArgumentNullException.ThrowIfNull(parameters);
        if (skeleton.Branches.Count == 0)
            return default;

        var scale = parameters.Scale > 0 ? parameters.Scale : 1;
        var trunk = skeleton.Branches[0];
        var sections = skeleton.SectionsOf(trunk);

        // Radius at 1 m (1 / scale Ez Tree units) up the trunk.
        var targetY = 1 / scale;
        var radius = sections[^1].Radius;
        for (var i = 1; i < sections.Length; i++)
        {
            if (sections[i].Origin.Y < targetY)
                continue;
            var y0 = sections[i - 1].Origin.Y;
            var y1 = sections[i].Origin.Y;
            var a = y1 > y0 ? Math.Clamp((targetY - y0) / (y1 - y0), 0, 1) : 0;
            radius = sections[i - 1].Radius + (sections[i].Radius - sections[i - 1].Radius) * a;
            break;
        }

        // Height of the first side branch's attachment.
        var start = Math.Clamp(skeleton.TrunkFirstChildStart, 0, 1);
        var last = sections.Length - 1;
        var sectionIndex = Math.Min((int)Math.Floor(start * last), last);
        var next = Math.Min(sectionIndex + 1, last);
        var alpha = start * last - sectionIndex;
        var attachY = sections[sectionIndex].Origin.Y + (sections[next].Origin.Y - sections[sectionIndex].Origin.Y) * alpha;

        var top = sections[^1].Origin.Y * scale;
        var r = (float)(radius * scale);
        var height = Math.Max(attachY * scale, Math.Min(top, 2.0));
        return new TreeTrunkCapsule((float)Math.Max(height, 2.0 * r), r);
    }

    /// <summary>A branch waiting in the queue (Ez Tree's <c>Branch</c>) plus the engine's RNG-free extras.</summary>
    private readonly record struct PendingBranch(
        Vec3d Origin,
        EulerXyz Orientation,
        double Length,
        double Radius,
        int Level,
        int SectionCount,
        int SegmentCount,
        double WeightBase,
        float Phase,
        int Parent,
        double ParentStart,
        bool Terminal);
}
