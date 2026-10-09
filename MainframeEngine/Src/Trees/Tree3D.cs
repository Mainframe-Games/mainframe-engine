using System.Globalization;
using System.Numerics;
using MainframeEngine.Trees;

namespace MainframeEngine;

/// <summary>
/// A procedural tree (G8b, ADR 0158): Ez Tree's generator run on <see cref="Options"/> (or the preset named by
/// <see cref="Preset"/>) for <see cref="Seed"/> in <see cref="Style"/>, drawn as three levels of detail, with a trunk
/// collision capsule. A <see cref="BakedMesh"/> (<see cref="Bake"/>) is drawn as is and skips the generator.
/// </summary>
/// <remarks>
/// <para>Each level is an internal, unsaved <see cref="TreeLod3D"/> child whose visibility range comes from
/// <see cref="Lod1Distance"/>, <see cref="Lod2Distance"/> and <see cref="MaxDistance"/> (distance from the camera to the
/// tree's bounds centre), so exactly one level draws — in the shadow maps too. The trunk is an internal
/// <see cref="StaticBody3D"/> with a <see cref="CapsuleShape3D"/> (<see cref="Collision"/>).</para>
/// <para>The tree generates when it becomes ready, in the editor and at run time, and again (once per frame at most) after
/// its properties or its <see cref="Options"/> change; <see cref="Regenerate"/> does it now. Trees with the same preset,
/// seed and style share one generated <see cref="TreeMesh"/>, and every tree shares the <see cref="TreeMaterials"/> of its
/// look, so equal trees batch into instanced draws. Generation is synchronous main-thread work (about 3–5 ms for Oak
/// Medium Realistic); bake trees a shipped game places by hand. For forests use <see cref="TreeScatter"/>.</para>
/// </remarks>
[Tool]
[EditorIcon("feather")]
public sealed class Tree3D : Node3D
{
    private static readonly Lock CacheGate = new();
    private static readonly Dictionary<string, TreeOptions> PresetOptions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<(string Preset, int Seed, TreeStyle Style), WeakReference<TreeMesh>> PresetMeshes = [];

    private readonly Action _onOptionsChanged;
    private TreeOptions? _subscribed;
    private TreeLod3D[] _lods = [];
    private StaticBody3D? _body;
    private CapsuleShape3D? _trunkShape;
    private TreeMesh? _mesh;
    private bool _dirty = true;

    public Tree3D()
    {
        _onOptionsChanged = MarkDirty;
    }

    /// <summary>The generator's inputs; null: the preset named by <see cref="Preset"/>. Edits regenerate the tree.</summary>
    [Export]
    public TreeOptions? Options
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            if (IsInsideTree)
                Subscribe();
            MarkDirty();
        }
    }

    /// <summary>
    /// A <see cref="TreePresets"/> name ("Oak Medium"), used when <see cref="Options"/> is null. Trees naming a preset share
    /// one read-only copy of it; assign <see cref="Options"/> (<c>TreePresets.Load</c>) to edit.
    /// </summary>
    [Export]
    public string Preset
    {
        get;
        set
        {
            value ??= string.Empty;
            if (field == value)
                return;
            field = value;
            MarkDirty();
        }
    } = string.Empty;

    /// <summary>The seed; −1 (default) uses the options' own <see cref="TreeOptions.Seed"/>.</summary>
    [Export(Range = "-1,65536,1")]
    public int Seed
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            MarkDirty();
        }
    } = -1;

    [Export]
    public TreeStyle Style
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

    /// <summary>A baked tree (<see cref="Bake"/>): drawn as is, the generator never runs. Its style and options win.</summary>
    [Export]
    public TreeMesh? BakedMesh
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            MarkDirty();
        }
    }

    /// <summary>Replaces the bark material (null: <see cref="TreeMaterials.Bark"/> for the options and style).</summary>
    [ExportGroup("Materials")]
    [Export]
    public Material? BarkMaterial
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            MarkDirty();
        }
    }

    /// <summary>Replaces the leaf material (null: <see cref="TreeMaterials.Leaves"/>).</summary>
    [Export]
    public Material? LeafMaterial
    {
        get;
        set
        {
            if (ReferenceEquals(field, value))
                return;
            field = value;
            MarkDirty();
        }
    }

    /// <summary>Whether the levels cast shadows (leaf shadows sway with the leaves).</summary>
    [Export]
    public bool CastShadows
    {
        get;
        set
        {
            field = value;
            foreach (var lod in _lods)
                lod.CastShadows = value;
        }
    } = true;

    /// <summary>A static trunk capsule (<see cref="TrunkShape"/>) for the player to bump into.</summary>
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

    /// <summary>Distance (m, camera to the tree's bounds centre) from which level 1 replaces level 0 (Ez Tree: 100 units × 0.3).</summary>
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

    /// <summary>Distance from which level 2 replaces level 1 (Ez Tree: 250 units × 0.3).</summary>
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

    /// <summary>Distance from which the tree is not drawn at all; 0 = always drawn.</summary>
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

    /// <summary>What is drawn: the baked or generated tree (null before the tree is ready or without options).</summary>
    public TreeMesh? Mesh => _mesh;

    /// <summary>The trunk capsule of <see cref="Mesh"/> (zero without one).</summary>
    public TreeTrunkCapsule Trunk => _mesh?.Trunk ?? default;

    /// <summary>The trunk's collision shape while <see cref="Collision"/> is on and the tree is built, else null.</summary>
    public CapsuleShape3D? TrunkShape => _trunkShape;

    /// <summary>The trunk body while <see cref="Collision"/> is on and the tree is built, else null.</summary>
    public StaticBody3D? TrunkBody => _body;

    /// <summary>Levels of detail shown (0 before the tree is built).</summary>
    public int LodCount => _lods.Length;

    /// <summary>The node drawing level <paramref name="lod"/>.</summary>
    public TreeLod3D GetLodNode(int lod) => _lods[lod];

    /// <summary>The options the tree uses: <see cref="Options"/>, else the shared copy of <see cref="Preset"/>, else the baked mesh's.</summary>
    public TreeOptions? ResolveOptions()
    {
        if (Options is { } options)
            return options;
        if (!string.IsNullOrEmpty(Preset))
            return SharedPreset(Preset);
        return BakedMesh?.Options;
    }

    /// <summary>The seed generation uses: <see cref="Seed"/>, or the options' own when it is −1.</summary>
    public int ResolveSeed(TreeOptions options) => Seed >= 0 ? Seed : options.Seed;

    /// <summary>Generates (unless baked) and rebuilds the levels and the trunk now.</summary>
    public void Regenerate()
    {
        _dirty = false;
        _mesh = BakedMesh ?? GenerateMesh();
        if (_mesh is null)
        {
            ClearLods();
            ClearBody();
            return;
        }

        var options = Options ?? (string.IsNullOrEmpty(Preset) ? null : SharedPreset(Preset)) ?? _mesh.Options;
        var style = BakedMesh?.Style ?? Style;
        var bark = BarkMaterial ?? (options is null ? null : TreeMaterials.Bark(options, style));

        var count = _mesh.Lods.Length;
        if (_lods.Length != count)
        {
            ClearLods();
            _lods = new TreeLod3D[count];
            for (var i = 0; i < count; i++)
            {
                _lods[i] = new TreeLod3D { Name = string.Create(CultureInfo.InvariantCulture, $"Lod{i}"), TreeNode = this, Lod = i };
                AddChild(_lods[i]);
            }
        }

        // Every level gets the union of their bounds, so all levels measure the same distance and switch together.
        var bounds = Aabb.Empty;
        foreach (var lod in _mesh.Lods)
            bounds = bounds.Merge(lod.Bounds);

        for (var i = 0; i < count; i++)
        {
            var node = _lods[i];
            node.Mesh = _mesh.Lods[i];
            node.CustomAabb = bounds;
            node.BarkMaterial = bark;
            node.LeafMaterial = LeafMaterial ?? (options is null ? null : TreeMaterials.Leaves(options, style, i));
            node.CastShadows = CastShadows;
            node.RenderStamp++;
        }

        ApplyRanges();
        UpdateBody(_mesh.Trunk);
    }

    /// <summary>
    /// Generates the tree from its options (ignoring <see cref="BakedMesh"/>), makes the result its
    /// <see cref="BakedMesh"/> and, when <paramref name="path"/> is given, saves it there as <c>.mres</c> (all levels, the
    /// trunk capsule and the options). Returns the baked mesh.
    /// </summary>
    public TreeMesh Bake(string? path = null)
    {
        var options = Options ?? (string.IsNullOrEmpty(Preset) ? null : SharedPreset(Preset))
            ?? throw new InvalidOperationException($"Tree '{Name}' has no options or preset to bake.");
        var mesh = TreeMesh.Generate(options, ResolveSeed(options), Style);
        if (path is not null)
            ResourceSaver.Save(mesh, path);
        BakedMesh = mesh;
        if (IsNodeReady)
            Regenerate();
        return mesh;
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Subscribe();
    }

    protected override void OnReady()
    {
        base.OnReady();
        Regenerate();
        SetProcess(false);
    }

    protected override void OnExitTree()
    {
        Unsubscribe();
        base.OnExitTree();
    }

    /// <summary>Regenerates after changes (at most once per frame); idle otherwise.</summary>
    protected override void OnProcess(in GameTime gameTime)
    {
        if (_dirty)
            Regenerate();
        SetProcess(false);
    }

    private void MarkDirty()
    {
        _dirty = true;
        if (IsInsideTree && IsNodeReady)
            SetProcess(true);
    }

    private void Subscribe()
    {
        if (ReferenceEquals(_subscribed, Options))
            return;
        Unsubscribe();
        if (Options is { } options)
        {
            options.Changed += _onOptionsChanged;
            _subscribed = options;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed is null)
            return;
        _subscribed.Changed -= _onOptionsChanged;
        _subscribed = null;
    }

    // ── Building ───────────────────────────────────────────────────────────────

    private TreeMesh? GenerateMesh()
    {
        if (Options is { } options)
            return TreeMesh.Generate(options, ResolveSeed(options), Style);
        if (string.IsNullOrEmpty(Preset))
            return null;

        var preset = SharedPreset(Preset);
        var key = (Preset.ToLowerInvariant(), ResolveSeed(preset), Style);
        lock (CacheGate)
        {
            if (PresetMeshes.TryGetValue(key, out var weak) && weak.TryGetTarget(out var cached))
                return cached;
        }

        var mesh = TreeMesh.Generate(preset, key.Item2, Style);
        lock (CacheGate)
            PresetMeshes[key] = new WeakReference<TreeMesh>(mesh);
        return mesh;
    }

    private static TreeOptions SharedPreset(string name)
    {
        lock (CacheGate)
        {
            if (!PresetOptions.TryGetValue(name, out var options))
            {
                options = TreePresets.Load(name);
                PresetOptions.Add(name, options);
            }

            return options;
        }
    }

    private void ApplyRanges()
    {
        for (var i = 0; i < _lods.Length; i++)
        {
            var (begin, end) = TreeMesh.LodRange(i, _lods.Length, Lod1Distance, Lod2Distance, MaxDistance);
            _lods[i].VisibilityRangeBegin = begin;
            _lods[i].VisibilityRangeEnd = end;
        }
    }

    private void UpdateBody(TreeTrunkCapsule trunk)
    {
        if (!Collision || trunk.Radius <= 0f || trunk.Height <= 0f)
        {
            ClearBody();
            return;
        }

        var height = MathF.Max(trunk.Height, 2f * trunk.Radius);
        if (_body is null || _trunkShape is null)
        {
            _trunkShape = new CapsuleShape3D { Radius = trunk.Radius, Height = height };
            _body = new StaticBody3D { Name = "Trunk" };
            _body.AddChild(new CollisionShape3D { Name = "Shape", Shape = _trunkShape });
            AddChild(_body);
        }
        else
        {
            _trunkShape.Radius = trunk.Radius;
            _trunkShape.Height = height;
        }

        _body.Position = new Vector3(0f, height * 0.5f, 0f);
    }

    private void ClearLods()
    {
        foreach (var lod in _lods)
            lod.Free();
        _lods = [];
    }

    private void ClearBody()
    {
        _body?.Free();
        _body = null;
        _trunkShape = null;
    }
}
