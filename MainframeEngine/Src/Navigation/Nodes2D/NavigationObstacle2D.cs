using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Something avoiding <see cref="NavigationAgent2D"/>s steer around (Godot's <c>NavigationObstacle2D</c>, G3.7): a
/// <see cref="Radius"/> (a moving obstacle: agents predict it with <see cref="Velocity"/> and make way for it alone), a
/// polygon of <see cref="Vertices"/> (hard edges, placed by the global transform at every step: static or moved), or
/// both. It changes no path: cells stay walkable, so block grids with <see cref="NavigationGrid2D.SetCellSolid"/> too when
/// a path must go round it. Agents see it when its <see cref="AvoidanceLayers"/> meet their mask.
/// </summary>
[EditorIcon("polygon", Family = EditorIconFamily.Space2D)]
public class NavigationObstacle2D : Node2D
{
    private Vector2[] _vertices = [];
    private NavigationMap2D? _map;

    /// <summary>The obstacle's radius (pixels, unscaled); 0 = no radius part.</summary>
    [Export(Range = "0,1000,0.1")]
    public float Radius { get; set; }

    /// <summary>
    /// The polygon (local pixels; 2 vertices make a wall segment). Either winding: agents outside are pushed out, and an
    /// agent inside a polygon ignores it.
    /// </summary>
    [Export]
    public Vector2[] Vertices
    {
        get => _vertices;
        set => _vertices = value ?? [];
    }

    /// <summary>The radius part's velocity (pixels per second), which agents predict; set it every tick for a mover.</summary>
    [Export]
    public Vector2 Velocity { get; set; }

    /// <summary>Whether agents avoid it.</summary>
    [Export]
    public bool AvoidanceEnabled { get; set; } = true;

    /// <summary>The avoidance layers it is on: agents whose <see cref="NavigationAgent2D.AvoidanceMask"/> meets them avoid it.</summary>
    [Export(Flags = true)]
    public uint AvoidanceLayers { get; set; } = 1;

    /// <summary>The map it is registered with (its viewport's; null outside the tree).</summary>
    public NavigationMap2D? Map => _map;

    internal ReadOnlySpan<Vector2> VerticesSpan => _vertices;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _map = NavigationServer2D.For(Tree!).MapOf(this);
        _map.AddObstacle(this);
    }

    protected override void OnExitTree()
    {
        _map?.RemoveObstacle(this);
        _map = null;
        base.OnExitTree();
    }
}
