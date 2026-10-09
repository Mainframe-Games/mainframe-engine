using System.Numerics;
using DrawingColor = System.Drawing.Color;

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
/// <para>Until <c>WaterMaterial3D</c> exists the ribbon renders with <see cref="Material"/>, or by default a blended
/// blue <see cref="StandardMaterial3D"/>. Carving, falls, pond joins and audio come later (docs/design/water.md).
/// Rivers are meant to be translated and turned about Y; queries assume no tilt or scale.</para>
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
    private StandardMaterial3D? _defaultMaterial;
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

    /// <summary>The ribbon's material (null: a blended blue <see cref="StandardMaterial3D"/> until WaterMaterial3D exists).</summary>
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

    private StandardMaterial3D DefaultMaterial => _defaultMaterial ??= new StandardMaterial3D
    {
        ResourceName = "River (default)",
        AlbedoColor = DrawingColor.FromArgb(153, 46, 112, 168),
        Transparency = AlphaMode.Blend,
        Specular = 1.5f,
        Shininess = 160f,
    };

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
        }

        // Custom0 (column depth, flow, foam) is wired to MeshSurface.Custom0 once that stream exists (G8a/A2).
        if (!ReferenceEquals(_ribbon.Mesh, _mesh))
            _ribbon.Mesh = _mesh;
    }
}
