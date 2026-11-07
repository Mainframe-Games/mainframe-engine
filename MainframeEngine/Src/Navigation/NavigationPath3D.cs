using System.Numerics;

namespace MainframeEngine;

/// <summary>A path query against a <see cref="NavigationMap3D"/> (G3.11).</summary>
/// <param name="Start">Where the path starts (metres).</param>
/// <param name="Target">Where it should end.</param>
/// <param name="NavigationLayers">The region layers the query may use.</param>
/// <param name="Postprocessing">How the cell path is simplified.</param>
/// <param name="AllowPartialPath">Return the path to the closest reachable point when the target cannot be reached.</param>
public readonly record struct NavigationPathQuery3D(Vector3 Start, Vector3 Target, uint NavigationLayers = 1,
    NavigationPathPostprocessing Postprocessing = NavigationPathPostprocessing.CorridorFunnel,
    bool AllowPartialPath = true);

/// <summary>A waypoint a <see cref="NavigationAgent3D"/> reached (G3.11).</summary>
/// <param name="Position">The waypoint (metres).</param>
/// <param name="Index">Its index in the agent's current path.</param>
/// <param name="Cell">The grid cell behind it (x, z).</param>
public readonly record struct NavigationWaypoint3D(Vector3 Position, int Index, Vector2I Cell);

/// <summary>
/// A reusable path result of <see cref="NavigationMap3D.QueryPath"/> (G3.11): the points (metres), the cell (x, z) and the
/// node behind each point. Buffers grow and are reused: a query that reuses it allocates nothing in steady state.
/// </summary>
public sealed class NavigationPath3D
{
    private readonly NavigationPath2D _flat; // the grid query on the XZ plane
    private Vector3[] _points;
    private int _count;

    public NavigationPath3D(int capacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        capacity = Math.Max(2, capacity);
        _flat = new NavigationPath2D(capacity);
        _points = new Vector3[capacity];
    }

    /// <summary>The path's points, from the start to the end.</summary>
    public ReadOnlySpan<Vector3> Points => _points.AsSpan(0, _count);

    /// <summary>The grid cells (x, z) behind <see cref="Points"/>.</summary>
    public ReadOnlySpan<Vector2I> Cells => _flat.Cells;

    /// <summary>The node behind each point (the <see cref="NavigationGrid3D"/>).</summary>
    public ReadOnlySpan<Node?> Owners => _flat.Owners;

    public int Count => _count;

    /// <summary>The query's outcome (<see cref="GridPathStatus.NotFound"/> before the first query).</summary>
    public GridPathStatus Status { get; internal set; } = GridPathStatus.NotFound;

    /// <summary>The grid region the path runs on (null when there was none).</summary>
    public NavigationGrid3D? Region { get; internal set; }

    /// <summary>The map iteration the path was computed against.</summary>
    public uint MapIteration { get; internal set; }

    public void Clear()
    {
        _flat.Clear();
        _count = 0;
        Status = GridPathStatus.NotFound;
        Region = null;
    }

    internal NavigationPath2D Flat => _flat;

    // Lifts the flat path onto the grid's plane at `height` (a grid region is flat: every point is on it).
    internal void Lift(float height)
    {
        var count = _flat.Count;
        if (count > _points.Length)
            Array.Resize(ref _points, Math.Max(count, _points.Length * 2));
        var flat = _flat.Points;
        for (var i = 0; i < count; i++)
            _points[i] = new Vector3(flat[i].X, height, flat[i].Y);
        _count = count;
    }
}
