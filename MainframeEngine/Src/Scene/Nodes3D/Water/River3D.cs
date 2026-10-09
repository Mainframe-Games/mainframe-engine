using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A spline stream (G8c): a <see cref="Curve3D"/> (anchor Y = water surface, per-point width and depth) generates a
/// ribbon mesh at the downhill-clamped surface height, with flow speed from the slope, and answers water queries
/// (<see cref="IWaterBody3D"/>, registered with <see cref="World3D.Water"/> while in the tree).
/// </summary>
/// <remarks>
/// <para>The river regenerates when the curve raises <see cref="Resource.Changed"/> or a Shape or Flow export changes;
/// nothing is generated per frame. Generation is <see cref="RiverBuilder"/> (plain C#). The ribbon is an internal,
/// unowned (so unsaved) <see cref="MeshInstance3D"/> child named <c>Ribbon</c>, created when the river is ready.</para>
/// <para>The ribbon renders with <see cref="Material"/>, by default a <see cref="WaterMaterial3D"/>; its
/// <see cref="MeshSurface.Custom0"/> stream carries the column depth, flow and foam. <see cref="Carve"/> cuts the channel
/// into a <see cref="Terrain3D"/> (ADR 0159). Falls, pond joins and audio come later (docs/design/water.md). Rivers are
/// meant to be translated and turned about Y; queries and carving assume no tilt or scale.</para>
/// </remarks>
[EditorIcon("ripple", Family = EditorIconFamily.Space3D)]
public class River3D : Node3D, IWaterBody3D
{
    private readonly Action _onCurveChanged;
    private readonly RiverBuilder _builder = new();
    private RiverSettings _settings = RiverSettings.Default;
    private Curve3D? _curve;
    private Material? _material;
    private MeshInstance3D? _ribbon;
    private ArrayMesh? _mesh;
    private MeshSurface? _surface;
    private Material? _defaultMaterial;
    private World3D? _world;
    private bool _dirty = true;

    public River3D()
    {
        _onCurveChanged = Invalidate;
    }

    /// <summary>The centreline; anchor Y is the water surface height, per-point width and depth shape the channel.</summary>
    [Export]
    public Curve3D? Curve
    {
        get => _curve;
        set
        {
            if (ReferenceEquals(_curve, value))
                return;
            if (_curve is not null)
                _curve.Changed -= _onCurveChanged;
            _curve = value;
            if (_curve is not null)
                _curve.Changed += _onCurveChanged;
            Invalidate();
        }
    }

    /// <summary>The ribbon's material (null: a default <see cref="WaterMaterial3D"/>).</summary>
    [Export]
    public Material? Material
    {
        get => _material;
        set
        {
            _material = value;
            if (_ribbon is not null)
                _ribbon.MaterialOverride = _material ?? DefaultMaterial;
        }
    }

    /// <summary>Distance between cross-sections in metres (Shape).</summary>
    [Export(Range = "0.1,10,0.05")]
    public float SectionLength { get => _settings.SectionLength; set => SetSetting(ref _settings.SectionLength, MathF.Max(value, 0.05f)); }

    /// <summary>Quads across the ribbon (Shape).</summary>
    [Export(Range = "1,32,1")]
    public int CrossSegments
    {
        get => _settings.CrossSegments;
        set
        {
            value = Math.Clamp(value, 1, 64);
            if (_settings.CrossSegments == value)
                return;
            _settings.CrossSegments = value;
            Invalidate();
        }
    }

    /// <summary>Metres of river per UV.v repeat (Shape).</summary>
    [Export(Range = "0.1,100,0.1")]
    public float UvLength { get => _settings.UvLength; set => SetSetting(ref _settings.UvLength, MathF.Max(value, 0.01f)); }

    /// <summary>Clamp the surface heights so they never rise downstream (Shape; the curve itself is unchanged).</summary>
    [Export]
    public bool EnforceDownhill
    {
        get => _settings.EnforceDownhill;
        set
        {
            if (_settings.EnforceDownhill == value)
                return;
            _settings.EnforceDownhill = value;
            Invalidate();
        }
    }

    /// <summary>Slowest flow in m/s (Flow): still stretches still drift.</summary>
    [Export(Range = "0,10,0.05")]
    public float MinSpeed { get => _settings.MinSpeed; set => SetSetting(ref _settings.MinSpeed, MathF.Max(value, 0f)); }

    /// <summary>Fastest flow in m/s (Flow).</summary>
    [Export(Range = "0,20,0.1")]
    public float MaxSpeed { get => _settings.MaxSpeed; set => SetSetting(ref _settings.MaxSpeed, MathF.Max(value, 0f)); }

    /// <summary>Speed gained per √slope (Flow): speed = MinSpeed + this × √slope, clamped (1 % ≈ 0.9 m/s, 10 % ≈ 2.2 m/s).</summary>
    [Export(Range = "0,50,0.1")]
    public float SpeedPerSqrtSlope { get => _settings.SpeedPerSqrtSlope; set => SetSetting(ref _settings.SpeedPerSqrtSlope, MathF.Max(value, 0f)); }

    /// <summary>Arc length in metres over which the slope is measured (Flow).</summary>
    [Export(Range = "0.1,100,0.1")]
    public float SlopeWindow { get => _settings.SlopeWindow; set => SetSetting(ref _settings.SlopeWindow, MathF.Max(value, 0.01f)); }

    /// <summary>
    /// The terrain <see cref="Carve"/> writes into (Carve). Empty: <see cref="Terrain"/> if set, else the first
    /// <see cref="Terrain3D"/> in the tree under the river's start.
    /// </summary>
    [ExportGroup("Carve")]
    [Export]
    public NodePath TerrainPath { get; set; } = "";

    /// <summary>Width in metres of the bank that blends from the channel's lip back to the original ground (Carve).</summary>
    [Export(Range = "0,20,0.1")]
    public float BankWidth { get; set; } = 2f;

    /// <summary>
    /// How far the carved banks stand above the water surface, in metres (Carve): the ribbon's freeboard, so the
    /// shoreline is a clean cut with no z-fighting.
    /// </summary>
    [Export(Range = "0,1,0.01")]
    public float ShoreLift { get; set; } = 0.05f;

    /// <summary>
    /// Names the carve record (set by the first <see cref="Carve"/>, cleared by <see cref="Uncarve"/>): the terrain keeps
    /// the original heights under this id (<c>carve_&lt;id&gt;.json</c> beside its layers once it saves).
    /// </summary>
    [Export]
    public string CarveId { get; set; } = "";

    /// <summary>The terrain to carve, set from code (wins over <see cref="TerrainPath"/>; not saved).</summary>
    public Terrain3D? Terrain { get; set; }

    /// <summary>The terrain edit the last carve, re-carve or uncarve recorded (undo with <see cref="TerrainEditRecord.Undo"/>), or null.</summary>
    public TerrainEditRecord? LastCarveEdit { get; private set; }

    /// <summary>Raised after the ribbon and the query data were rebuilt.</summary>
    [Signal]
    public event Action? Regenerated;

    /// <summary>The generated vertex streams (rebuilt on change; valid until the next regeneration).</summary>
    public RiverMeshData MeshData
    {
        get
        {
            EnsureGenerated();
            return _builder.Mesh;
        }
    }

    /// <summary>Arc length of the river in metres.</summary>
    public float Length
    {
        get
        {
            EnsureGenerated();
            return _builder.Length;
        }
    }

    /// <summary>The ribbon node (null until the river is ready in a tree).</summary>
    public MeshInstance3D? Ribbon => _ribbon;

    internal RiverBuilder Builder
    {
        get
        {
            EnsureGenerated();
            return _builder;
        }
    }

    /// <summary>The flow speed in m/s <paramref name="offset"/> metres along the river.</summary>
    public float FlowSpeedAt(float offset)
    {
        EnsureGenerated();
        return _builder.SectionCount > 0 ? _builder.SpeedAt(offset, _settings) : 0f;
    }

    /// <summary>The (downhill-clamped) surface height, river-local, <paramref name="offset"/> metres along the river.</summary>
    public float SurfaceHeightAtOffset(float offset)
    {
        EnsureGenerated();
        return _builder.SurfaceHeightAt(offset);
    }

    /// <summary>Rebuilds the ribbon and the query data now.</summary>
    public void Regenerate()
    {
        _dirty = false;
        _builder.Build(_curve, _settings);
        UpdateRibbon();
        Regenerated?.Invoke();
    }

    /// <inheritdoc />
    public Aabb WaterBounds
    {
        get
        {
            EnsureGenerated();
            var bounds = _builder.Bounds;
            return bounds.IsEmpty ? bounds : bounds.Transform(GlobalTransform.ToMatrix4x4());
        }
    }

    /// <inheritdoc />
    public bool TrySample(Vector3 position, out WaterSample sample)
    {
        EnsureGenerated();
        var global = GlobalTransform;
        var local = global.AffineInverse().TransformPoint(position);
        if (!_builder.TrySample(local, out var s, out _))
        {
            sample = default;
            return false;
        }

        var surface = global.TransformPoint(new Vector3(local.X, s.SurfaceHeight, local.Z)).Y;
        sample = new WaterSample(surface, s.ColumnDepth, global.TransformDirection(s.Flow));
        return true;
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _world = GetWorld3D();
        _world?.Water.Register(this);
        if (_dirty && _ribbon is not null)
            Regenerate();
    }

    protected override void OnReady()
    {
        base.OnReady();
        if (_ribbon is null)
        {
            _ribbon = new MeshInstance3D { Name = "Ribbon", MaterialOverride = _material ?? DefaultMaterial };
            AddChild(_ribbon); // unowned: generated, never saved
        }

        if (_dirty)
            Regenerate();
        else
            UpdateRibbon();
    }

    protected override void OnExitTree()
    {
        _world?.Water.Unregister(this);
        _world = null;
        base.OnExitTree();
    }

    // ── Carving ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Carves the river's channel into the terrain as one height edit (<see cref="LastCarveEdit"/>): a parabolic bed
    /// <c>Depth</c> below the surface, banks <see cref="ShoreLift"/> above it blending back to the ground over
    /// <see cref="BankWidth"/> (docs/design/water.md). The original heights are kept under <see cref="CarveId"/>, so
    /// carving again first restores them (as <see cref="Recarve"/>). False when there is no terrain under the river.
    /// </summary>
    public bool Carve() => CarveInto(ResolveTerrain());

    /// <summary>Restores the last carve's heights, then carves the current curve; false when there was no carve.</summary>
    public bool Recarve()
    {
        var terrain = ResolveTerrain();
        if (terrain?.Data is not { } data || data.GetCarveRecord(CarveId) is null)
            return false;
        return CarveInto(terrain);
    }

    /// <summary>Restores the heights the last carve replaced (one edit) and forgets the carve; false when there was none.</summary>
    public bool Uncarve()
    {
        var terrain = ResolveTerrain();
        if (terrain?.Data is not { } data || data.GetCarveRecord(CarveId) is not { } record)
            return false;
        RecordEdit(terrain, () =>
        {
            data.SetHeights(record.Rect, record.Heights);
            data.SetCarveRecord(CarveId, null);
        });
        CarveId = "";
        return true;
    }

    /// <summary>
    /// Moves every curve point's height to the terrain under it minus <paramref name="depthBelow"/> metres (the water
    /// surface sits in the ground, so a carve makes a channel), in one curve change. For code-made streams: call it
    /// before <see cref="Carve"/> (it samples the current, possibly carved, ground). False without a terrain or curve.
    /// </summary>
    public bool FitToTerrain(float depthBelow = 0.3f)
    {
        if (ResolveTerrain() is not { Data: not null } terrain || _curve is not { PointCount: > 0 } curve)
            return false;
        var global = GlobalTransform;
        var inverse = global.AffineInverse();
        var points = (float[])curve.Points.Clone();
        for (var i = 0; i < curve.PointCount; i++)
        {
            var world = global.TransformPoint(curve.GetPointPosition(i));
            var ground = terrain.HeightAt(world.X, world.Z) - depthBelow;
            var local = inverse.TransformPoint(new Vector3(world.X, ground, world.Z));
            var o = i * Curve3D.FloatsPerPoint;
            points[o] = local.X;
            points[o + 1] = local.Y;
            points[o + 2] = local.Z;
        }

        curve.Points = points;
        return true;
    }

    private bool CarveInto(Terrain3D? terrain)
    {
        if (terrain?.Data is not { } data)
            return false;
        EnsureGenerated();
        var count = _builder.SectionCount;
        if (count < 2)
            return false;

        // The sections in terrain-local space: the river's transform (translation, yaw) then the terrain's origin.
        var global = GlobalTransform;
        var origin = terrain.GlobalPosition;
        var centres = new Vector3[count];
        var source = _builder.SectionCentres;
        for (var k = 0; k < count; k++)
            centres[k] = global.TransformPoint(source[k]) - origin;
        var halfWidths = _builder.SectionHalfWidths;
        var depths = _builder.SectionDepths;
        if (!RiverCarver.ComputeRect(data, centres, halfWidths, BankWidth, out var rect))
            return false;

        var halfWidthArray = halfWidths.ToArray();
        var depthArray = depths.ToArray();
        var id = TerrainCarveRecord.IsValidId(CarveId) ? CarveId : NewCarveId();
        var previous = data.GetCarveRecord(id);
        RecordEdit(terrain, () =>
        {
            if (previous is not null)
                data.SetHeights(previous.Rect, previous.Heights); // back to the uncarved ground first
            var original = new float[rect.Area];
            data.GetHeights(rect, original);
            var carved = new float[rect.Area];
            RiverCarver.Carve(data.VertexSpacing, rect, original, carved, centres, halfWidthArray, depthArray, BankWidth, ShoreLift);
            data.SetHeights(rect, carved);
            data.SetCarveRecord(id, new TerrainCarveRecord(rect, original));
        });
        CarveId = id;
        return true;
    }

    // One undoable height edit (or part of the caller's, when one is already recording).
    private void RecordEdit(Terrain3D terrain, Action write)
    {
        var edit = terrain.Edit;
        if (edit.IsActive)
        {
            write();
            LastCarveEdit = null;
            return;
        }

        edit.Begin(TerrainLayers.Height);
        try
        {
            write();
        }
        finally
        {
            LastCarveEdit = edit.End();
        }
    }

    private static string NewCarveId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary><see cref="Terrain"/>, else <see cref="TerrainPath"/>, else the first terrain in the tree under the river's start.</summary>
    private Terrain3D? ResolveTerrain()
    {
        if (Terrain is { } explicitTerrain)
            return explicitTerrain;
        if (!TerrainPath.IsEmpty)
            return GetNodeOrNull<Terrain3D>(TerrainPath);
        Node root = this;
        while (root.Parent is { } parent)
            root = parent;
        var start = _curve is { PointCount: > 0 } curve ? GlobalTransform.TransformPoint(curve.GetPointPosition(0)) : GlobalPosition;
        return FindTerrain(root, start.X, start.Z);

        static Terrain3D? FindTerrain(Node node, float x, float z)
        {
            if (node is Terrain3D { Data: not null } terrain && terrain.Contains(x, z))
                return terrain;
            foreach (var child in node.Children)
                if (FindTerrain(child, x, z) is { } found)
                    return found;
            return null;
        }
    }

    private Material DefaultMaterial => _defaultMaterial ??= new WaterMaterial3D { ResourceName = "River (default)" };

    private void EnsureGenerated()
    {
        if (_dirty)
            Regenerate();
    }

    private void Invalidate()
    {
        _dirty = true;
        if (IsInsideTree && _ribbon is not null)
            Regenerate();
    }

    private void SetSetting(ref float field, float value)
    {
        if (field == value)
            return;
        field = value;
        Invalidate();
    }

    private void UpdateRibbon()
    {
        if (_ribbon is null)
            return;
        var data = _builder.Mesh;
        if (data.VertexCount == 0 || data.IndexCount == 0)
        {
            _ribbon.Mesh = null;
            return;
        }

        if (_surface is null || _mesh is null)
        {
            _surface = new MeshSurface();
            _mesh = new ArrayMesh();
            _mesh.AddSurface(_surface);
        }

        // The builder reuses its arrays while the section count holds: same arrays, so only notify.
        if (ReferenceEquals(_surface.Positions, data.Positions) && ReferenceEquals(_surface.Indices, data.Indices))
        {
            _surface.NotifyChanged();
        }
        else
        {
            _surface.Positions = data.Positions;
            _surface.Normals = data.Normals;
            _surface.UVs = data.UVs;
            _surface.Indices = data.Indices;
            _surface.Custom0 = data.Custom0; // column depth, flow, foam: WaterMaterial3D reads it
        }

        if (!ReferenceEquals(_ribbon.Mesh, _mesh))
            _ribbon.Mesh = _mesh;
    }
}
