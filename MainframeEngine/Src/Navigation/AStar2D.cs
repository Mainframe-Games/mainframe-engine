using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Godot 4's point-graph A* (<c>AStar2D</c>): waypoints with ids, positions and weight scales, joined by one-way or
/// two-way connections. The step cost is the distance between the points times the entered point's weight scale; the
/// estimate is the distance to the target (override <see cref="ComputeCost"/> / <see cref="EstimateCost"/> to change
/// them). Adding points allocates; queries into spans do not.
/// </summary>
public class AStar2D : IPointCosts
{
    private readonly PointGraph _graph = new();
    private readonly bool _builtInCosts;

    public AStar2D()
    {
        _builtInCosts = GetType() == typeof(AStar2D);
    }

    /// <summary>Adds a point, or moves and re-weights an existing one.</summary>
    public void AddPoint(long id, Vector2 position, float weightScale = 1f) => _graph.Add(id, new Vector3(position, 0f), weightScale);

    public void RemovePoint(long id) => _graph.Remove(id);

    public bool HasPoint(long id) => _graph.Has(id);

    public int GetPointCount() => _graph.Count;

    /// <summary>The slots allocated (points can be added up to this without allocating).</summary>
    public int GetPointCapacity() => _graph.Capacity;

    /// <summary>The ids of every point (allocates: for tools and tests).</summary>
    public long[] GetPointIds() => [.. _graph.Ids];

    /// <summary>The lowest id not in use.</summary>
    public long GetAvailablePointId()
    {
        var id = 0L;
        while (_graph.Has(id))
            id++;
        return id;
    }

    public Vector2 GetPointPosition(long id) => AsVector2(_graph.Position(_graph.RequireSlot(id)));

    public void SetPointPosition(long id, Vector2 position) => _graph.SetPosition(_graph.RequireSlot(id), new Vector3(position, 0f));

    public float GetPointWeightScale(long id) => _graph.Weight(_graph.RequireSlot(id));

    public void SetPointWeightScale(long id, float weightScale) => _graph.SetWeight(_graph.RequireSlot(id), weightScale);

    /// <summary>A disabled point cannot be passed through (a closed door).</summary>
    public void SetPointDisabled(long id, bool disabled = true) => _graph.SetEnabled(_graph.RequireSlot(id), !disabled);

    public bool IsPointDisabled(long id) => !_graph.IsEnabled(_graph.RequireSlot(id));

    /// <summary>The ids <paramref name="id"/> connects to (allocates: for tools and tests).</summary>
    public long[] GetPointConnections(long id)
    {
        var links = _graph.Neighbours(_graph.RequireSlot(id));
        var ids = new long[links.Length];
        for (var i = 0; i < links.Length; i++)
            ids[i] = _graph.IdOf(links[i]);
        return ids;
    }

    public void ConnectPoints(long id, long toId, bool bidirectional = true) => _graph.Connect(id, toId, bidirectional);

    public void DisconnectPoints(long id, long toId, bool bidirectional = true) => _graph.Disconnect(id, toId, bidirectional);

    public bool ArePointsConnected(long id, long toId, bool bidirectional = true) => _graph.AreConnected(id, toId, bidirectional);

    /// <summary>The closest point (the lowest id among equally close ones), or -1 when there is none.</summary>
    public long GetClosestPoint(Vector2 position, bool includeDisabled = false) =>
        _graph.ClosestPoint(new Vector3(position, 0f), includeDisabled);

    /// <summary>The closest position on any connection between two enabled points (the position itself when there is none).</summary>
    public Vector2 GetClosestPositionInSegment(Vector2 position) => AsVector2(_graph.ClosestPositionInSegment(new Vector3(position, 0f)));

    /// <summary>Makes room for <paramref name="capacity"/> points (allocates once instead of growing).</summary>
    public void ReserveSpace(int capacity) => _graph.Reserve(capacity);

    public void Clear() => _graph.Clear();

    /// <summary>The ids of the cheapest path into <paramref name="path"/> (allocation-free); see <see cref="AStarGrid2D.GetIdPath(Vector2I, Vector2I, Span{Vector2I}, out int, bool)"/>.</summary>
    public GridPathStatus GetIdPath(long fromId, long toId, Span<long> path, out int count, bool allowPartialPath = false)
    {
        var status = Solve(fromId, toId, allowPartialPath, out var last, out count);
        if (count > path.Length)
            return GridPathStatus.BufferTooSmall;
        var i = count;
        for (var node = last; node >= 0; node = _graph.Search.Parent(node))
            path[--i] = _graph.IdOf(node);
        return status;
    }

    /// <summary>The positions of the cheapest path into <paramref name="path"/> (allocation-free).</summary>
    public GridPathStatus GetPointPath(long fromId, long toId, Span<Vector2> path, out int count, bool allowPartialPath = false)
    {
        var status = Solve(fromId, toId, allowPartialPath, out var last, out count);
        if (count > path.Length)
            return GridPathStatus.BufferTooSmall;
        var i = count;
        for (var node = last; node >= 0; node = _graph.Search.Parent(node))
            path[--i] = AsVector2(_graph.Position(node));
        return status;
    }

    /// <summary>The ids of the cheapest path as a new array (empty when there is none). Allocates.</summary>
    public long[] GetIdPath(long fromId, long toId, bool allowPartialPath = false)
    {
        Solve(fromId, toId, allowPartialPath, out var last, out var count);
        var path = new long[count];
        for (var node = last; node >= 0; node = _graph.Search.Parent(node))
            path[--count] = _graph.IdOf(node);
        return path;
    }

    /// <summary>The positions of the cheapest path as a new array. Allocates.</summary>
    public Vector2[] GetPointPath(long fromId, long toId, bool allowPartialPath = false)
    {
        Solve(fromId, toId, allowPartialPath, out var last, out var count);
        var path = new Vector2[count];
        for (var node = last; node >= 0; node = _graph.Search.Parent(node))
            path[--count] = AsVector2(_graph.Position(node));
        return path;
    }

    /// <summary>Godot's <c>_compute_cost</c>: the cost of moving between two connected points (before the weight scale).</summary>
    protected virtual float ComputeCost(long fromId, long toId) =>
        PointGraph.Distance(_graph.Position(_graph.RequireSlot(fromId)), _graph.Position(_graph.RequireSlot(toId)));

    /// <summary>Godot's <c>_estimate_cost</c>: the estimate from a point to the target.</summary>
    protected virtual float EstimateCost(long fromId, long endId) =>
        PointGraph.Distance(_graph.Position(_graph.RequireSlot(fromId)), _graph.Position(_graph.RequireSlot(endId)));

    float IPointCosts.Compute(int fromSlot, int toSlot) =>
        _builtInCosts ? PointGraph.Distance(_graph.Position(fromSlot), _graph.Position(toSlot)) : ComputeCost(_graph.IdOf(fromSlot), _graph.IdOf(toSlot));

    float IPointCosts.Estimate(int fromSlot, int endSlot) =>
        _builtInCosts ? PointGraph.Distance(_graph.Position(fromSlot), _graph.Position(endSlot)) : EstimateCost(_graph.IdOf(fromSlot), _graph.IdOf(endSlot));

    private GridPathStatus Solve(long fromId, long toId, bool allowPartial, out int last, out int count)
    {
        last = -1;
        count = 0;
        if (!_graph.Has(fromId) || !_graph.Has(toId))
            return GridPathStatus.InvalidPoint;
        var status = _graph.Solve(_graph.RequireSlot(fromId), _graph.RequireSlot(toId), allowPartial, this, out last);
        if (last >= 0)
            count = _graph.PathLength(last);
        else
            last = -1;
        return status;
    }

    private static Vector2 AsVector2(Vector3 v) => new(v.X, v.Y);
}
