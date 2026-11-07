using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A reusable path result of <see cref="NavigationMap2D.QueryPath"/>: the points (pixels), the node each point belongs to
/// (<see cref="Owners"/>: the grid or navigation region, or a <see cref="NavigationLink2D"/>) and, on grid regions, the
/// cell behind each point. Its buffers grow when a longer path arrives and never shrink, so a query that reuses it allocates
/// nothing in steady state.
/// </summary>
public sealed class NavigationPath2D
{
    private Vector2[] _points;
    private Vector2I[] _cells;
    private Vector2I[] _rawCells; // the search's full cell path, before post-processing
    private Node?[] _owners;
    private int _count;

    public NavigationPath2D(int capacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        capacity = Math.Max(2, capacity);
        _points = new Vector2[capacity];
        _cells = new Vector2I[capacity];
        _rawCells = new Vector2I[capacity];
        _owners = new Node?[capacity];
    }

    /// <summary>The path's points, from the start to the end.</summary>
    public ReadOnlySpan<Vector2> Points => _points.AsSpan(0, _count);

    /// <summary>The cells behind <see cref="Points"/> (one per point) on grid regions.</summary>
    public ReadOnlySpan<Vector2I> Cells => _cells.AsSpan(0, _count);

    /// <summary>
    /// The node behind each point: the <see cref="NavigationGrid2D"/> or <see cref="NavigationRegion2D"/> it lies on, or
    /// the <see cref="NavigationLink2D"/> whose entry or exit it is (G3.9).
    /// </summary>
    public ReadOnlySpan<Node?> Owners => _owners.AsSpan(0, _count);

    public int Count => _count;

    /// <summary>The query's outcome (<see cref="GridPathStatus.NotFound"/> before the first query).</summary>
    public GridPathStatus Status { get; internal set; } = GridPathStatus.NotFound;

    /// <summary>The grid region the path runs on (null on navigation meshes, or when there was none).</summary>
    public NavigationGrid2D? Region { get; internal set; }

    /// <summary>The map iteration the path was computed against.</summary>
    public uint MapIteration { get; internal set; }

    public void Clear()
    {
        Array.Clear(_owners, 0, _count);
        _count = 0;
        Status = GridPathStatus.NotFound;
        Region = null;
    }

    internal Span<Vector2I> RawCells => _rawCells;

    internal void EnsureCapacity(int count)
    {
        if (count <= _points.Length)
            return;
        var size = Math.Max(count, _points.Length * 2);
        Array.Resize(ref _points, size);
        Array.Resize(ref _cells, size);
        Array.Resize(ref _owners, size);
        _rawCells = new Vector2I[size];
    }

    internal void SetCount(int count) => _count = count;

    internal Span<Vector2> PointBuffer => _points;

    internal Span<Vector2I> CellBuffer => _cells;

    internal Span<Node?> OwnerBuffer => _owners;
}
