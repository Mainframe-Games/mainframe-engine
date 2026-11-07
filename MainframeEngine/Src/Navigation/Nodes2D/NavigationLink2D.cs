using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.9: a navigation link (Godot's <c>NavigationLink2D</c>): a connection between two points of the navigation mesh that
/// the polygons do not join — a jump, a ladder, a teleporter. Each end attaches to the polygon closest to it within the
/// map's <see cref="NavigationMap2D.LinkConnectionRadius"/>; a path through the link goes to <see cref="StartPosition"/>,
/// then straight to <see cref="EndPosition"/> (and back when <see cref="Bidirectional"/>). The link moves nothing: an agent
/// raises <see cref="NavigationAgent2D.LinkReached"/> at its entry, and the game moves the body across (a jump arc, a
/// teleport). Links join navigation meshes (<see cref="NavigationRegion2D"/>), not grids.
/// </summary>
[EditorIcon("link", Family = EditorIconFamily.Space2D)]
public class NavigationLink2D : Node2D
{
    private bool _enabled = true;
    private bool _bidirectional = true;
    private uint _layers = 1;
    private Vector2 _start;
    private Vector2 _end = new(0f, 100f);
    private float _enterCost;
    private float _travelCost = 1f;
    private NavigationMap2D? _map;

    [Export]
    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>Paths may also cross from the end to the start (Godot's on).</summary>
    [Export]
    public bool Bidirectional
    {
        get => _bidirectional;
        set => Set(ref _bidirectional, value);
    }

    /// <summary>The navigation layers the link is on (a query uses it when its layers intersect these).</summary>
    [Export(Flags = true)]
    public uint NavigationLayers
    {
        get => _layers;
        set => Set(ref _layers, value);
    }

    /// <summary>The entry (local pixels).</summary>
    [Export]
    public Vector2 StartPosition
    {
        get => _start;
        set => Set(ref _start, value);
    }

    /// <summary>The exit (local pixels; default 100 px below the start).</summary>
    [Export]
    public Vector2 EndPosition
    {
        get => _end;
        set => Set(ref _end, value);
    }

    /// <summary>Added to a path's cost when it takes the link (Godot's 0).</summary>
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

    /// <summary>Multiplies the link's length in the path cost (Godot's 1).</summary>
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

    /// <summary>The map the link is on (null outside the tree).</summary>
    public NavigationMap2D? Map => _map;

    public Vector2 GetGlobalStartPosition() => GlobalTransform.TransformPoint(_start);

    public Vector2 GetGlobalEndPosition() => GlobalTransform.TransformPoint(_end);

    public void SetGlobalStartPosition(Vector2 position) => StartPosition = GlobalTransform.AffineInverse().TransformPoint(position);

    public void SetGlobalEndPosition(Vector2 position) => EndPosition = GlobalTransform.AffineInverse().TransformPoint(position);

    /// <summary>Whether both ends found a polygon at the last map sync (a link off the mesh joins nothing).</summary>
    public bool IsConnected() => _map?.IsLinkConnected(this) ?? false;

    // The global transform the map's mesh was built with (the map compares it at every sync: moves rebuild the mesh).
    internal Transform2D Placed { get; set; }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Placed = GlobalTransform;
        _map = NavigationServer2D.For(Tree!).MapOf(this);
        _map.AddLink(this);
    }

    protected override void OnExitTree()
    {
        _map?.RemoveLink(this);
        _map = null;
        base.OnExitTree();
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        _map?.MarkMeshDirty();
    }
}
