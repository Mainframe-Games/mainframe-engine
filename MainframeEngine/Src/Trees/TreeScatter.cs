using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>Where one tree of a <see cref="TreeScatter"/> stands (scatter-local).</summary>
/// <param name="Position">The trunk base.</param>
/// <param name="Yaw">Rotation about +Y, radians.</param>
/// <param name="Scale">Uniform scale (1 = the generated size).</param>
/// <param name="Species">Index into <see cref="TreeScatter.Species"/>.</param>
public readonly record struct TreePlacement(Vector3 Position, float Yaw, float Scale, int Species);

/// <summary>
/// Many trees (G8b scatter, ADR 0158): placements of several <see cref="TreeSpecies"/> drawn as
/// <see cref="MultiMesh"/> batches. The placements are bucketed into square chunks of <see cref="ChunkSize"/> (scatter-local
/// XZ); each chunk gets, per species variant and per level of detail, one internal <see cref="TreeScatterBatch3D"/> whose
/// visibility range is the level's, so a chunk draws one level, chosen by the camera's distance to the chunk's bounds
/// centre (the shadow passes use the same choice). Each placement's variant is a hash of its position.
/// </summary>
/// <remarks>
/// <para>Trunk collision (<see cref="Collision"/>): one internal <see cref="StaticBody3D"/> per chunk holding a capsule per
/// tree (each variant's <see cref="TreeMesh.Trunk"/>, scaled). Static shapes cost the physics step nothing while
/// nothing moves near them, so every tree gets one.</para>
/// <para>Building (on ready, after a change, or <see cref="Rebuild"/>) generates each variant once (a few ms each) and
/// uploads the batches; nothing runs per frame afterwards. The placements are saved with the scene.</para>
/// </remarks>
[Tool]
[EditorIcon("stack-2")]
public sealed class TreeScatter : Node3D
{
    /// <summary>Floats per placement in <see cref="PlacementData"/>: x, y, z, yaw, scale, species.</summary>
    public const int PlacementStride = 6;

    private readonly List<TreeScatterBatch3D> _batches = [];
    private readonly List<StaticBody3D> _bodies = [];
    private int _chunkCount;
    private bool _dirty = true;

    /// <summary>The kinds of trees placements refer to by index.</summary>
    [Export]
    public TreeSpecies[] Species
    {
        get;
        set
        {
            field = value ?? [];
            MarkDirty();
        }
    } = [];

    /// <summary>The placements, <see cref="PlacementStride"/> floats each (see <see cref="SetPlacements"/>).</summary>
    [Export]
    internal float[] PlacementData
    {
        get;
        set
        {
            field = value ?? [];
            MarkDirty();
        }
    } = [];

    /// <summary>Side of the square chunks (m) the placements are bucketed into: smaller chunks switch levels closer to each tree, larger ones draw less often.</summary>
    [Export(Range = "4,1024,1")]
    public float ChunkSize
    {
        get;
        set
        {
            field = value;
            MarkDirty();
        }
    } = 32f;

    /// <summary>Distance (m, camera to a chunk's bounds centre) from which level 1 replaces level 0.</summary>
    [ExportGroup("Levels of detail")]
    [Export(Range = "0,1000,0.5")]
    public float Lod1Distance
    {
        get;
        set
        {
            field = value;
            ApplyRanges();
        }
    } = 30f;

    /// <summary>Distance from which level 2 replaces level 1.</summary>
    [Export(Range = "0,1000,0.5")]
    public float Lod2Distance
    {
        get;
        set
        {
            field = value;
            ApplyRanges();
        }
    } = 75f;

    /// <summary>Distance from which a chunk is not drawn; 0 = always drawn.</summary>
    [Export(Range = "0,10000,1")]
    public float MaxDistance
    {
        get;
        set
        {
            field = value;
            ApplyRanges();
        }
    }

    [ExportGroup("Shadows and collision")]
    [Export]
    public bool CastShadows
    {
        get;
        set
        {
            field = value;
            foreach (var batch in _batches)
                ApplyShadows(batch);
        }
    } = true;

    /// <summary>
    /// The coarsest level that casts shadows (default 1): batches above it do not. Far chunks (level 2, from
    /// <see cref="Lod2Distance"/>) lie at the end of the sun's shadow range, where their leaf cards would fill the last
    /// cascade for shadows hidden by distance and fog; 2 casts from every level, 0 only from level 0.
    /// </summary>
    [Export(Range = "0,8,1")]
    public int ShadowMaxLod
    {
        get;
        set
        {
            field = value;
            foreach (var batch in _batches)
                ApplyShadows(batch);
        }
    } = 1;

    /// <summary>
    /// The level that casts into the sun's coarse shadow passes (ADR 0167; default −1: none, every level up to
    /// <see cref="ShadowMaxLod"/> casts everywhere). From 0 up, that level casts into the coarse passes (the light's last
    /// <see cref="DirectionalLight.CoarseCascades"/> cascades and its far shadow) from any distance whatever its visibility
    /// range, and into the fine passes where it is drawn; the finer levels up to <see cref="ShadowMaxLod"/> cast only into
    /// the fine passes. Far cascades draw every tree at that cheap level, near cascades still get the far trees' long
    /// shadows. G8e.5's impostor casters will take its place.
    /// </summary>
    [Export(Range = "-1,8,1")]
    public int ShadowCoarseLod
    {
        get;
        set
        {
            field = value;
            foreach (var batch in _batches)
                ApplyShadows(batch);
        }
    } = -1;

    // Which passes a batch casts into (CastShadows, ShadowMaxLod, ShadowCoarseLod).
    private void ApplyShadows(TreeScatterBatch3D batch)
    {
        if (ShadowCoarseLod < 0)
        {
            batch.CastShadows = CastShadows && batch.Lod <= ShadowMaxLod;
            batch.ShadowCasterLod = ShadowCasterLod.All;
        }
        else if (batch.Lod == ShadowCoarseLod)
        {
            batch.CastShadows = CastShadows;
            batch.ShadowCasterLod = ShadowCasterLod.Coarse;
        }
        else
        {
            batch.CastShadows = CastShadows && batch.Lod < ShadowCoarseLod && batch.Lod <= ShadowMaxLod;
            batch.ShadowCasterLod = ShadowCasterLod.Fine;
        }
    }

    /// <summary>Static trunk capsules for every tree.</summary>
    [Export]
    public bool Collision
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            MarkDirty();
        }
    } = true;

    [Export]
    public uint CollisionLayer
    {
        get;
        set
        {
            field = value;
            foreach (var body in _bodies)
                body.CollisionLayer = value;
        }
    } = CollisionLayers.Default;

    [Export]
    public uint CollisionMask
    {
        get;
        set
        {
            field = value;
            foreach (var body in _bodies)
                body.CollisionMask = value;
        }
    } = CollisionLayers.Default;

    /// <summary>Placements stored.</summary>
    public int PlacementCount => PlacementData.Length / PlacementStride;

    /// <summary>Chunks holding at least one tree (after a build).</summary>
    public int ChunkCount => _chunkCount;

    /// <summary>The batches (one per chunk, species variant and level), in chunk, species, variant, level order.</summary>
    public IReadOnlyList<TreeScatterBatch3D> Batches => _batches;

    /// <summary>The collision bodies (one per chunk) while <see cref="Collision"/> is on.</summary>
    public IReadOnlyList<StaticBody3D> CollisionBodies => _bodies;

    /// <summary>Replaces the placements (rebuilt on the next frame, or call <see cref="Rebuild"/>).</summary>
    public void SetPlacements(ReadOnlySpan<TreePlacement> placements)
    {
        var data = new float[placements.Length * PlacementStride];
        for (var i = 0; i < placements.Length; i++)
        {
            var p = placements[i];
            var k = i * PlacementStride;
            data[k] = p.Position.X;
            data[k + 1] = p.Position.Y;
            data[k + 2] = p.Position.Z;
            data[k + 3] = p.Yaw;
            data[k + 4] = p.Scale;
            data[k + 5] = p.Species;
        }

        PlacementData = data;
    }

    public TreePlacement GetPlacement(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, PlacementCount);
        var k = index * PlacementStride;
        var d = PlacementData;
        return new TreePlacement(new Vector3(d[k], d[k + 1], d[k + 2]), d[k + 3], d[k + 4], (int)d[k + 5]);
    }

    /// <summary>The chunk of a scatter-local position: its XZ ÷ <paramref name="chunkSize"/>, floored.</summary>
    public static Vector2I ChunkOf(Vector3 position, float chunkSize) =>
        new((int)MathF.Floor(position.X / chunkSize), (int)MathF.Floor(position.Z / chunkSize));

    /// <summary>The variant a tree at <paramref name="position"/> uses among <paramref name="count"/>: a hash of its XZ bits.</summary>
    public static int VariantOf(Vector3 position, int count)
    {
        if (count <= 1)
            return 0;
        var h = unchecked((uint)BitConverter.SingleToInt32Bits(position.X) * 0x9E3779B1u ^ (uint)BitConverter.SingleToInt32Bits(position.Z) * 0x85EBCA77u);
        h ^= h >> 15;
        h = unchecked(h * 0x2C1B3C6Du);
        h ^= h >> 12;
        return (int)(h % (uint)count);
    }

    /// <summary>Generates the variants and rebuilds every batch and collision body now.</summary>
    public void Rebuild()
    {
        _dirty = false;
        Clear();
        var count = PlacementCount;
        var chunkSize = MathF.Max(ChunkSize, 1f);
        if (count == 0 || Species.Length == 0)
            return;

        // Resolve every variant once (generation happens here).
        var generator = new Trees.TreeGenerator();
        var variants = new TreeMesh?[Species.Length][];
        for (var s = 0; s < Species.Length; s++)
        {
            var species = Species[s];
            variants[s] = species is null ? [] : new TreeMesh?[species.VariantCount];
            for (var v = 0; v < variants[s].Length; v++)
                variants[s][v] = species!.GetVariant(v, generator);
        }

        // Bucket: (chunk, species, variant) → placements.
        var buckets = new SortedDictionary<(int Cz, int Cx, int Species, int Variant), List<int>>();
        var chunks = new HashSet<Vector2I>();
        for (var i = 0; i < count; i++)
        {
            var p = GetPlacement(i);
            if ((uint)p.Species >= (uint)Species.Length || variants[p.Species].Length == 0)
                continue;
            var variant = VariantOf(p.Position, variants[p.Species].Length);
            if (variants[p.Species][variant] is null)
                continue;
            var chunk = ChunkOf(p.Position, chunkSize);
            chunks.Add(chunk);
            var key = (chunk.Y, chunk.X, p.Species, variant);
            if (!buckets.TryGetValue(key, out var list))
                buckets.Add(key, list = []);
            list.Add(i);
        }

        _chunkCount = chunks.Count;
        var bodies = new Dictionary<Vector2I, StaticBody3D>();
        var shapes = new Dictionary<(int, int), CapsuleShape3D>();
        foreach (var ((cz, cx, s, v), indices) in buckets)
        {
            var mesh = variants[s][v]!;
            var species = Species[s];
            var bark = species.ResolveBarkMaterial(mesh);
            var leaves = species.ResolveLeafMaterial(mesh);
            var transforms = new Transform3D[indices.Count];
            for (var k = 0; k < indices.Count; k++)
            {
                var p = GetPlacement(indices[k]);
                transforms[k] = Transform3D.FromTrs(p.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, p.Yaw), new Vector3(p.Scale));
            }

            var chunk = new Vector2I(cx, cz);
            var multimeshes = new MultiMesh[mesh.Lods.Length];
            var bounds = Aabb.Empty;
            for (var lod = 0; lod < multimeshes.Length; lod++)
            {
                multimeshes[lod] = new MultiMesh { Mesh = mesh.Lods[lod], InstanceCount = transforms.Length };
                multimeshes[lod].SetTransforms(transforms);
                bounds = bounds.Merge(multimeshes[lod].GetAabb());
            }

            for (var lod = 0; lod < multimeshes.Length; lod++)
            {
                var multimesh = multimeshes[lod];
                var batch = new TreeScatterBatch3D
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"Chunk{cx}_{cz}_S{s}V{v}_Lod{lod}"),
                    Multimesh = multimesh,
                    Chunk = chunk,
                    Species = s,
                    Variant = v,
                    Lod = lod,
                    LodCount = mesh.Lods.Length,
                    BarkMaterial = bark,
                    LeafMaterial = leaves,
                    CustomAabb = bounds, // one distance for every level of the batch: they switch together
                };
                ApplyShadows(batch);
                _batches.Add(batch);
                AddChild(batch);
            }

            if (!Collision || mesh.TrunkRadius <= 0f || mesh.TrunkHeight <= 0f)
                continue;
            if (!shapes.TryGetValue((s, v), out var shape))
            {
                shape = new CapsuleShape3D { Radius = mesh.TrunkRadius, Height = MathF.Max(mesh.TrunkHeight, 2f * mesh.TrunkRadius) };
                shapes.Add((s, v), shape);
            }

            if (!bodies.TryGetValue(chunk, out var body))
            {
                body = new StaticBody3D
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"Trunks{cx}_{cz}"),
                    CollisionLayer = CollisionLayer,
                    CollisionMask = CollisionMask,
                };
                bodies.Add(chunk, body);
                _bodies.Add(body);
            }

            foreach (var index in indices)
            {
                var p = GetPlacement(index);
                body.AddChild(new CollisionShape3D
                {
                    Shape = shape,
                    Position = p.Position + new Vector3(0f, shape.Height * 0.5f * p.Scale, 0f),
                    Scale = new Vector3(p.Scale),
                });
            }
        }

        foreach (var body in _bodies)
            AddChild(body);
        ApplyRanges();
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    protected override void OnReady()
    {
        base.OnReady();
        Rebuild();
        SetProcess(false);
    }

    /// <summary>Rebuilds after changes (at most once per frame); idle otherwise.</summary>
    protected override void OnProcess(in GameTime gameTime)
    {
        if (_dirty)
            Rebuild();
        SetProcess(false);
    }

    private void MarkDirty()
    {
        _dirty = true;
        if (IsInsideTree && IsNodeReady)
            SetProcess(true);
    }

    private void ApplyRanges()
    {
        foreach (var batch in _batches)
        {
            var (begin, end) = TreeMesh.LodRange(batch.Lod, batch.LodCount, Lod1Distance, Lod2Distance, MaxDistance);
            batch.VisibilityRangeBegin = begin;
            batch.VisibilityRangeEnd = end;
        }
    }

    private void Clear()
    {
        foreach (var batch in _batches)
            batch.Free();
        foreach (var body in _bodies)
            body.Free();
        _batches.Clear();
        _bodies.Clear();
        _chunkCount = 0;
    }
}
