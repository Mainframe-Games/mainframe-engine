using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.11: the navigation of one <see cref="World3D"/>: its <see cref="NavigationGrid3D"/> regions and
/// <see cref="NavigationAgent3D"/>s, with the 2D map's sync points (changes apply after each fixed step, or in the frame's
/// process without one; a sync that changed anything bumps <see cref="IterationId"/>). Grid queries run on the XZ plane
/// with the 2D map's grid code (<see cref="NavigationMap2D.QueryGrid"/>) and are lifted to the grid's height. Main thread.
/// </summary>
public sealed class NavigationMap3D
{
    private readonly List<NavigationGrid3D> _regions = [];
    private readonly List<NavigationGrid3D> _pendingAdd = [];
    private readonly List<NavigationGrid3D> _pendingRemove = [];
    private readonly List<NavigationAgent3D> _agents = [];
    private bool _dirty;

    internal NavigationMap3D(World3D world)
    {
        World = world;
    }

    public World3D World { get; }

    /// <summary>The viewport whose <see cref="SceneViewport.DebugLines"/> the debug draw writes into.</summary>
    public SceneViewport? Viewport { get; internal set; }

    /// <summary>Bumped by every sync that changed something.</summary>
    public uint IterationId { get; private set; }

    /// <summary>The grid regions in use, in registration order (changes appear after the next sync).</summary>
    public IReadOnlyList<NavigationGrid3D> Regions => _regions;

    /// <summary>The agents in the map's world.</summary>
    public IReadOnlyList<NavigationAgent3D> Agents => _agents;

    internal List<NavigationGrid3D> RegionList => _regions;

    internal List<NavigationAgent3D> AgentList => _agents;

    internal bool IsEmpty => _regions.Count == 0 && _pendingAdd.Count == 0 && _agents.Count == 0;

    /// <summary>Applies pending changes now (tools and tests; the server syncs every tick).</summary>
    public void ForceSync() => Sync();

    internal void MarkDirty() => _dirty = true;

    internal void AddRegion(NavigationGrid3D region)
    {
        _pendingRemove.Remove(region);
        if (!_regions.Contains(region) && !_pendingAdd.Contains(region))
            _pendingAdd.Add(region);
        _dirty = true;
    }

    internal void RemoveRegion(NavigationGrid3D region)
    {
        if (!_pendingAdd.Remove(region) && _regions.Contains(region) && !_pendingRemove.Contains(region))
            _pendingRemove.Add(region);
        _dirty = true;
    }

    internal void AddAgent(NavigationAgent3D agent)
    {
        if (!_agents.Contains(agent))
            _agents.Add(agent);
    }

    internal void RemoveAgent(NavigationAgent3D agent) => _agents.Remove(agent);

    internal void Sync()
    {
        // Grids follow their node's position: a move (also up or down) is polled here, so it applies in this sync.
        for (var i = 0; i < _regions.Count && !_dirty; i++)
        {
            var region = _regions[i];
            if (!region.IsInsideTree)
                continue;
            var p = region.GlobalPosition;
            if (region.LiveGrid.Offset != new Vector2(p.X, p.Z) || region.PlacedHeight != p.Y)
                _dirty = true;
        }

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
    /// fit). The region is the first (registration order) on the query's layers whose cells contain the start's XZ; the
    /// grid rules are the 2D map's (a start in a solid cell moves to the nearest walkable cell within two; a target outside
    /// or solid gives a partial path when allowed). Points lie on the grid's plane.
    /// </summary>
    public GridPathStatus QueryPath(in NavigationPathQuery3D query, NavigationPath3D result)
    {
        ArgumentNullException.ThrowIfNull(result);
        using var scope = NavigationMarkers.Query.Auto();
        result.Clear();
        result.MapIteration = IterationId;
        var region = FindRegion(query.Start, query.NavigationLayers);
        if (region is null)
            return result.Status = GridPathStatus.InvalidPoint;
        var grid = region.LiveGrid;
        var flat = result.Flat;
        var flatQuery = new NavigationPathQuery2D(new Vector2(query.Start.X, query.Start.Z), new Vector2(query.Target.X, query.Target.Z),
            query.NavigationLayers, query.Postprocessing, query.AllowPartialPath);
        var status = NavigationMap2D.QueryGrid(grid, flatQuery, flat, grid.Search);
        if (status is GridPathStatus.NotFound or GridPathStatus.InvalidPoint)
            return result.Status = status;
        var owners = flat.OwnerBuffer;
        for (var i = 0; i < flat.Count; i++)
            owners[i] = region;
        flat.Region = null;
        result.Lift(region.PlacedHeight);
        result.Region = region;
        return result.Status = status;
    }

    /// <summary>
    /// The closest point to <paramref name="point"/> an agent can stand on: on the grid plane under it when its cell is
    /// walkable, else the centre of the nearest walkable cell of a region on <paramref name="navigationLayers"/> (by 3D
    /// distance), else <paramref name="point"/>.
    /// </summary>
    public Vector3 GetClosestPoint(Vector3 point, uint navigationLayers = 1)
    {
        var best = point;
        var bestDistance = float.PositiveInfinity;
        for (var i = 0; i < _regions.Count; i++)
        {
            var region = _regions[i];
            if ((region.NavigationLayers & navigationLayers) == 0)
                continue;
            if (!NavigationMap2D.ClosestOnGrid(region.LiveGrid, new Vector2(point.X, point.Z), out var flat))
                continue;
            var candidate = new Vector3(flat.X, region.PlacedHeight, flat.Y);
            var d = Vector3.DistanceSquared(candidate, point);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = candidate;
            }
        }

        return best;
    }

    internal NavigationGrid3D? FindRegion(Vector3 position, uint layers)
    {
        for (var i = 0; i < _regions.Count; i++)
        {
            var region = _regions[i];
            if ((region.NavigationLayers & layers) == 0)
                continue;
            var grid = region.LiveGrid;
            if (!grid.IsDirty && grid.IsInBounds(grid.GetPointAt(new Vector2(position.X, position.Z))))
                return region;
        }

        return null;
    }
}
