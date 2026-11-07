using System.Numerics;
using DotRecast.Core.Numerics;
using DotRecast.Detour;

namespace MainframeEngine;

/// <summary>
/// G3.12: a <see cref="NavigationRegion3D"/>'s runtime navigation mesh — its <see cref="NavigationMesh"/>'s Detour tile in
/// a <c>DtNavMesh</c> with a main-thread <c>DtNavMeshQuery</c> (DotRecast, ADR 0264) — placed by the region's global
/// transform (queries go to the region's local space and back). Loaded by the map at a sync; queries reuse their buffers
/// (DotRecast's span overloads): 0 bytes per query.
/// </summary>
internal sealed class DetourMesh3D
{
    private const int MaxPolys = 1024;
    private readonly long[] _path = new long[MaxPolys];
    private readonly DtStraightPath[] _straight = new DtStraightPath[MaxPolys];
    private readonly DtQueryDefaultFilter _filter = new();
    private DtNavMeshQuery? _query;
    private Transform3D _toLocal = Transform3D.Identity;
    private Transform3D _toGlobal = Transform3D.Identity;
    private RcVec3f _extents;

    public bool IsLoaded => _query is not null;

    public int Version { get; private set; } = -1;

    public Transform3D Placed { get; private set; }

    /// <summary>Loads <paramref name="mesh"/>'s tile (main thread, at a sync). False (and a reason) when it has none or it is corrupt.</summary>
    public bool Load(NavigationMesh mesh, in Transform3D global, out string? error)
    {
        Version = mesh.Version;
        Place(global);
        _query = null;
        var nvp = Math.Clamp(mesh.VerticesPerPolygon, 3, 6);
        if (RecastBaker.Read(mesh.DetourData, nvp, out error) is not { } data)
            return false;
        var nav = new DtNavMesh();
        if (nav.Init(data, nvp, 0).Failed())
        {
            error = "Detour could not load the tile";
            return false;
        }

        _query = new DtNavMeshQuery(nav);
        // Nearest-polygon searches look this far: two agent radii across, an agent height up and down.
        _extents = new RcVec3f(MathF.Max(mesh.AgentRadius * 2f, mesh.CellSize * 4f), MathF.Max(mesh.AgentHeight, mesh.CellHeight * 4f),
            MathF.Max(mesh.AgentRadius * 2f, mesh.CellSize * 4f));
        return true;
    }

    public void Unload()
    {
        _query = null;
        Version = -1;
    }

    public void Place(in Transform3D global)
    {
        Placed = global;
        _toGlobal = global;
        _toLocal = global.AffineInverse();
    }

    /// <summary>The closest point of the mesh to <paramref name="point"/> (global) within the search extents.</summary>
    public bool ClosestPoint(Vector3 point, out Vector3 closest, out long poly)
    {
        closest = point;
        poly = 0;
        if (_query is not { } query)
            return false;
        var local = ToRc(_toLocal.TransformPoint(point));
        if (query.FindNearestPoly(local, _extents, _filter, out poly, out var nearest, out _).Failed() || poly == 0)
            return false;
        closest = _toGlobal.TransformPoint(FromRc(nearest));
        return true;
    }

    /// <summary>
    /// A path from <paramref name="start"/> to <paramref name="target"/> (global) into <paramref name="result"/>: the start
    /// and target snap to the mesh within the search extents; an unreachable target, or one off the mesh, gives the path to
    /// the closest point (<see cref="GridPathStatus.Partial"/>), or <see cref="GridPathStatus.NotFound"/> without partial
    /// paths. <see cref="NavigationPathPostprocessing.CorridorFunnel"/> is Detour's straight path; the others add the
    /// crossing of every polygon edge.
    /// </summary>
    public GridPathStatus FindPath(Vector3 start, Vector3 target, bool allowPartial, NavigationPathPostprocessing post,
        NavigationPath3D result, Node owner)
    {
        if (_query is not { } query || !ClosestPoint(start, out _, out var startPoly))
            return GridPathStatus.InvalidPoint;
        var localStart = ToRc(_toLocal.TransformPoint(start));
        query.FindNearestPoly(localStart, _extents, _filter, out startPoly, out var from, out _);
        var localTarget = ToRc(_toLocal.TransformPoint(target));
        if (query.FindNearestPoly(localTarget, _extents, _filter, out var endPoly, out var to, out _).Failed() || endPoly == 0)
        {
            if (!allowPartial)
                return GridPathStatus.NotFound;
            endPoly = 0;
        }

        var partial = endPoly == 0 || Horizontal(FromRc(to), FromRc(localTarget)) > 0.05f;
        int count;
        if (endPoly == 0)
        {
            // The target is beyond the search extents: walk to the closest point of the start's island, towards it.
            _path[0] = startPoly;
            count = 1;
            query.ClosestPointOnPoly(startPoly, localTarget, out to, out _);
        }
        else
        {
            var status = query.FindPath(startPoly, endPoly, from, to, _filter, _path, out count, _path.Length);
            if (status.Failed() || count == 0)
                return GridPathStatus.NotFound;
            if (status.IsPartial() || _path[count - 1] != endPoly)
            {
                partial = true;
                query.ClosestPointOnPoly(_path[count - 1], to, out to, out _);
            }
        }

        if (partial && !allowPartial)
            return GridPathStatus.NotFound;
        var options = post == NavigationPathPostprocessing.CorridorFunnel ? 0 : DtStraightPathOptions.DT_STRAIGHTPATH_ALL_CROSSINGS;
        if (query.FindStraightPath(from, to, _path, count, _straight, out var corners, _straight.Length, options).Failed() || corners == 0)
            return GridPathStatus.NotFound;
        var points = result.MeshPoints(corners, owner);
        for (var i = 0; i < corners; i++)
            points[i] = _toGlobal.TransformPoint(FromRc(_straight[i].pos));
        return partial ? GridPathStatus.Partial : GridPathStatus.Found;
    }

    private static float Horizontal(Vector3 a, Vector3 b) => new Vector2(a.X - b.X, a.Z - b.Z).Length();

    private static RcVec3f ToRc(Vector3 v) => new(v.X, v.Y, v.Z);

    private static Vector3 FromRc(RcVec3f v) => new(v.X, v.Y, v.Z);
}
