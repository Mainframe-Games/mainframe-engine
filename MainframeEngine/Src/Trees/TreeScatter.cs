using System.Globalization;
using System.Numerics;

namespace MainframeEngine;

/// <summary>How a <see cref="TreeScatter"/> chooses its trees' levels of detail (ADR 0172).</summary>
public enum TreeLodSelection : byte
{
    /// <summary>A chunk draws one level, chosen by its bounds centre's distance.</summary>
    PerChunk,

    /// <summary>Each tree chooses its level by its own distance (in the vertex shader), cross-fading between levels.</summary>
    PerInstance,
}

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
    private readonly Dictionary<(FoliageMaterial3D, int, int, int), Material> _levelMaterials = [];
    private readonly Dictionary<TreeImpostor, ImpostorMaterial3D> _impostorMaterials = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Material, (int Lod, int Levels, bool Impostor)> _levelCounts = new(ReferenceEqualityComparer.Instance);
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

    /// <summary>
    /// Distance from which each Realistic variant's octahedral impostor (ADR 0172, <see cref="TreeImpostor"/>, baked on the
    /// first build) replaces its last mesh level: one camera-facing quad per tree, lit live. 0 (default): no impostors.
    /// The impostor is the level after the meshes (<see cref="TreeScatterBatch3D.Lod"/> = the mesh level count), so
    /// <see cref="ShadowCoarseLod"/> can name it: the far cascades and the far shadow then draw every tree as one quad
    /// facing the sun.
    /// </summary>
    [Export(Range = "0,10000,1")]
    public float ImpostorDistance
    {
        get;
        set
        {
            if (field == value)
                return;
            var rebuild = field <= 0f != value <= 0f;
            field = value;
            if (rebuild)
                MarkDirty();
            else
                ApplyRanges();
        }
    }

    /// <summary>
    /// The share of each impostor's silhouette that casts into the sun's shadow maps (<see cref="ImpostorMaterial3D.ShadowDensity"/>,
    /// ADR 0172): 1 (default) casts the whole baked view; less lets light through the far trees' canopies, as the sparse mesh
    /// levels do.
    /// </summary>
    [Export(Range = "0,1,0.01")]
    public float ImpostorShadowDensity
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            foreach (var material in _impostorMaterials.Values)
                material.ShadowDensity = value;
        }
    } = 1f;

    /// <summary>Views per side of the impostors' octahedral grid (8: 64 views).</summary>
    [Export(Range = "2,16,1")]
    public int ImpostorFrames
    {
        get;
        set
        {
            field = value;
            MarkDirty();
        }
    } = 8;

    /// <summary>Pixels per impostor view (128: a 1024² atlas at 8 frames).</summary>
    [Export(Range = "16,512,1")]
    public int ImpostorResolution
    {
        get;
        set
        {
            field = value;
            MarkDirty();
        }
    } = 128;

    /// <summary>
    /// How levels are chosen (ADR 0172): per chunk (default: a chunk draws one level, by its centre's distance), or per
    /// instance: every tree picks its own level by its distance in the vertex shader and cross-fades over
    /// ±<see cref="LodFadeMargin"/> around each switch (a dither TAA smooths), so nothing pops. Per instance, a chunk
    /// draws every level some of its trees use (the vertex shader drops the others' trees). Realistic trees only.
    /// </summary>
    [Export]
    public TreeLodSelection LodSelection
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            MarkDirty();
        }
    }

    /// <summary>Half-width (m) of the cross-fade around each switch with <see cref="TreeLodSelection.PerInstance"/>; 0 = a hard switch.</summary>
    [Export(Range = "0,50,0.1")]
    public float LodFadeMargin
    {
        get;
        set
        {
            field = value;
            ApplyRanges();
        }
    } = 3f;

    /// <summary>
    /// Per-tree brightness variation (ADR 0175, <see cref="InstanceVariation"/>): every tree's bark, leaves and impostor
    /// scale by 1 ± this, from a hash of its position; 0 (default): off.
    /// </summary>
    [ExportGroup("Variation")]
    [Export(Range = "0,0.5,0.01")]
    public float InstanceValueJitter
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            ApplyVariation();
        }
    }

    /// <summary>Per-tree hue variation (ADR 0175): warmer or cooler by up to this; 0 (default): off.</summary>
    [Export(Range = "0,0.5,0.01")]
    public float InstanceHueJitter
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            ApplyVariation();
        }
    }

    // The level and impostor materials are the scatter's own copies; with variation on, every material it draws is.
    private bool HasVariation => InstanceValueJitter > 0f || InstanceHueJitter > 0f;

    private void ApplyVariation()
    {
        if (HasVariation && _levelMaterials.Count == 0 && _impostorMaterials.Count == 0 && _batches.Count > 0)
        {
            MarkDirty(); // the batches still draw the species' shared materials: rebuild with copies
            return;
        }

        foreach (var material in _levelMaterials.Values)
            if (material is FoliageMaterial3D foliage)
                (foliage.InstanceValueJitter, foliage.InstanceHueJitter) = (InstanceValueJitter, InstanceHueJitter);
        foreach (var material in _impostorMaterials.Values)
            (material.InstanceValueJitter, material.InstanceHueJitter) = (InstanceValueJitter, InstanceHueJitter);
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
    /// shadows. With <see cref="ImpostorDistance"/>, the impostor level (the mesh level count, 3) casts as one quad per
    /// tree facing the sun (ADR 0172): the cheapest coarse caster.
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
        var impostors = ImpostorDistance > 0f;
        var impostorOptions = new TreeImpostorOptions { Frames = Math.Clamp(ImpostorFrames, 2, 16), CellSize = Math.Clamp(ImpostorResolution, 16, 512) };
        foreach (var ((cz, cx, s, v), indices) in buckets)
        {
            var mesh = variants[s][v]!;
            var species = Species[s];
            var bark = species.ResolveBarkMaterial(mesh);
            var leaves = species.ResolveLeafMaterial(mesh); // level 0's (the impostor bakes level 0)
            var impostor = impostors && mesh.Style == Trees.TreeStyle.Realistic ? mesh.GetImpostor(bark, leaves, impostorOptions) : null;
            var meshLevels = mesh.Lods.Length;
            var levels = meshLevels + (impostor is null ? 0 : 1);
            var transforms = new Transform3D[indices.Count];
            for (var k = 0; k < indices.Count; k++)
            {
                var p = GetPlacement(indices[k]);
                transforms[k] = Transform3D.FromTrs(p.Position, Quaternion.CreateFromAxisAngle(Vector3.UnitY, p.Yaw), new Vector3(p.Scale));
            }

            var chunk = new Vector2I(cx, cz);
            var origins = new Vector3[transforms.Length];
            for (var k = 0; k < origins.Length; k++)
                origins[k] = transforms[k].Origin;
            var multimeshes = new MultiMesh[levels];
            var bounds = Aabb.Empty;
            for (var lod = 0; lod < multimeshes.Length; lod++)
            {
                multimeshes[lod] = new MultiMesh { Mesh = lod < meshLevels ? mesh.Lods[lod] : impostor!.Mesh, InstanceCount = transforms.Length };
                multimeshes[lod].SetTransforms(transforms);
                bounds = bounds.Merge(multimeshes[lod].GetAabb());
            }

            for (var lod = 0; lod < multimeshes.Length; lod++)
            {
                var multimesh = multimeshes[lod];
                var isImpostor = lod >= meshLevels;
                var batch = new TreeScatterBatch3D
                {
                    Name = string.Create(CultureInfo.InvariantCulture, $"Chunk{cx}_{cz}_S{s}V{v}_{(isImpostor ? "Impostor" : "Lod")}{lod}"),
                    Multimesh = multimesh,
                    Chunk = chunk,
                    Species = s,
                    Variant = v,
                    Lod = lod,
                    LodCount = levels,
                    IsImpostor = isImpostor,
                    HasImpostorLevel = impostor is not null,
                    BarkMaterial = isImpostor ? LevelMaterial(impostor!, s, v, lod, levels) : LevelMaterial(bark, s, lod, levels, impostor is not null),
                    LeafMaterial = isImpostor ? null : LevelMaterial(species.ResolveLeafMaterial(mesh, lod), s, lod, levels, impostor is not null),
                    CustomAabb = bounds, // one distance for every level of the batch: they switch together
                };
                if (LodSelection == TreeLodSelection.PerInstance && batch.BarkMaterial is FoliageMaterial3D { InstanceVisibility: true } or ImpostorMaterial3D)
                    batch.InstanceOrigins = origins; // each tree picks its level (ADR 0172)
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

    /// <summary>
    /// The distance range of level <paramref name="lod"/> of <paramref name="count"/> (<see cref="TreeMesh.LodRange"/>; with
    /// an impostor, the last mesh level ends and the impostor begins at <see cref="ImpostorDistance"/>).
    /// </summary>
    public (float Begin, float End) LevelRange(int lod, int count, bool impostor)
    {
        if (!impostor)
            return TreeMesh.LodRange(lod, count, Lod1Distance, Lod2Distance, MaxDistance);
        if (lod == count - 1)
            return (ImpostorDistance, MaxDistance);
        var (begin, end) = TreeMesh.LodRange(lod, count - 1, Lod1Distance, Lod2Distance, ImpostorDistance);
        return begin < ImpostorDistance ? (begin, end <= 0f ? ImpostorDistance : MathF.Min(end, ImpostorDistance)) : (float.MaxValue, float.MaxValue);
    }

    // Per instance (ADR 0172): the level's material, a copy that draws only the trees in the level's range.
    private Material? LevelMaterial(Material? material, int species, int lod, int levels, bool impostor)
    {
        var perInstance = LodSelection == TreeLodSelection.PerInstance;
        if ((!perInstance && !HasVariation) || material is not FoliageMaterial3D foliage)
            return material;
        var key = (foliage, species, impostor ? 1 : 0, lod);
        if (!_levelMaterials.TryGetValue(key, out var copy))
        {
            var level = (FoliageMaterial3D)foliage.Duplicate();
            level.ResourceName = $"{foliage.ResourceName} (level {lod})";
            level.InstanceVisibility = perInstance;
            level.InstanceValueJitter = InstanceValueJitter;
            level.InstanceHueJitter = InstanceHueJitter;
            _levelMaterials.Add(key, copy = level);
        }

        _levelCounts[copy] = (lod, levels, impostor);
        return copy;
    }

    private ImpostorMaterial3D LevelMaterial(TreeImpostor impostor, int species, int variant, int lod, int levels)
    {
        if (!_impostorMaterials.TryGetValue(impostor, out var material))
        {
            material = impostor.CreateMaterial();
            if (Species[species] is { } kind && kind.ResolveLeafMaterial(kind.GetVariant(variant)!) is FoliageMaterial3D leaves)
            {
                material.Translucency = leaves.Translucency;
                material.TranslucencyColor = leaves.TranslucencyColor;
                material.TranslucencyScatter = leaves.TranslucencyScatter;
                material.LeafRoughness = leaves.Roughness;
            }

            material.InstanceVisibility = LodSelection == TreeLodSelection.PerInstance;
            material.ShadowDensity = ImpostorShadowDensity;
            material.InstanceValueJitter = InstanceValueJitter;
            material.InstanceHueJitter = InstanceHueJitter;
            _impostorMaterials.Add(impostor, material);
        }

        _levelCounts[material] = (lod, levels, true);
        return material;
    }

    private void ApplyRanges()
    {
        var margin = MathF.Max(LodFadeMargin, 0f);
        foreach (var batch in _batches)
        {
            var (begin, end) = LevelRange(batch.Lod, batch.LodCount, batch.HasImpostorLevel);
            batch.VisibilityRangeBegin = begin;
            batch.VisibilityRangeEnd = end;
            // Per instance, the batch draws while any of its trees is in range (TreeScatterBatch3D.IsInVisibilityRange).
            batch.InstanceBegin = begin;
            batch.InstanceEnd = end;
            batch.InstanceMargin = margin;
        }

        foreach (var (material, (lod, levels, impostor)) in _levelCounts)
        {
            var (begin, end) = LevelRange(lod, levels, impostor);
            begin = begin == float.MaxValue ? 0f : begin;
            end = end == float.MaxValue ? 0f : end;
            switch (material)
            {
                case FoliageMaterial3D foliage:
                    foliage.InstanceVisibilityBegin = begin;
                    foliage.InstanceVisibilityEnd = end;
                    foliage.InstanceVisibilityMargin = margin;
                    break;
                case ImpostorMaterial3D impostorMaterial:
                    impostorMaterial.InstanceVisibilityBegin = begin;
                    impostorMaterial.InstanceVisibilityEnd = end;
                    impostorMaterial.InstanceVisibilityMargin = margin;
                    break;
            }
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
        _levelMaterials.Clear();
        _impostorMaterials.Clear();
        _levelCounts.Clear();
        _chunkCount = 0;
    }
}
