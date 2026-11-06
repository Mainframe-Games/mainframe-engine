using System.Diagnostics;
using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.9: a navigation mesh region (Godot's <c>NavigationRegion2D</c>): puts its <see cref="NavigationPolygon"/>'s convex
/// polygons on the <see cref="NavigationMap2D"/> of its viewport's <see cref="World2D"/>, placed by the node's full global
/// transform (rotation and scale included). Polygons that share an edge are connected, inside this region and with other
/// regions; edges of different regions that run within the map's <see cref="NavigationMap2D.EdgeConnectionMargin"/> are
/// connected too (<see cref="UseEdgeConnections"/>). Any change — the mesh, a move, a setting — applies at the map's next
/// sync and makes agents repath. <see cref="Bake"/> fills the mesh from the scene (<see cref="NavigationPolygonBaker2D"/>).
/// </summary>
[EditorIcon("polygon", Family = EditorIconFamily.Space2D)]
public class NavigationRegion2D : Node2D
{
    private NavigationPolygon? _polygon;
    private bool _enabled = true;
    private uint _layers = 1;
    private float _enterCost;
    private float _travelCost = 1f;
    private bool _useEdgeConnections = true;
    private NavigationMap2D? _map;

    /// <summary>The navigation mesh (local pixels).</summary>
    [Export]
    public NavigationPolygon? NavigationPolygon
    {
        get => _polygon;
        set
        {
            if (ReferenceEquals(value, _polygon))
                return;
            if (_polygon is not null && IsInsideTree)
                _polygon.Changed -= OnPolygonChanged;
            _polygon = value;
            if (_polygon is not null && IsInsideTree)
                _polygon.Changed += OnPolygonChanged;
            MarkChanged();
            NavigationPolygonChanged?.Invoke();
        }
    }

    /// <summary>Whether the region's polygons are on the map.</summary>
    [Export]
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>The navigation layers the region is on (a query uses it when its layers intersect these).</summary>
    [Export(Flags = true)]
    public uint NavigationLayers
    {
        get => _layers;
        set => Set(ref _layers, value);
    }

    /// <summary>Added to a path's cost when it enters this region from another one (Godot's 0).</summary>
    [Export(Range = "0,100000,0.1")]
    public float EnterCost
    {
        get => _enterCost;
        set
        {
            if (!(value >= 0f) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), "The enter cost must be finite and not negative.");
            Set(ref _enterCost, value);
        }
    }

    /// <summary>Multiplies the distance travelled inside this region (Godot's 1; mud above 1, a road below).</summary>
    [Export(Range = "0,100000,0.01")]
    public float TravelCost
    {
        get => _travelCost;
        set
        {
            if (!(value >= 0f) || !float.IsFinite(value))
                throw new ArgumentOutOfRangeException(nameof(value), "The travel cost must be finite and not negative.");
            Set(ref _travelCost, value);
        }
    }

    /// <summary>Connect free edges to other regions' within the map's edge connection margin (Godot's on).</summary>
    [Export]
    public bool UseEdgeConnections
    {
        get => _useEdgeConnections;
        set => Set(ref _useEdgeConnections, value);
    }

    /// <summary>The navigation polygon resource was replaced.</summary>
    [Signal] public event Action? NavigationPolygonChanged;

    /// <summary>A <see cref="Bake"/> finished (successfully or not).</summary>
    [Signal] public event Action? BakeFinished;

    /// <summary>The map the region is on (null outside the tree).</summary>
    public NavigationMap2D? Map => _map;

    /// <summary>How long the last bake took (milliseconds).</summary>
    public double LastBakeMilliseconds { get; private set; }

    /// <summary>
    /// Bakes the navigation polygon from the scene (<see cref="NavigationPolygonBaker2D"/>: the outlines or baking
    /// rectangle, minus the parsed obstructions, offset by the agent radius) into <see cref="NavigationPolygon"/>, which
    /// is created when missing. Main thread. The group source modes need the tree; the default (the region's descendants)
    /// also bakes a scene being built in code. Returns false (and logs) on failure.
    /// </summary>
    public bool Bake()
    {
        _polygon ??= NavigationPolygon = new NavigationPolygon();
        var mesh = BakeData(out var error);
        if (mesh is null)
        {
            Log.Warning($"[Navigation] '{Name}' was not baked: {error}.");
            BakeFinished?.Invoke();
            return false;
        }

        _polygon!.SetMesh(mesh.Value.Vertices, mesh.Value.Polygons);
        BakeFinished?.Invoke();
        return true;
    }

    /// <summary>The mesh a bake would produce, without changing the resource (the editor's undoable bake).</summary>
    public (Vector2[] Vertices, int[][] Polygons)? BakeData(out string? error)
    {
        error = null;
        if (_polygon is null)
        {
            error = "the region has no navigation polygon";
            return null;
        }

        if (!IsInsideTree && _polygon.SourceGeometryMode != NavigationSourceGeometryMode.RootNodeChildren)
        {
            error = "the region is not in the tree (the group modes read the tree's groups)";
            return null;
        }

        using var scope = NavigationMarkers.Bake.Auto();
        var started = Stopwatch.GetTimestamp();
        var result = NavigationPolygonBaker2D.Bake(this, _polygon, out error);
        LastBakeMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return result;
    }

    // The global transform the map's mesh was built with (the map compares it at every sync: moves rebuild the mesh).
    internal Transform2D Placed { get; set; }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Placed = GlobalTransform;
        if (_polygon is not null)
            _polygon.Changed += OnPolygonChanged;
        _map = NavigationServer2D.For(Tree!).MapOf(this);
        _map.AddPolygonRegion(this);
    }

    protected override void OnExitTree()
    {
        if (_polygon is not null)
            _polygon.Changed -= OnPolygonChanged;
        _map?.RemovePolygonRegion(this);
        _map = null;
        base.OnExitTree();
    }

    private void OnPolygonChanged() => MarkChanged();

    private void MarkChanged() => _map?.MarkMeshDirty();

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        MarkChanged();
    }
}
