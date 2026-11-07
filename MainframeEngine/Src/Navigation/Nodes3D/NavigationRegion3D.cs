using System.Diagnostics;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.12: a 3D navigation mesh region (Godot's <c>NavigationRegion3D</c>): puts its <see cref="NavigationMesh"/>, placed by
/// the node's global transform, on the <see cref="NavigationMap3D"/> of its viewport's <see cref="World3D"/>. Paths run
/// inside one region (regions are not joined to each other yet). <see cref="Bake"/> fills the mesh from the scene with
/// Recast (DotRecast). Any change — the mesh, a move, a setting — applies at the map's next sync and makes agents repath.
/// </summary>
[EditorIcon("vector-triangle", Family = EditorIconFamily.Space3D)]
public class NavigationRegion3D : Node3D
{
    private NavigationMesh? _mesh;
    private bool _enabled = true;
    private uint _layers = 1;
    private NavigationMap3D? _map;
    private bool _warnedLoad;
    private bool _settingsChanged; // Enabled or the layers: which queries use the region

    /// <summary>The navigation mesh (region-local metres).</summary>
    [Export]
    public NavigationMesh? NavigationMesh
    {
        get => _mesh;
        set
        {
            if (ReferenceEquals(value, _mesh))
                return;
            if (_mesh is not null && IsInsideTree)
                _mesh.Changed -= OnMeshChanged;
            _mesh = value;
            if (_mesh is not null && IsInsideTree)
                _mesh.Changed += OnMeshChanged;
            OnMeshChanged();
            NavigationMeshChanged?.Invoke();
        }
    }

    [Export]
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (value == _enabled)
                return;
            _enabled = value;
            _settingsChanged = true;
            _map?.MarkDirty();
        }
    }

    /// <summary>The navigation layers the region is on.</summary>
    [Export(Flags = true)]
    public uint NavigationLayers
    {
        get => _layers;
        set
        {
            if (value == _layers)
                return;
            _layers = value;
            _settingsChanged = true;
            _map?.MarkDirty();
        }
    }

    /// <summary>The navigation mesh resource was replaced.</summary>
    [Signal] public event Action? NavigationMeshChanged;

    /// <summary>A <see cref="Bake"/> finished (successfully or not).</summary>
    [Signal] public event Action? BakeFinished;

    public NavigationMap3D? Map => _map;

    /// <summary>How long the last bake took (milliseconds).</summary>
    public double LastBakeMilliseconds { get; private set; }

    internal DetourMesh3D Runtime { get; } = new();

    /// <summary>Bakes the navigation mesh from the scene (created when missing). Returns false (and logs) on failure.</summary>
    public bool Bake()
    {
        _mesh ??= NavigationMesh = new NavigationMesh();
        var baked = BakeData(out var error);
        if (baked is not { } data)
        {
            Log.Warning($"[Navigation] '{Name}' was not baked: {error}.");
            BakeFinished?.Invoke();
            return false;
        }

        _mesh!.SetBaked(data.Vertices, data.Polygons, data.DetourData);
        BakeFinished?.Invoke();
        return true;
    }

    /// <summary>What a bake would produce, without changing the resource (the editor's undoable bake).</summary>
    public (Vector3[] Vertices, int[][] Polygons, string DetourData)? BakeData(out string? error)
    {
        if (_mesh is null)
        {
            error = "the region has no navigation mesh";
            return null;
        }

        if (!IsInsideTree && _mesh.SourceGeometryMode != NavigationSourceGeometryMode.RootNodeChildren)
        {
            error = "the region is not in the tree (the group modes read the tree's groups)";
            return null;
        }

        using var scope = NavigationMarkers.Bake.Auto();
        var started = Stopwatch.GetTimestamp();
        var ok = RecastBaker.Bake(this, _mesh, out var vertices, out var polygons, out var detour, out error);
        LastBakeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return ok ? (vertices, polygons, detour) : null;
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (_mesh is not null)
            _mesh.Changed += OnMeshChanged;
        _map = NavigationServer3D.For(Tree!).MapOf(this);
        _map.AddMeshRegion(this);
    }

    protected override void OnExitTree()
    {
        if (_mesh is not null)
            _mesh.Changed -= OnMeshChanged;
        _map?.RemoveMeshRegion(this);
        _map = null;
        base.OnExitTree();
    }

    // The map's sync: reload the tile when the mesh changed, re-place it when the node moved. Returns whether anything changed.
    internal bool ApplyPending()
    {
        var transform = GlobalTransform;
        var changed = _settingsChanged;
        _settingsChanged = false;
        if (_mesh is null || !_mesh.IsBaked)
        {
            changed |= Runtime.IsLoaded;
            Runtime.Unload();
            Runtime.Place(transform);
            return changed;
        }

        if (Runtime.Version != _mesh.Version)
        {
            if (!Runtime.Load(_mesh, transform, out var error) && !_warnedLoad)
            {
                _warnedLoad = true;
                Log.Warning($"[Navigation] '{Name}' ignores its navigation mesh ({error}). Bake again.");
            }

            return true;
        }

        if (Runtime.Placed != transform)
        {
            Runtime.Place(transform);
            return true;
        }

        return changed;
    }

    private void OnMeshChanged() => _map?.MarkDirty();
}
