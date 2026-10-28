using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The navigation of one <see cref="World2D"/> (Godot's navigation map): its <see cref="NavigationGrid2D"/> regions and
/// <see cref="NavigationAgent2D"/>s. Region changes (a node entering or leaving, a bake, a runtime cell edit, a move) are
/// queued and applied at the map's next sync — after each fixed step, or in the frame's process when no step ran (edit
/// mode, a paused tree) — so every query of a tick sees the same map whatever order the nodes ask in. A sync that changed
/// anything bumps <see cref="IterationId"/>, which makes agents repath. Main thread only.
/// </summary>
public sealed class NavigationMap2D
{
    private readonly List<NavigationGrid2D> _regions = [];
    private readonly List<NavigationGrid2D> _pendingAdd = [];
    private readonly List<NavigationGrid2D> _pendingRemove = [];
    private readonly List<NavigationAgent2D> _agents = [];
    private bool _dirty;

    internal NavigationMap2D(World2D world)
    {
        World = world;
    }

    public World2D World { get; }

    /// <summary>The viewport whose <see cref="SceneViewport.DebugLines"/> the debug draw writes into (the first region's or agent's).</summary>
    public SceneViewport? Viewport { get; internal set; }

    /// <summary>Bumped by every sync that changed something.</summary>
    public uint IterationId { get; private set; }

    /// <summary>The regions in use, in registration order (changes appear after the next sync).</summary>
    public IReadOnlyList<NavigationGrid2D> Regions => _regions;

    /// <summary>The agents in the map's world.</summary>
    public IReadOnlyList<NavigationAgent2D> Agents => _agents;

    internal List<NavigationGrid2D> RegionList => _regions;

    internal List<NavigationAgent2D> AgentList => _agents;

    internal bool IsEmpty => _regions.Count == 0 && _pendingAdd.Count == 0 && _agents.Count == 0;

    /// <summary>Applies pending region changes now (tools and tests; the server syncs every tick).</summary>
    public void ForceSync() => Sync();

    internal void MarkDirty() => _dirty = true;

    internal void AddRegion(NavigationGrid2D region)
    {
        _pendingRemove.Remove(region);
        if (!_regions.Contains(region) && !_pendingAdd.Contains(region))
            _pendingAdd.Add(region);
        _dirty = true;
    }

    internal void RemoveRegion(NavigationGrid2D region)
    {
        if (!_pendingAdd.Remove(region) && _regions.Contains(region) && !_pendingRemove.Contains(region))
            _pendingRemove.Add(region);
        _dirty = true;
    }

    internal void AddAgent(NavigationAgent2D agent)
    {
        if (!_agents.Contains(agent))
            _agents.Add(agent);
    }

    internal void RemoveAgent(NavigationAgent2D agent) => _agents.Remove(agent);

    internal void Sync()
    {
        if (!_dirty)
            return;
        using var scope = NavigationMarkers.Sync.Auto();
        _dirty = false;
        var changed = false;
        for (var i = 0; i < _pendingRemove.Count; i++)
        {
            var region = _pendingRemove[i];
            changed |= _regions.Remove(region);
            region.LeftMap(this);
        }

        _pendingRemove.Clear();
        for (var i = 0; i < _pendingAdd.Count; i++)
        {
            _regions.Add(_pendingAdd[i]);
            changed = true;
        }

        _pendingAdd.Clear();
        for (var i = 0; i < _regions.Count; i++)
            changed |= _regions[i].ApplyPending();
        if (changed)
            IterationId++;
    }

    /// <summary>
    /// Finds a path for <paramref name="query"/> into <paramref name="result"/> (reused: allocation-free once its buffers
    /// fit). The region is the first (in registration order) whose layers intersect the query's and that contains the
    /// start; a start in a solid cell moves to the nearest walkable cell within two cells. A target outside the region or
    /// in a solid cell gives the path to the closest reachable cell when <see cref="NavigationPathQuery2D.AllowPartialPath"/>.
    /// The first point is the start, the last the target when it was reached (else the last cell's centre).
    /// </summary>
    public GridPathStatus QueryPath(in NavigationPathQuery2D query, NavigationPath2D result)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var scope = NavigationMarkers.Query.Auto();
        result.Clear();
        result.MapIteration = IterationId;
        var region = FindRegion(query.Start, query.NavigationLayers);
        if (region is null)
            return result.Status = GridPathStatus.InvalidPoint;
        var grid = region.LiveGrid;
        var startCell = grid.GetPointAt(query.Start);
        var startMoved = false;
        if (grid.IsSolid(grid.Index(startCell.X, startCell.Y)))
        {
            if (!TryNearestWalkable(grid, startCell, 2, query.Start, out startCell))
                return result.Status = GridPathStatus.InvalidPoint;
            startMoved = true;
        }

        var targetCell = grid.GetPointAt(query.Target);
        var targetInside = grid.IsInBounds(targetCell);
        if (!targetInside)
        {
            if (!query.AllowPartialPath)
                return result.Status = GridPathStatus.NotFound;
            var r = grid.Region;
            targetCell = new Vector2I(Math.Clamp(targetCell.X, r.Position.X, r.End.X - 1), Math.Clamp(targetCell.Y, r.Position.Y, r.End.Y - 1));
        }

        GridPathStatus status;
        int count;
        while (true)
        {
            status = grid.GetIdPath(startCell, targetCell, result.RawCells, out count, query.AllowPartialPath);
            if (status != GridPathStatus.BufferTooSmall)
                break;
            result.EnsureCapacity(count);
        }

        if (status is GridPathStatus.NotFound or GridPathStatus.InvalidPoint)
            return result.Status = status;
        if (!targetInside)
            status = GridPathStatus.Partial;

        var raw = result.RawCells[..count];
        var cells = result.CellBuffer;
        var kept = query.Postprocessing switch
        {
            NavigationPathPostprocessing.None => CopyAll(raw, cells),
            NavigationPathPostprocessing.EdgeCentered => KeepCorners(raw, cells),
            _ => PullStrings(grid, raw, cells),
        };

        var points = result.PointBuffer;
        for (var i = 0; i < kept; i++)
            points[i] = grid.GetPointPosition(cells[i]);
        if (!startMoved)
            points[0] = query.Start;
        if (status == GridPathStatus.Found && targetInside && kept > 1)
            points[kept - 1] = query.Target;
        else if (status == GridPathStatus.Found && targetInside && kept == 1 && !startMoved)
        {
            // Start and target in the same cell: two points, so the agent still walks to the exact target.
            cells[1] = cells[0];
            points[1] = query.Target;
            kept = 2;
        }

        result.SetCount(kept);
        result.Region = region;
        return result.Status = status;
    }

    /// <summary>
    /// The closest point to <paramref name="point"/> an agent can stand on: the point itself in a walkable cell of a region
    /// on <paramref name="navigationLayers"/>, else the centre of the nearest walkable cell (within 16 cells of the point
    /// clamped into a region), else <paramref name="point"/>.
    /// </summary>
    public Vector2 GetClosestPoint(Vector2 point, uint navigationLayers = 1)
    {
        var best = point;
        var bestDistance = float.PositiveInfinity;
        for (var r0 = 0; r0 < _regions.Count; r0++)
        {
            var region = _regions[r0];
            if ((region.NavigationLayers & navigationLayers) == 0)
                continue;
            var grid = region.LiveGrid;
            var r = grid.Region;
            if (r.Size.X <= 0 || r.Size.Y <= 0)
                continue;
            var cell = grid.GetPointAt(point);
            var clamped = new Vector2I(Math.Clamp(cell.X, r.Position.X, r.End.X - 1), Math.Clamp(cell.Y, r.Position.Y, r.End.Y - 1));
            var walkable = !grid.IsSolid(grid.Index(clamped.X, clamped.Y));
            if (walkable && clamped == cell)
                return point;
            var found = clamped;
            if (!walkable && !TryNearestWalkable(grid, clamped, 16, point, out found))
                continue;
            var centre = grid.GetPointPosition(found);
            var d = Vector2.DistanceSquared(centre, point);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = centre;
            }
        }

        return best;
    }

    internal NavigationGrid2D? FindRegion(Vector2 position, uint layers)
    {
        for (var i = 0; i < _regions.Count; i++)
        {
            var region = _regions[i];
            if ((region.NavigationLayers & layers) == 0)
                continue;
            var grid = region.LiveGrid;
            if (grid.IsInBounds(grid.GetPointAt(position)))
                return region;
        }

        return null;
    }

    // Rings around cell, nearest cell centre to `point` first; ties by ring order (deterministic).
    private static bool TryNearestWalkable(AStarGrid2D grid, Vector2I cell, int radius, Vector2 point, out Vector2I found)
    {
        found = cell;
        for (var ring = 1; ring <= radius; ring++)
        {
            var best = float.PositiveInfinity;
            for (var y = cell.Y - ring; y <= cell.Y + ring; y++)
            {
                for (var x = cell.X - ring; x <= cell.X + ring; x++)
                {
                    if (Math.Max(Math.Abs(x - cell.X), Math.Abs(y - cell.Y)) != ring || !grid.IsWalkable(x, y))
                        continue;
                    var d = Vector2.DistanceSquared(grid.GetPointPosition(new Vector2I(x, y)), point);
                    if (d < best)
                    {
                        best = d;
                        found = new Vector2I(x, y);
                    }
                }
            }

            if (best < float.PositiveInfinity)
                return true;
        }

        return false;
    }

    private static int CopyAll(ReadOnlySpan<Vector2I> raw, Span<Vector2I> cells)
    {
        raw.CopyTo(cells);
        return raw.Length;
    }

    // EdgeCentered on grids: the first and last cells and every cell where the direction changes.
    private static int KeepCorners(ReadOnlySpan<Vector2I> raw, Span<Vector2I> cells)
    {
        var n = 0;
        cells[n++] = raw[0];
        for (var i = 1; i < raw.Length - 1; i++)
        {
            var a = raw[i] - raw[i - 1];
            var b = raw[i + 1] - raw[i];
            if (a != b)
                cells[n++] = raw[i];
        }

        if (raw.Length > 1)
            cells[n++] = raw[^1];
        return n;
    }

    // CorridorFunnel on grids (string pulling): from each kept cell, skip ahead to the farthest cell still in line of sight.
    private static int PullStrings(AStarGrid2D grid, ReadOnlySpan<Vector2I> raw, Span<Vector2I> cells)
    {
        var n = 0;
        var anchor = 0;
        cells[n++] = raw[0];
        var maxWeight = grid.HasWeights ? grid.WeightAt(Index(grid, raw[0])) : 1f;
        for (var i = 2; i < raw.Length; i++)
        {
            if (grid.HasWeights)
                maxWeight = MathF.Max(maxWeight, grid.WeightAt(Index(grid, raw[i - 1])));
            var limit = grid.HasWeights ? MathF.Max(maxWeight, grid.WeightAt(Index(grid, raw[i]))) : 1f;
            if (GridLineOfSight.IsClear(grid, raw[anchor], raw[i], limit))
                continue;
            anchor = i - 1;
            cells[n++] = raw[anchor];
            maxWeight = grid.HasWeights ? grid.WeightAt(Index(grid, raw[anchor])) : 1f;
        }

        if (raw.Length > 1)
            cells[n++] = raw[^1];
        return n;
    }

    private static int Index(AStarGrid2D grid, Vector2I cell) => grid.Index(cell.X, cell.Y);
}

/// <summary>
/// Line of sight between two cell centres on a grid: every cell the segment passes through is walkable (and no heavier
/// than a weight limit), and where it passes exactly through a cell corner the diagonal mode's rule holds
/// (<see cref="GridDiagonalMode.OnlyIfNoObstacles"/> and <see cref="GridDiagonalMode.Never"/>: both side cells walkable;
/// <see cref="GridDiagonalMode.AtLeastOneWalkable"/>: one). Integer arithmetic only, so it is exact and deterministic.
/// </summary>
internal static class GridLineOfSight
{
    public static bool IsClear(AStarGrid2D grid, Vector2I from, Vector2I to, float maxWeight = float.PositiveInfinity)
    {
        long dx = to.X - from.X, dy = to.Y - from.Y;
        long nx = Math.Abs(dx), ny = Math.Abs(dy);
        int sx = Math.Sign(dx), sy = Math.Sign(dy);
        int x = from.X, y = from.Y;
        long ix = 0, iy = 0;
        while (ix < nx || iy < ny)
        {
            // Compare (0.5 + ix) / nx with (0.5 + iy) / ny: which grid line the segment crosses next.
            var decision = (1 + 2 * ix) * ny - (1 + 2 * iy) * nx;
            if (decision == 0)
            {
                if (!CornerAllowed(grid, x, y, sx, sy))
                    return false;
                x += sx;
                y += sy;
                ix++;
                iy++;
            }
            else if (decision < 0)
            {
                x += sx;
                ix++;
            }
            else
            {
                y += sy;
                iy++;
            }

            if (!grid.IsWalkable(x, y) || grid.HasWeights && grid.WeightAt(grid.Index(x, y)) > maxWeight)
                return false;
        }

        return true;
    }

    private static bool CornerAllowed(AStarGrid2D grid, int x, int y, int sx, int sy)
    {
        var a = grid.IsWalkable(x + sx, y);
        var b = grid.IsWalkable(x, y + sy);
        return grid.DiagonalMode switch
        {
            GridDiagonalMode.Always => true,
            GridDiagonalMode.AtLeastOneWalkable => a || b,
            _ => a && b,
        };
    }
}
