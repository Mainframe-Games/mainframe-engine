using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using MainframeEngine.Trees;

namespace MainframeEngine;

/// <summary>Per-thread scratch for tracing a <see cref="ProbeBakeScene"/> (instance mailboxes).</summary>
internal sealed class ProbeTraceContext(int instances)
{
    public readonly int[] Stamps = new int[Math.Max(1, instances)];
    public int Stamp;

    public int Next()
    {
        if (++Stamp == int.MaxValue)
        {
            Array.Clear(Stamps);
            Stamp = 1;
        }

        return Stamp;
    }
}

/// <summary>
/// The scene a light-probe bake traces (ADR 0170): ray-traceable proxies of what the volume's world draws. The terrain is
/// its height field (traced analytically, chunk by chunk) with the splat layers' mean colours per cell; trees are
/// branch capsules (from their Ez Tree skeletons) plus a leaf-area density grid per variant (Beer–Lambert canopy);
/// static meshes (<see cref="GIMode.Static"/>) are their triangles. Instances sit in an XZ grid for the rays to walk.
/// Built on one thread (<see cref="ProbeBakeSceneBuilder"/>), then read by many.
/// </summary>
internal sealed class ProbeBakeScene
{
    internal enum InstanceKind : byte
    {
        Tree,
        Mesh,
    }

    internal readonly struct Instance(InstanceKind kind, int proxy, Transform3D toLocal, Basis normalToWorld, Aabb worldBounds)
    {
        public readonly InstanceKind Kind = kind;
        public readonly int Proxy = proxy;
        public readonly Transform3D ToLocal = toLocal;
        public readonly Basis NormalToWorld = normalToWorld;
        public readonly Aabb WorldBounds = worldBounds;
    }

    internal sealed class TreeProxy
    {
        public ProbeCapsules? Capsules;
        public ProbeTriangleMesh? Bark; // without a skeleton: the coarsest level's bark triangles
        public ProbeLeafGrid? Leaves;
        public Vector3 BarkAlbedo;
        public Vector3 LeafAlbedo;
        public float LeafTransmission;
        public Aabb LocalBounds;
    }

    internal sealed class TerrainProxy
    {
        public TerrainGrid Grid;
        public float[] Heights = [];
        public float[] ChunkMin = [];
        public float[] ChunkMax = [];
        public int ChunkQuads;
        public float MinHeight, MaxHeight;
        public Vector3 Origin;
        public Vector3[] Albedo = []; // per quad (row by row)
        public ProbeHeightfield? Field;

        public float HeightAt(float x, float z) => Grid.HeightAt(Heights, x - Origin.X, z - Origin.Z);

        public bool Contains(float x, float z) =>
            x >= Origin.X && z >= Origin.Z && x <= Origin.X + Grid.Size && z <= Origin.Z + Grid.Size;
    }

    private readonly TerrainProxy? _terrain;
    private readonly TreeProxy[] _trees;
    private readonly ProbeTriangleMesh[] _meshes;
    private readonly Instance[] _instances;
    private readonly ProbeCapsules _branches; // every tree's capsules in world space, one BVH
    private readonly float _cell;
    private readonly Vector2 _gridMin;
    private readonly int _gx, _gz;
    private readonly CellGrid _solids; // instances traced as solids per instance (meshes, trees without a skeleton)
    private readonly CellGrid _canopy; // trees with leaves

    // Instances bucketed into the XZ cells their bounds touch, with each cell's height range (a ray above or below skips it).
    private sealed class CellGrid(int[] start, int[] items, float[] minY, float[] maxY)
    {
        public readonly int[] Start = start;
        public readonly int[] Items = items;
        public readonly float[] MinY = minY;
        public readonly float[] MaxY = maxY;
    }

    internal ProbeBakeScene(TerrainProxy? terrain, TreeProxy[] trees, ProbeTriangleMesh[] meshes, Instance[] instances,
        Vector3 sunDirection, Vector3 sunRadiance, ShL2Rgb sky, byte[] contentHash)
    {
        _terrain = terrain;
        _trees = trees;
        _meshes = meshes;
        _instances = instances;
        SunDirection = sunDirection.LengthSquared() > 0f ? Vector3.Normalize(sunDirection) : Vector3.UnitY;
        SunRadiance = sunRadiance;
        Sky = sky;
        ContentHash = contentHash;

        var bounds = Aabb.Empty;
        if (terrain is not null)
        {
            // The heights it has, not the data's range: rays leave the scene as soon as they rise above everything.
            float lo = terrain.Field?.MinHeight ?? terrain.MinHeight, hi = terrain.Field?.MaxHeight ?? terrain.MaxHeight;
            bounds = bounds.Merge(new Aabb(terrain.Origin + new Vector3(0f, lo, 0f), terrain.Origin + new Vector3(terrain.Grid.Size, hi, terrain.Grid.Size)));
        }

        foreach (var instance in instances)
            bounds = bounds.Merge(instance.WorldBounds);
        if (bounds.Min.X > bounds.Max.X)
            bounds = new Aabb(Vector3.Zero, Vector3.Zero);
        Bounds = bounds;

        // The instance grids: square XZ cells of 8 m (fewer when the scene is huge).
        var size = new Vector2(bounds.Max.X - bounds.Min.X, bounds.Max.Z - bounds.Min.Z);
        _cell = MathF.Max(8f, MathF.Max(size.X, size.Y) / 256f);
        _gridMin = new Vector2(bounds.Min.X, bounds.Min.Z);
        _gx = Math.Max(1, (int)MathF.Ceiling(size.X / _cell));
        _gz = Math.Max(1, (int)MathF.Ceiling(size.Y / _cell));
        _solids = BuildGrid(i => instances[i].Kind == InstanceKind.Mesh || trees[instances[i].Proxy].Bark is not null);
        _canopy = BuildGrid(i => instances[i].Kind == InstanceKind.Tree && trees[instances[i].Proxy].Leaves is { IsEmpty: false });

        // Branches: every tree's capsules moved into world space under one BVH (tight boxes, no per-tree transforms).
        var capsules = new List<(Vector3, Vector3, float)>();
        var albedos = new List<Vector3>();
        foreach (var instance in instances)
        {
            if (instance.Kind != InstanceKind.Tree || trees[instance.Proxy].Capsules is not { Count: > 0 } local)
                continue;
            var toWorld = instance.ToLocal.AffineInverse();
            var scale = MathF.Cbrt(MathF.Abs(Determinant(toWorld.Basis)));
            for (var c = 0; c < local.Count; c++)
            {
                var (a, b, r) = local[c];
                capsules.Add((toWorld.TransformPoint(a), toWorld.TransformPoint(b), r * scale));
                albedos.Add(trees[instance.Proxy].BarkAlbedo);
            }
        }

        _branches = new ProbeCapsules(capsules, albedos);
    }

    private static float Determinant(Basis b) => Vector3.Dot(b.X, Vector3.Cross(b.Y, b.Z));

    private CellGrid BuildGrid(Func<int, bool> include)
    {
        var instances = _instances;
        var cells = _gx * _gz;
        var starts = new int[cells + 1];
        for (var i = 0; i < instances.Length; i++)
        {
            if (!include(i))
                continue;
            CellRange(instances[i].WorldBounds, out var x0, out var z0, out var x1, out var z1);
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                    starts[z * _gx + x + 1]++;
        }

        for (var c = 0; c < cells; c++)
            starts[c + 1] += starts[c];
        var items = new int[starts[cells]];
        var cursor = (int[])starts.Clone();
        for (var i = 0; i < instances.Length; i++)
        {
            if (!include(i))
                continue;
            CellRange(instances[i].WorldBounds, out var x0, out var z0, out var x1, out var z1);
            for (var z = z0; z <= z1; z++)
                for (var x = x0; x <= x1; x++)
                    items[cursor[z * _gx + x]++] = i;
        }

        var minY = new float[cells];
        var maxY = new float[cells];
        for (var c = 0; c < cells; c++)
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (var k = starts[c]; k < starts[c + 1]; k++)
            {
                lo = MathF.Min(lo, instances[items[k]].WorldBounds.Min.Y);
                hi = MathF.Max(hi, instances[items[k]].WorldBounds.Max.Y);
            }

            minY[c] = lo;
            maxY[c] = hi;
        }

        return new CellGrid(starts, items, minY, maxY);
    }

    // True when the ray's height between tEnter and tExit misses every instance in the cell.
    private static bool SkipsCell(CellGrid grid, int cell, Vector3 origin, Vector3 dir, float tEnter, float tExit)
    {
        var ya = origin.Y + dir.Y * tEnter;
        var yb = origin.Y + dir.Y * tExit;
        return MathF.Min(ya, yb) > grid.MaxY[cell] || MathF.Max(ya, yb) < grid.MinY[cell];
    }

    /// <summary>Unit direction towards the sun.</summary>
    public Vector3 SunDirection { get; }

    /// <summary>The sun's linear colour × intensity: what lights a white surface facing it (engine units).</summary>
    public Vector3 SunRadiance { get; }

    /// <summary>The sky's radiance in ambient units (× the world's ambient energy).</summary>
    public ShL2Rgb Sky { get; }

    /// <summary>World bounds of everything traceable.</summary>
    public Aabb Bounds { get; }

    /// <summary>SHA-256 of every proxy's data, the sun and the sky.</summary>
    public byte[] ContentHash { get; }

    public int InstanceCount => _instances.Length;

    public int TreeVariantCount => _trees.Length;

    public int MeshCount => _meshes.Length;

    /// <summary>A one-line summary for logs.</summary>
    public string Describe()
    {
        var triangles = 0L;
        foreach (var mesh in _meshes)
            triangles += mesh.TriangleCount;
        var capsules = 0;
        foreach (var tree in _trees)
            capsules += tree.Capsules?.Count ?? 0;
        return $"{(_terrain is null ? "no terrain" : $"terrain {_terrain.Grid.Quads}² quads")}, {_instances.Length} instances " +
               $"({_trees.Length} tree variants, {capsules} capsules; {_meshes.Length} meshes, {triangles} triangles), " +
               $"bounds {Bounds.Min} – {Bounds.Max}, grid {_gx}×{_gz} cells of {_cell} m ({_solids.Items.Length} solid, {_canopy.Items.Length} canopy entries), {_branches.Count} world capsules";
    }

    public bool HasTerrain => _terrain is not null;

    /// <summary>The terrain's height at (x, z) (null outside it or without a terrain).</summary>
    public float? GroundAt(float x, float z) =>
        _terrain is { } t && t.Contains(x, z) ? t.HeightAt(x, z) : null;

    private void CellRange(in Aabb b, out int x0, out int z0, out int x1, out int z1)
    {
        x0 = Math.Clamp((int)MathF.Floor((b.Min.X - _gridMin.X) / _cell), 0, _gx - 1);
        z0 = Math.Clamp((int)MathF.Floor((b.Min.Z - _gridMin.Y) / _cell), 0, _gz - 1);
        x1 = Math.Clamp((int)MathF.Floor((b.Max.X - _gridMin.X) / _cell), 0, _gx - 1);
        z1 = Math.Clamp((int)MathF.Floor((b.Max.Z - _gridMin.Y) / _cell), 0, _gz - 1);
    }

    /// <summary>Distance from <paramref name="origin"/> along <paramref name="dir"/> to where the ray leaves the scene (≥ 0).</summary>
    public float ExitDistance(Vector3 origin, Vector3 dir)
    {
        var inv = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        var a = (Bounds.Min - new Vector3(1f) - origin) * inv;
        var b = (Bounds.Max + new Vector3(1f) - origin) * inv;
        var tf = Vector3.Max(a, b);
        return MathF.Max(0f, MathF.Min(tf.X, MathF.Min(tf.Y, tf.Z)));
    }

    /// <summary>The closest solid surface on the ray before <paramref name="hit"/>.T (set it to the far limit first).</summary>
    public bool TraceSolid(Vector3 origin, Vector3 dir, ref ProbeHit hit, ProbeTraceContext ctx)
    {
        var found = false;
        if (_terrain is { } terrain && TraceTerrain(terrain, origin, dir, ref hit))
            found = true;
        if (_branches.Count > 0 && _branches.Intersect(origin, dir, ref hit))
            found = true;
        var grid = _solids;
        if (grid.Items.Length == 0)
            return found;

        var stamp = ctx.Next();
        var inv = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        var walk = new Walk(this, origin, dir, hit.T);
        while (walk.Next(out var cell, out var tEnter, out var tExit))
        {
            if (tEnter > hit.T)
                break;
            if (SkipsCell(grid, cell, origin, dir, tEnter, MathF.Min(tExit, hit.T)))
                continue;
            for (var k = grid.Start[cell]; k < grid.Start[cell + 1]; k++)
            {
                var i = grid.Items[k];
                if (ctx.Stamps[i] == stamp)
                    continue;
                ctx.Stamps[i] = stamp;
                ref readonly var instance = ref _instances[i];
                if (!HitsBox(instance.WorldBounds, origin, inv, hit.T))
                    continue;
                if (IntersectInstance(instance, origin, dir, ref hit))
                    found = true;
            }
        }

        return found;
    }

    /// <summary>Optical depth past which the canopy counts as opaque (transmittance below 10⁻⁴): marches stop there.</summary>
    public const float OpaqueDepth = 9.2f;

    /// <summary>
    /// The canopy's optical depth along the ray up to <paramref name="tEnd"/> (or until it reaches
    /// <paramref name="tauLimit"/>: the march stops there). When it first passes <paramref name="eventTau"/>, a leaf
    /// scatters the ray: <paramref name="eventT"/> gets the distance and <paramref name="eventTree"/> the tree variant (if
    /// <paramref name="eventT"/> is still negative).
    /// </summary>
    public float Canopy(Vector3 origin, Vector3 dir, float tEnd, float eventTau, ref float eventT, ref int eventTree, ProbeTraceContext ctx,
        float tauLimit = OpaqueDepth)
    {
        var tau = 0f;
        var grid = _canopy;
        if (grid.Items.Length == 0)
            return 0f;
        var stamp = ctx.Next();
        var inv = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        var walk = new Walk(this, origin, dir, tEnd);
        while (tau < tauLimit && walk.Next(out var cell, out var tEnter, out var tExit))
        {
            if (SkipsCell(grid, cell, origin, dir, tEnter, tExit))
                continue;
            for (var k = grid.Start[cell]; k < grid.Start[cell + 1] && tau < tauLimit; k++)
            {
                var i = grid.Items[k];
                if (ctx.Stamps[i] == stamp)
                    continue;
                ctx.Stamps[i] = stamp;
                ref readonly var instance = ref _instances[i];
                var leaves = _trees[instance.Proxy].Leaves!;
                if (!HitsBox(instance.WorldBounds, origin, inv, tEnd))
                    continue;
                var lo = instance.ToLocal.TransformPoint(origin);
                var ld = instance.ToLocal.Basis.Transform(dir);
                var k2 = ld.Length();
                if (!(k2 > 0f))
                    continue;
                ld /= k2;
                var before = eventT;
                var localEvent = -1f;
                leaves.March(lo, ld, 0f, tEnd * k2, ref tau, eventTau, ref localEvent, tauLimit);
                if (before < 0f && localEvent >= 0f)
                {
                    eventT = localEvent / k2;
                    eventTree = instance.Proxy;
                }
            }
        }

        return tau;
    }

    /// <summary>Light reaching <paramref name="point"/> from the sun's direction: 0 behind a solid, else the canopy's transmittance.</summary>
    public float SunTransmittance(Vector3 point, ProbeTraceContext ctx)
    {
        var dir = SunDirection;
        var far = ExitDistance(point, dir);
        var hit = new ProbeHit { T = far };
        if (TraceSolid(point, dir, ref hit, ctx))
            return 0f;
        var eventT = 0f; // ≥ 0: no event wanted
        var tree = -1;
        var tau = Canopy(point, dir, far, float.PositiveInfinity, ref eventT, ref tree, ctx);
        return MathF.Exp(-tau);
    }

    /// <summary>The leaf albedo and transmission of tree variant <paramref name="tree"/>.</summary>
    public (Vector3 Albedo, float Transmission) Leaf(int tree) => (_trees[tree].LeafAlbedo, _trees[tree].LeafTransmission);

    private bool IntersectInstance(in Instance instance, Vector3 origin, Vector3 dir, ref ProbeHit hit)
    {
        var lo = instance.ToLocal.TransformPoint(origin);
        var ld = instance.ToLocal.Basis.Transform(dir);
        var k = ld.Length();
        if (!(k > 0f))
            return false;
        ld /= k;
        var local = new ProbeHit { T = hit.T * k };
        bool found;
        if (instance.Kind == InstanceKind.Tree)
        {
            found = _trees[instance.Proxy].Bark is { } bark && bark.Intersect(lo, ld, ref local);
        }
        else
        {
            found = _meshes[instance.Proxy].Intersect(lo, ld, ref local);
        }

        if (!found)
            return false;
        var b = instance.NormalToWorld;
        var n = b.Transform(local.Normal);
        hit.T = local.T / k;
        hit.Normal = n.LengthSquared() > 0f ? Vector3.Normalize(n) : Vector3.UnitY;
        hit.Albedo = local.Albedo;
        hit.BackFace = local.BackFace;
        return true;
    }

    private static bool TraceTerrain(TerrainProxy terrain, Vector3 origin, Vector3 dir, ref ProbeHit hit)
    {
        var local = origin - terrain.Origin;
        if (!terrain.Field!.Raycast(local, dir, hit.T, out var distance, out var normal))
            return false;
        if (distance <= 1e-4f || distance >= hit.T)
            return false;
        var back = Vector3.Dot(normal, dir) > 0f;
        hit.T = distance;
        hit.Normal = back ? -normal : normal;
        hit.BackFace = back;
        var p = local + dir * distance;
        var quads = terrain.Grid.Quads;
        var i = Math.Clamp((int)(p.X / terrain.Grid.Spacing), 0, quads - 1);
        var j = Math.Clamp((int)(p.Z / terrain.Grid.Spacing), 0, quads - 1);
        hit.Albedo = terrain.Albedo.Length > 0 ? terrain.Albedo[j * quads + i] : new Vector3(0.2f);
        return true;
    }

    private static bool HitsBox(in Aabb box, Vector3 origin, Vector3 inv, float tMax)
    {
        var a = (box.Min - origin) * inv;
        var b = (box.Max - origin) * inv;
        var tn = Vector3.Min(a, b);
        var tf = Vector3.Max(a, b);
        var enter = MathF.Max(MathF.Max(tn.X, tn.Y), MathF.Max(tn.Z, 0f));
        var exit = MathF.Min(MathF.Min(tf.X, tf.Y), MathF.Min(tf.Z, tMax));
        return enter <= exit;
    }

    /// <summary>A 2D DDA over the instance grid's cells along a ray (XZ), in order of distance.</summary>
    private struct Walk
    {
        private int _x, _z;
        private readonly int _stepX, _stepZ, _gx, _gz;
        private float _tMaxX, _tMaxZ, _t;
        private readonly float _dX, _dZ, _tEnd;
        private bool _done;

        public Walk(ProbeBakeScene scene, Vector3 origin, Vector3 dir, float tEnd)
        {
            _gx = scene._gx;
            _gz = scene._gz;
            var cell = scene._cell;
            var min = scene._gridMin;
            var maxX = min.X + _gx * cell;
            var maxZ = min.Y + _gz * cell;
            // Clip to the grid's XZ box.
            float t0 = 0f, t1 = tEnd;
            _done = !Clip(origin.X, dir.X, min.X, maxX, ref t0, ref t1) || !Clip(origin.Z, dir.Z, min.Y, maxZ, ref t0, ref t1);
            _t = t0;
            _tEnd = t1;
            var px = (origin.X + dir.X * t0 - min.X) / cell;
            var pz = (origin.Z + dir.Z * t0 - min.Y) / cell;
            _x = Math.Clamp((int)MathF.Floor(px), 0, _gx - 1);
            _z = Math.Clamp((int)MathF.Floor(pz), 0, _gz - 1);
            _stepX = dir.X > 0f ? 1 : dir.X < 0f ? -1 : 0;
            _stepZ = dir.Z > 0f ? 1 : dir.Z < 0f ? -1 : 0;
            _dX = _stepX != 0 ? cell / MathF.Abs(dir.X) : float.PositiveInfinity;
            _dZ = _stepZ != 0 ? cell / MathF.Abs(dir.Z) : float.PositiveInfinity;
            _tMaxX = _stepX != 0 ? (min.X + (_x + (_stepX > 0 ? 1 : 0)) * cell - origin.X) / dir.X : float.PositiveInfinity;
            _tMaxZ = _stepZ != 0 ? (min.Y + (_z + (_stepZ > 0 ? 1 : 0)) * cell - origin.Z) / dir.Z : float.PositiveInfinity;
        }

        public bool Next(out int cell, out float tEnter, out float tExit)
        {
            cell = _z * _gx + _x;
            tEnter = _t;
            tExit = _t;
            if (_done || _t > _tEnd)
                return false;
            if (_tMaxX < _tMaxZ)
            {
                _t = _tMaxX;
                _tMaxX += _dX;
                _x += _stepX;
                if ((uint)_x >= (uint)_gx)
                    _done = true;
            }
            else
            {
                _t = _tMaxZ;
                _tMaxZ += _dZ;
                _z += _stepZ;
                if ((uint)_z >= (uint)_gz)
                    _done = true;
            }

            if (_stepX == 0 && _stepZ == 0)
                _done = true; // vertical: one cell
            tExit = MathF.Min(_t, _tEnd);
            return true;
        }

        private static bool Clip(float origin, float dir, float min, float max, ref float t0, ref float t1)
        {
            if (MathF.Abs(dir) < 1e-12f)
                return origin >= min && origin <= max;
            var a = (min - origin) / dir;
            var b = (max - origin) / dir;
            if (a > b)
                (a, b) = (b, a);
            t0 = MathF.Max(t0, a);
            t1 = MathF.Min(t1, b);
            return t0 <= t1;
        }
    }
}

/// <summary>
/// Collects a <see cref="ProbeBakeScene"/>: from a node tree (<see cref="AddTree(Node)"/>: terrains, tree scatters,
/// trees, static meshes, the first directional light and the world environment's sky), or by hand (tests). Proxies are
/// shared per tree mesh and per mesh-and-material.
/// </summary>
/// <remarks>
/// The scene's <see cref="ProbeBakeScene.ContentHash"/> hashes the <b>inputs</b> (heights and splat weights, mesh
/// vertices and indices, transforms, material colours and texture paths, the sun, the sky), not the proxies, so a
/// <see cref="ProbeBakeSceneBuilder(bool)"/> with <c>fingerprintOnly</c> computes it (<see cref="Fingerprint"/>) in a
/// fraction of the time a full build takes (no BVHs, leaf grids, skeletons or texture decodes): what a level checks at
/// load to know whether its committed bake is current. Code-made textures without a resource path are hashed by size only.
/// </remarks>
internal sealed class ProbeBakeSceneBuilder(bool fingerprintOnly = false)
{
    /// <summary>Bumped when the proxies or the bake change meaning, so older bakes read as stale.</summary>
    public const int Version = 2;

    /// <summary>The deepest branch level traced as capsules (0 = the trunk).</summary>
    public const int MaxCapsuleLevel = 1;

    /// <summary>Skeleton sections spanned by one capsule.</summary>
    public const int SectionsPerCapsule = 2;

    /// <summary>Thinner branch pieces are left to the leaf density (m).</summary>
    public const float MinCapsuleRadius = 0.03f;

    /// <summary>Leaf-density cell size (m).</summary>
    public const float LeafCellSize = 0.5f;

    /// <summary>
    /// Light a leaf passes through to its other side, × its albedo, for leaves whose material is not a
    /// <see cref="FoliageMaterial3D"/>; foliage passes its <see cref="FoliageMaterial3D.Translucency"/>, as the foliage shader does.
    /// </summary>
    public const float LeafTransmission = 0.15f;

    /// <summary>Albedo of water surfaces.</summary>
    public const float WaterAlbedo = 0.05f;

    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly List<ProbeBakeScene.TreeProxy> _trees = [];
    private readonly Dictionary<TreeMesh, int> _treeIndex = new(ReferenceEqualityComparer.Instance);
    private readonly List<ProbeTriangleMesh> _meshes = [];
    private readonly Dictionary<(Mesh, Material?), int> _meshIndex = new();
    private readonly List<ProbeBakeScene.Instance> _instances = [];
    private readonly Dictionary<Texture2D, Vector4> _textureMeans = new(ReferenceEqualityComparer.Instance);
    private readonly TreeGenerator _generator = new();
    private ProbeBakeScene.TerrainProxy? _terrain;
    private bool _hasTerrain;

    /// <summary>Unit direction towards the sun (default straight up).</summary>
    public Vector3 SunDirection { get; set; } = Vector3.UnitY;

    /// <summary>The sun's linear colour × intensity (default none).</summary>
    public Vector3 SunRadiance { get; set; }

    /// <summary>The sky's radiance in ambient units (default black).</summary>
    public ShL2Rgb Sky { get; set; } = ShL2Rgb.Uniform(Vector3.Zero);

    /// <summary>Only hashing: <see cref="Fingerprint"/>, no <see cref="Build"/>.</summary>
    public bool FingerprintOnly { get; } = fingerprintOnly;

    /// <summary>Adds everything under <paramref name="root"/> the bake traces, plus the first directional light and environment of its world.</summary>
    public void AddTree(Node root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var lightFound = false;
        WorldEnvironment? environment = null;
        Visit(root, ref lightFound, ref environment);
        if (environment is not null)
            Sky = SkyFrom(environment, SunDirection, SunRadiance);
    }

    private void Visit(Node node, ref bool lightFound, ref WorldEnvironment? environment)
    {
        switch (node)
        {
            case WorldEnvironment env:
                environment ??= env;
                break;
            case DirectionalLight3D sunNode when !lightFound && sunNode.IsVisibleInTree():
                // The direction from the node's transform (the light object follows it only after the next process).
                var forward = sunNode.GlobalForward;
                if (sunNode.Light is { } light && light.Intensity > 0f && forward.LengthSquared() > 1e-12f)
                {
                    lightFound = true;
                    SunDirection = -Vector3.Normalize(forward);
                    SunRadiance = light.LinearColor * light.Intensity;
                }

                break;
            case Terrain3D terrain:
                if (!_hasTerrain && terrain.IsVisibleInTree() && terrain.Data is not null)
                    AddTerrain(terrain);
                return; // its chunks, water and foliage are not traced
            case TreeScatter scatter:
                if (scatter.IsVisibleInTree())
                    AddScatter(scatter);
                return;
            case Tree3D tree:
                if (tree.IsVisibleInTree() && tree.Mesh is { } treeMesh)
                    AddTreeInstance(treeMesh, tree.BarkMaterial, tree.LeafMaterial, tree.GlobalTransform);
                return;
            case River3D or TerrainFoliage3D or LightProbeVolume:
                return;
            case MeshInstance3D { GIMode: GIMode.Static, Mesh: { } mesh } instance when instance.IsVisibleInTree():
                AddMesh(mesh, instance.MaterialOverride, instance.GlobalTransform);
                break;
            case MultiMeshInstance3D { GIMode: GIMode.Static, Multimesh: { Mesh: { } mm } multimesh } multi when multi.IsVisibleInTree():
                var global = multi.GlobalTransform;
                for (var i = 0; i < multimesh.DrawnInstanceCount; i++)
                    AddMesh(mm, multi.MaterialOverride, global * multimesh.GetInstanceTransform(i));
                break;
        }

        foreach (var child in node.Children)
            Visit(child, ref lightFound, ref environment);
    }

    /// <summary>Adds a terrain's height field (its bed heights, the water layer darkening the cells under water).</summary>
    public void AddTerrain(Terrain3D terrain)
    {
        var data = terrain.Data!;
        data.EnsureLoaded();
        _hasTerrain = true;
        var grid = data.Grid;
        var quads = grid.Quads;
        var origin = terrain.GlobalPosition;
        var heights = data.BedHeights;

        // Splat weights per cell (Realistic: cells are quads; two RGBA images of four layers each) and water per vertex.
        var realistic = data.Profile == TerrainProfile.Realistic && data.CellsPerSide == quads;
        var cells = new Rect2I(0, 0, quads, quads);
        var splat0 = realistic ? new uint[quads * quads] : [];
        var splat1 = realistic ? new uint[quads * quads] : [];
        if (realistic)
        {
            data.GetCells(TerrainLayers.Surface, 0, cells, splat0);
            data.GetCells(TerrainLayers.Surface, 1, cells, splat1);
        }

        var stride = grid.Stride;
        var water = new uint[stride * stride];
        data.GetCells(TerrainLayers.Water, 0, new Rect2I(0, 0, stride, stride), water);

        HashVector(origin);
        HashFloats(heights);
        HashUInts(splat0);
        HashUInts(splat1);
        HashUInts(water);
        HashMaterial(terrain.Material);
        if (FingerprintOnly)
            return;

        // Albedo per quad: the splat weights × each layer's mean colour (Realistic + TerrainSplatMaterial3D), else the
        // material's; water (deeper than 5 cm at the quad's centre) is dark.
        var albedo = new Vector3[quads * quads];
        var layers = LayerAlbedos(terrain.Material);
        var flat = MaterialAlbedo(terrain.Material).Color;
        var waterDepth = data.MaxWaterDepth / 255f;
        for (var j = 0; j < quads; j++)
        {
            for (var i = 0; i < quads; i++)
            {
                var c = j * quads + i;
                var colour = flat;
                if (realistic && layers.Length > 0)
                {
                    var sum = Vector3.Zero;
                    var total = 0f;
                    for (var l = 0; l < Math.Min(layers.Length, 8); l++)
                    {
                        var packed = l < 4 ? splat0[c] : splat1[c];
                        var w = (packed >> (8 * (l & 3))) & 0xFF;
                        sum += layers[l] * w;
                        total += w;
                    }

                    if (total > 0f)
                        colour = sum / total;
                }

                var k = j * stride + i;
                var depth = ((water[k] & 0xFF) + (water[k + 1] & 0xFF) + (water[k + stride] & 0xFF) + (water[k + stride + 1] & 0xFF)) * 0.25f * waterDepth;
                if (depth > 0.05f)
                    colour = new Vector3(WaterAlbedo);
                albedo[c] = colour;
            }
        }

        var proxy = new ProbeBakeScene.TerrainProxy
        {
            Grid = grid,
            Heights = heights.ToArray(),
            ChunkMin = data.ChunkMin.ToArray(),
            ChunkMax = data.ChunkMax.ToArray(),
            ChunkQuads = data.ChunkQuads,
            MinHeight = data.HeightMin - data.MaxWaterDepth,
            MaxHeight = data.HeightMax,
            Origin = origin,
            Albedo = albedo,
        };
        proxy.Field = new ProbeHeightfield(grid, proxy.Heights);
        _terrain = proxy;
    }

    /// <summary>Adds every placement of a tree scatter.</summary>
    public void AddScatter(TreeScatter scatter)
    {
        var global = scatter.GlobalTransform;
        var variants = new TreeMesh?[scatter.Species.Length][];
        for (var i = 0; i < scatter.PlacementCount; i++)
        {
            var p = scatter.GetPlacement(i);
            if ((uint)p.Species >= (uint)scatter.Species.Length || scatter.Species[p.Species] is not { } species)
                continue;
            variants[p.Species] ??= new TreeMesh?[species.VariantCount];
            var v = TreeScatter.VariantOf(p.Position, variants[p.Species].Length);
            var mesh = variants[p.Species][v] ??= species.GetVariant(v, _generator);
            if (mesh is null)
                continue;
            var local = Transform3D.FromTrs(p.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, p.Yaw), new Vector3(p.Scale));
            AddTreeInstance(mesh, species.ResolveBarkMaterial(mesh), species.ResolveLeafMaterial(mesh), global * local);
        }
    }

    /// <summary>Adds one tree: its variant's proxy (built once per <see cref="TreeMesh"/>) at <paramref name="transform"/>.</summary>
    public void AddTreeInstance(TreeMesh mesh, Material? bark, Material? leaves, Transform3D transform)
    {
        if (!_treeIndex.TryGetValue(mesh, out var index))
        {
            index = _treeIndex.Count;
            _treeIndex.Add(mesh, index);
            // The tree's inputs: every level's vertices (they follow from its options, seed and generator), its materials.
            foreach (var lod in mesh.Lods)
                for (var s = 0; s < lod.SurfaceCount; s++)
                    HashVectors(lod.GetSurface(s).Positions);
            HashMaterial(bark);
            HashMaterial(leaves);
            if (!FingerprintOnly)
                _trees.Add(BuildTree(mesh, bark, leaves));
        }

        AddInstance(ProbeBakeScene.InstanceKind.Tree, index, FingerprintOnly ? default : _trees[index].LocalBounds, transform);
    }

    /// <summary>Adds a static mesh (its surfaces' triangles; albedo from <paramref name="materialOverride"/> or each surface's material).</summary>
    public void AddMesh(Mesh mesh, Material? materialOverride, Transform3D transform)
    {
        if (!_meshIndex.TryGetValue((mesh, materialOverride), out var index))
        {
            index = _meshIndex.Count;
            _meshIndex.Add((mesh, materialOverride), index);
            var positions = new List<Vector3>();
            var albedos = new List<Vector3>();
            for (var s = 0; s < mesh.SurfaceCount; s++)
            {
                var surface = mesh.GetSurface(s);
                var material = materialOverride ?? mesh.GetSurfaceMaterial(s);
                HashVectors(surface.Positions);
                HashInts(surface.Indices);
                HashMaterial(material);
                if (FingerprintOnly || IsTransparent(material))
                    continue;
                AppendTriangles(surface.Positions, surface.Indices, MaterialAlbedo(material).Color, positions, albedos);
            }

            if (!FingerprintOnly)
                _meshes.Add(new ProbeTriangleMesh(positions, albedos));
        }

        if (FingerprintOnly)
            AddInstance(ProbeBakeScene.InstanceKind.Mesh, index, default, transform);
        else if (_meshes[index].TriangleCount > 0)
            AddInstance(ProbeBakeScene.InstanceKind.Mesh, index, _meshes[index].Bounds, transform);
    }

    /// <summary>Adds triangles (world space) with one albedo: tests and code-made occluders.</summary>
    public void AddTriangles(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, Vector3 albedo)
    {
        HashVectors(positions);
        HashInts(indices);
        HashVector(albedo);
        var index = _meshIndex.Count + _meshes.Count; // unique, not shared
        if (FingerprintOnly)
        {
            AddInstance(ProbeBakeScene.InstanceKind.Mesh, index, default, Transform3D.Identity);
            return;
        }

        var p = new List<Vector3>();
        var a = new List<Vector3>();
        AppendTriangles(positions, indices, albedo, p, a);
        var proxy = new ProbeTriangleMesh(p, a);
        _meshes.Add(proxy);
        AddInstance(ProbeBakeScene.InstanceKind.Mesh, _meshes.Count - 1, proxy.Bounds, Transform3D.Identity);
    }

    /// <summary>The hash of everything added plus the sun and the sky (the scene's <see cref="ProbeBakeScene.ContentHash"/>).</summary>
    public byte[] Fingerprint()
    {
        HashVector(SunDirection);
        HashVector(SunRadiance);
        foreach (var c in Sky.Coefficients)
            HashVector(c);
        Span<int> version = [Version];
        _hash.AppendData(MemoryMarshal.AsBytes(version));
        return _hash.GetHashAndReset();
    }

    /// <summary>The scene, with <see cref="Fingerprint"/> as its content hash.</summary>
    public ProbeBakeScene Build()
    {
        if (FingerprintOnly)
            throw new InvalidOperationException("A fingerprint-only builder has no proxies to build.");
        var hash = Fingerprint();
        return new ProbeBakeScene(_terrain, [.. _trees], [.. _meshes], [.. _instances], SunDirection, SunRadiance, Sky, hash);
    }

    // ── Proxies ──────────────────────────────────────────────────────────────────────────────────────────────────

    private void AddInstance(ProbeBakeScene.InstanceKind kind, int proxy, Aabb localBounds, Transform3D transform)
    {
        Span<float> data = [(float)kind, proxy, transform.Origin.X, transform.Origin.Y, transform.Origin.Z,
            transform.Basis.X.X, transform.Basis.X.Y, transform.Basis.X.Z, transform.Basis.Y.X, transform.Basis.Y.Y, transform.Basis.Y.Z,
            transform.Basis.Z.X, transform.Basis.Z.Y, transform.Basis.Z.Z];
        _hash.AppendData(MemoryMarshal.AsBytes(data));
        if (FingerprintOnly)
            return;

        Transform3D inverse;
        try
        {
            inverse = transform.AffineInverse();
        }
        catch (InvalidOperationException)
        {
            return; // zero scale
        }

        var normalToWorld = Transpose(inverse.Basis);
        var world = Aabb.Empty;
        for (var c = 0; c < 8; c++)
        {
            var corner = new Vector3((c & 1) != 0 ? localBounds.Max.X : localBounds.Min.X,
                (c & 2) != 0 ? localBounds.Max.Y : localBounds.Min.Y,
                (c & 4) != 0 ? localBounds.Max.Z : localBounds.Min.Z);
            world = world.Encapsulate(transform.TransformPoint(corner));
        }

        _instances.Add(new ProbeBakeScene.Instance(kind, proxy, inverse, normalToWorld, world));
    }

    private static Basis Transpose(Basis b) =>
        new(new Vector3(b.X.X, b.Y.X, b.Z.X), new Vector3(b.X.Y, b.Y.Y, b.Z.Y), new Vector3(b.X.Z, b.Y.Z, b.Z.Z));

    private ProbeBakeScene.TreeProxy BuildTree(TreeMesh mesh, Material? bark, Material? leaves)
    {
        var proxy = new ProbeBakeScene.TreeProxy
        {
            BarkAlbedo = MaterialAlbedo(bark).Color,
            LeafTransmission = leaves is FoliageMaterial3D foliage ? Math.Clamp(foliage.Translucency, 0f, 1f) : LeafTransmission,
        };
        var leafMaterial = MaterialAlbedo(leaves);
        proxy.LeafAlbedo = leafMaterial.Color;
        var bounds = Aabb.Empty;

        // Branches: capsules from the Ez Tree skeleton: the trunk and the first branch level, two sections per capsule
        // (thinner branches and twigs are in the leaves' density; they occlude like foliage at probe scale).
        var options = mesh.Options;
        if (options is not null)
        {
            var parameters = options.ToParams();
            parameters.Seed = mesh.Seed;
            var skeleton = _generator.GrowSkeleton(parameters, mesh.Seed);
            var scale = (float)parameters.Scale;
            var capsules = new List<(Vector3, Vector3, float)>();
            foreach (var branch in skeleton.Branches)
            {
                if (branch.Level > MaxCapsuleLevel)
                    continue;
                var sections = skeleton.SectionsOf(branch);
                for (var s = 0; s + 1 < sections.Length; s += SectionsPerCapsule)
                {
                    var e = Math.Min(s + SectionsPerCapsule, sections.Length - 1);
                    var a = ToVector(sections[s].Origin) * scale;
                    var b = ToVector(sections[e].Origin) * scale;
                    var r = (float)(0.5 * (sections[s].Radius + sections[e].Radius)) * scale;
                    if (r >= MinCapsuleRadius)
                        capsules.Add((a, b, r));
                }
            }

            proxy.Capsules = new ProbeCapsules(capsules);
            if (proxy.Capsules.Count > 0)
                bounds = bounds.Merge(proxy.Capsules.Bounds);
        }

        // Without a skeleton (a hand-made bake): the coarsest level's bark triangles.
        var lods = mesh.Lods;
        if (proxy.Capsules is not { Count: > 0 } && lods.Length > 0 && lods[^1].SurfaceCount > 0)
        {
            var surface = lods[^1].GetSurface(0);
            var positions = new List<Vector3>();
            var albedos = new List<Vector3>();
            AppendTriangles(surface.Positions, surface.Indices, proxy.BarkAlbedo, positions, albedos);
            proxy.Bark = new ProbeTriangleMesh(positions, albedos);
            if (proxy.Bark.TriangleCount > 0)
                bounds = bounds.Merge(proxy.Bark.Bounds);
        }

        // Leaves: the finest level's leaf cards, as area density (× the texture's alpha coverage).
        if (lods.Length > 0 && lods[0].SurfaceCount > 1)
        {
            var surface = lods[0].GetSurface(1);
            proxy.Leaves = new ProbeLeafGrid(surface.Positions, surface.Indices, leafMaterial.Coverage, LeafCellSize);
            if (!proxy.Leaves.IsEmpty)
                bounds = bounds.Merge(proxy.Leaves.Bounds);
        }

        proxy.LocalBounds = bounds.Min.X <= bounds.Max.X ? bounds : new Aabb(Vector3.Zero, Vector3.Zero);
        return proxy;
    }

    private static Vector3 ToVector(Vec3d v) => new((float)v.X, (float)v.Y, (float)v.Z);

    private static void AppendTriangles(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, Vector3 albedo, List<Vector3> into, List<Vector3> albedos)
    {
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            into.Add(positions[indices[t]]);
            into.Add(positions[indices[t + 1]]);
            into.Add(positions[indices[t + 2]]);
            albedos.Add(albedo);
        }
    }

    // ── Input hashing ────────────────────────────────────────────────────────────────────────────────────────────

    // What a material contributes to the bake: its kind, colours, transparency and textures (by path and size).
    private void HashMaterial(Material? material)
    {
        HashString(material?.GetType().Name ?? "none");
        switch (material)
        {
            case StandardMaterial3D standard:
                HashInts([standard.AlbedoColor.ToArgb(), (int)standard.Transparency]);
                HashTexture(standard.AlbedoTexture);
                break;
            case FoliageMaterial3D foliage:
                HashInts([foliage.AlbedoColor.ToArgb()]);
                HashFloats([foliage.Translucency]);
                HashTexture(foliage.AlbedoTexture);
                break;
            case TerrainSplatMaterial3D splat:
                for (var i = 0; i < splat.LayerCount; i++)
                {
                    var layer = splat.Layers[i];
                    HashInts([layer?.Tint.ToArgb() ?? 0]);
                    HashTexture(layer?.Albedo);
                }

                break;
        }
    }

    private void HashTexture(Texture2D? texture)
    {
        HashString(texture?.ResourcePath ?? string.Empty);
        HashInts([texture?.Width ?? 0, texture?.Height ?? 0]);
    }

    private void HashString(string value) => _hash.AppendData(System.Text.Encoding.UTF8.GetBytes(value + "\0"));

    private void HashFloats(ReadOnlySpan<float> values) => _hash.AppendData(MemoryMarshal.AsBytes(values));

    private void HashInts(ReadOnlySpan<int> values) => _hash.AppendData(MemoryMarshal.AsBytes(values));

    private void HashUInts(ReadOnlySpan<uint> values) => _hash.AppendData(MemoryMarshal.AsBytes(values));

    private void HashVectors(ReadOnlySpan<Vector3> values) => _hash.AppendData(MemoryMarshal.AsBytes(values));

    private void HashVector(Vector3 value)
    {
        Span<Vector3> one = [value];
        _hash.AppendData(MemoryMarshal.AsBytes(one));
    }

    // ── Albedo ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static bool IsTransparent(Material? material) => material switch
    {
        StandardMaterial3D standard => standard.Transparency == AlphaMode.Blend,
        _ => false,
    };

    /// <summary>A material's mean linear albedo (colour × texture mean) and alpha coverage; mid grey when unknown.</summary>
    internal (Vector3 Color, float Coverage) MaterialAlbedo(Material? material)
    {
        switch (material)
        {
            case StandardMaterial3D standard:
                {
                    var tint = Linear(standard.AlbedoColor);
                    var mean = standard.AlbedoTexture is { } texture ? TextureMean(texture) : Vector4.One;
                    return (tint * new Vector3(mean.X, mean.Y, mean.Z), Math.Clamp(mean.W * standard.AlbedoColor.A / 255f, 0.05f, 1f));
                }
            case FoliageMaterial3D foliage:
                {
                    var tint = Linear(foliage.AlbedoColor);
                    var mean = foliage.AlbedoTexture is { } texture ? TextureMean(texture) : Vector4.One;
                    return (tint * new Vector3(mean.X, mean.Y, mean.Z), Math.Clamp(mean.W, 0.05f, 1f));
                }
            case WaterMaterial3D:
                return (new Vector3(WaterAlbedo), 1f);
            case TerrainSplatMaterial3D splat:
                {
                    var layers = LayerAlbedos(splat);
                    var sum = Vector3.Zero;
                    foreach (var l in layers)
                        sum += l;
                    return (layers.Length > 0 ? sum / layers.Length : new Vector3(0.25f), 1f);
                }
            default:
                return (new Vector3(0.25f), 1f);
        }
    }

    private Vector3[] LayerAlbedos(Material? material)
    {
        if (material is not TerrainSplatMaterial3D splat)
            return [];
        var count = splat.LayerCount;
        var result = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var layer = splat.Layers[i];
            var tint = layer is null ? Vector3.One : Linear(layer.Tint);
            var mean = layer?.Albedo is { } texture ? TextureMean(texture) : new Vector4(0.25f);
            result[i] = tint * new Vector3(mean.X, mean.Y, mean.Z);
        }

        return result;
    }

    private static Vector3 Linear(System.Drawing.Color c) => ColorSpace.SrgbToLinear(new Vector3(c.R, c.G, c.B) / 255f);

    // The mean linear colour (sRGB decoded) and mean alpha of a texture's pixels; mid grey if it cannot be decoded.
    private Vector4 TextureMean(Texture2D texture)
    {
        if (_textureMeans.TryGetValue(texture, out var cached))
            return cached;
        var mean = new Vector4(0.5f, 0.5f, 0.5f, 1f);
        try
        {
            var (rgba, width, height) = texture.DecodePixels();
            if (width > 0 && height > 0 && rgba.Length >= width * height * 4)
            {
                Span<float> lut = stackalloc float[256];
                for (var i = 0; i < 256; i++)
                    lut[i] = ColorSpace.SrgbToLinear(i / 255f);
                double r = 0, g = 0, b = 0, a = 0, w = 0;
                var step = Math.Max(1, width * height / 65536);
                for (var p = 0; p < width * height; p += step)
                {
                    var alpha = rgba[p * 4 + 3] / 255.0;
                    r += lut[rgba[p * 4]] * alpha;
                    g += lut[rgba[p * 4 + 1]] * alpha;
                    b += lut[rgba[p * 4 + 2]] * alpha;
                    a += alpha;
                    w++;
                }

                mean = a > 0 ? new Vector4((float)(r / a), (float)(g / a), (float)(b / a), (float)(a / w)) : new Vector4(0f, 0f, 0f, 0f);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or InvalidOperationException or NotSupportedException)
        {
            // keep the grey
        }

        _textureMeans[texture] = mean;
        return mean;
    }

    // ── The sky ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The world's sky light in ambient units, as the lit shaders see it (<c>iblDiffuse</c>): the sky's radiance ×
    /// <see cref="WorldEnvironment.AmbientEnergy"/> when <see cref="AmbientSource.Sky"/> (the physical sky from the CPU
    /// atmosphere model's single scattering, the procedural one from its gradient), else the ambient colour.
    /// </summary>
    internal static ShL2Rgb SkyFrom(WorldEnvironment environment, Vector3 sunDirection, Vector3 sunRadiance)
    {
        var energy = environment.AmbientEnergy;
        if (environment.AmbientSource != AmbientSource.Sky || environment.Sky is not { } sky)
            return ShL2Rgb.Uniform(ColorSpace.SrgbToLinear(environment.AmbientColor) * energy);
        switch (sky.Mode)
        {
            case SkyEnvironmentType.Physical:
                {
                    var settings = sky.PhysicalSettings;
                    var atmosphere = settings.ToAtmosphere();
                    var illuminance = sunRadiance * settings.EnergyMultiplier;
                    var altitude = AtmosphereModel.ViewRadius(atmosphere, settings.AltitudeMeters) - atmosphere.BottomRadius;
                    var ground = Vector3.Clamp(ColorSpace.SrgbToLinear(settings.GroundColor), Vector3.Zero, Vector3.One);
                    var transmittance = AtmosphereModel.SunTransmittance(atmosphere, settings.AltitudeMeters, sunDirection);
                    return ShL2Rgb.Project(d =>
                    {
                        if (d.Y < 0f)
                            return ground * illuminance * transmittance * MathF.Max(sunDirection.Y, 0f) * energy;
                        var s = AtmosphereModel.SingleScattering(atmosphere, altitude, d, sunDirection, steps: 24);
                        // Single scattering misses the multiple scattering the GPU sky adds (≈ a third more in a clear sky).
                        return s * (AtmosphereModel.RadianceScale * 1.35f) * illuminance * energy;
                    }, samples: 256);
                }
            case SkyEnvironmentType.Procedural:
                {
                    var top = ColorSpace.SrgbToLinear(sky.SkyColor);
                    var horizon = ColorSpace.SrgbToLinear(sky.HorizonColor);
                    var bottom = ColorSpace.SrgbToLinear(sky.GroundColor);
                    var sharpness = sky.HorizonSharpness;
                    return ShL2Rgb.Project(d => (d.Y >= 0f
                        ? Vector3.Lerp(horizon, top, Math.Clamp(d.Y * sharpness, 0f, 1f))
                        : Vector3.Lerp(horizon, bottom, Math.Clamp(-d.Y * sharpness, 0f, 1f))) * energy, samples: 512);
                }
            default:
                return ShL2Rgb.Uniform(ColorSpace.SrgbToLinear(environment.AmbientColor) * energy);
        }
    }

}
