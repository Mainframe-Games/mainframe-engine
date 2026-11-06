using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.9: the scratch of navmesh queries (one per thread that queries; the map's own serves the main thread). A* over the
/// polygons of a <see cref="NavMesh2D"/> with Godot's cost model — the distance between entry points (each the closest
/// point of the next portal to the previous entry) times the region's <see cref="NavigationRegion2D.TravelCost"/>, plus
/// <see cref="NavigationRegion2D.EnterCost"/> when the path enters another region, and a link's own costs — and the
/// straight-line distance to the end as the estimate. Ties break by the lower polygon index, so paths are the same on every
/// run. Then the corridor becomes points: the funnel algorithm per stretch between links (<see cref="NavigationPathPostprocessing.CorridorFunnel"/>),
/// portal midpoints (<see cref="NavigationPathPostprocessing.EdgeCentered"/>) or the entry points (<see cref="NavigationPathPostprocessing.None"/>).
/// Its arrays grow with the mesh and are reused: queries allocate nothing.
/// </summary>
internal sealed class NavMeshSearch
{
    private float[] _g = new float[64];
    private float[] _f = new float[64];
    private Vector2[] _entry = new Vector2[64];
    private int[] _parent = new int[64];
    private int[] _parentConn = new int[64];
    private uint[] _stamp = new uint[64];
    private uint[] _seen = new uint[64];
    private int[] _heap = new int[64];
    private int[] _heapIndex = new int[64]; // -1: not in the heap
    private int _heapCount;
    private uint _query;
    private uint _seenQuery;
    private int[] _corridor = new int[64];
    private Vector2[] _left = new Vector2[64];
    private Vector2[] _right = new Vector2[64];
    private Node?[] _owner = new Node?[64];

    public void Reset(NavMesh2D mesh)
    {
        var n = mesh.PolyCount;
        if (n <= _g.Length)
            return;
        var size = Math.Max(n, _g.Length * 2);
        _g = new float[size];
        _f = new float[size];
        _entry = new Vector2[size];
        _parent = new int[size];
        _parentConn = new int[size];
        _stamp = new uint[size];
        _seen = new uint[size];
        _heap = new int[size];
        _heapIndex = new int[size];
        _query = 0;
        _seenQuery = 0;
    }

    /// <summary>
    /// The polygon on <paramref name="layers"/> closest to <paramref name="point"/> (one containing it: distance 0; ties:
    /// the lower index) within <paramref name="maxDistance"/>, and the closest point on it; -1 when there is none.
    /// </summary>
    public int ClosestPolygon(NavMesh2D mesh, Vector2 point, uint layers, float maxDistance, out Vector2 closest)
    {
        closest = point;
        if (mesh.PolyCount == 0 || mesh.GridWidth == 0)
            return -1;
        Reset(mesh);
        if (++_seenQuery == 0)
        {
            Array.Clear(_seen);
            _seenQuery = 1;
        }

        var (cx, cy) = mesh.CellOf(point);
        var best = -1;
        var bestDistance = float.PositiveInfinity;
        var rings = Math.Max(mesh.GridWidth, mesh.GridHeight);
        for (var r = 0; r <= rings; r++)
        {
            if (best >= 0 && bestDistance <= (r - 1) * mesh.GridCell)
                break;
            if ((r - 1) * mesh.GridCell > maxDistance)
                break;
            for (var y = cy - r; y <= cy + r; y++)
            {
                if (y < 0 || y >= mesh.GridHeight)
                    continue;
                var full = y == cy - r || y == cy + r;
                for (var x = cx - r; x <= cx + r; x += full || r == 0 ? 1 : 2 * r)
                {
                    if (x < 0 || x >= mesh.GridWidth)
                        continue;
                    var c = y * mesh.GridWidth + x;
                    for (var i = mesh.GridStart[c]; i < mesh.GridStart[c + 1]; i++)
                    {
                        var p = mesh.GridItems[i];
                        if (_seen[p] == _seenQuery)
                            continue;
                        _seen[p] = _seenQuery;
                        if ((mesh.RegionLayers[mesh.PolyRegion[p]] & layers) == 0)
                            continue;
                        var q = mesh.ClosestPoint(p, point);
                        var d = Vector2.Distance(q, point);
                        if (d < bestDistance || d == bestDistance && p < best)
                        {
                            best = p;
                            bestDistance = d;
                            closest = q;
                        }
                    }
                }
            }
        }

        if (best < 0 || bestDistance > maxDistance)
        {
            closest = point;
            return -1;
        }

        return best;
    }

    public GridPathStatus FindPath(NavMesh2D mesh, in NavigationPathQuery2D query, NavigationPath2D result)
    {
        var layers = query.NavigationLayers;
        var startPoly = ClosestPolygon(mesh, query.Start, layers, float.PositiveInfinity, out var start);
        if (startPoly < 0)
            return GridPathStatus.InvalidPoint;
        var endPoly = ClosestPolygon(mesh, query.Target, layers, float.PositiveInfinity, out var end);
        if (endPoly < 0)
            return GridPathStatus.InvalidPoint;
        var targetOnMesh = Vector2.DistanceSquared(end, query.Target) <= 1e-6f;
        if (!targetOnMesh && !query.AllowPartialPath)
            return GridPathStatus.NotFound;

        if (++_query == 0)
        {
            Array.Clear(_stamp);
            _query = 1;
        }

        _heapCount = 0;
        Visit(startPoly, 0f, start, -1, -1, Vector2.Distance(start, end));
        var bestPartial = startPoly;
        var bestPartialDistance = Vector2.Distance(mesh.ClosestPoint(startPoly, query.Target), query.Target);
        var found = startPoly == endPoly;
        while (!found && _heapCount > 0)
        {
            var p = Pop();
            if (p == endPoly)
            {
                found = true;
                break;
            }

            var from = _entry[p];
            var travel = mesh.RegionTravel[mesh.PolyRegion[p]];
            for (var c = mesh.ConnStart[p]; c < mesh.ConnStart[p + 1]; c++)
            {
                ref readonly var conn = ref mesh.Conns[c];
                var q = conn.To;
                if ((mesh.RegionLayers[mesh.PolyRegion[q]] & layers) == 0)
                    continue;
                float cost;
                Vector2 entry;
                if (conn.Link >= 0)
                {
                    var l = conn.Link;
                    if ((mesh.LinkLayers[l] & layers) == 0)
                        continue;
                    var linkFrom = conn.Reverse ? mesh.LinkEnd[l] : mesh.LinkStart[l];
                    entry = conn.Reverse ? mesh.LinkStart[l] : mesh.LinkEnd[l];
                    cost = _g[p] + Vector2.Distance(from, linkFrom) * travel + mesh.LinkEnter[l]
                           + Vector2.Distance(linkFrom, entry) * mesh.LinkTravel[l];
                }
                else
                {
                    entry = NavMesh2D.ClosestOnSegment(from, conn.Right, conn.Left);
                    cost = _g[p] + Vector2.Distance(from, entry) * travel;
                    if (mesh.PolyRegion[q] != mesh.PolyRegion[p])
                        cost += mesh.RegionEnter[mesh.PolyRegion[q]];
                }

                if (_stamp[q] == _query)
                {
                    if (cost >= _g[q])
                        continue;
                }
                else
                {
                    var d = Vector2.Distance(mesh.ClosestPoint(q, query.Target), query.Target);
                    if (d < bestPartialDistance || d == bestPartialDistance && q < bestPartial)
                    {
                        bestPartial = q;
                        bestPartialDistance = d;
                    }
                }

                Visit(q, cost, entry, p, c, Vector2.Distance(entry, end));
            }
        }

        var status = GridPathStatus.Found;
        if (!found)
        {
            if (!query.AllowPartialPath)
                return GridPathStatus.NotFound;
            endPoly = bestPartial;
            end = mesh.ClosestPoint(endPoly, query.Target);
            status = GridPathStatus.Partial;
        }
        else if (!targetOnMesh)
        {
            status = GridPathStatus.Partial;
        }

        // The corridor: the connections from the start polygon to the end polygon.
        var length = 0;
        for (var p = endPoly; _parent[p] >= 0 && p != startPoly; p = _parent[p])
            length++;
        Grow(ref _corridor, length);
        var at = length;
        for (var p = endPoly; at > 0; p = _parent[p])
            _corridor[--at] = _parentConn[p];

        result.EnsureCapacity(2 * length + 2);
        var count = query.Postprocessing switch
        {
            NavigationPathPostprocessing.None => EntryPoints(mesh, length, start, startPoly, end, endPoly, result),
            NavigationPathPostprocessing.EdgeCentered => Midpoints(mesh, length, start, startPoly, end, endPoly, result),
            _ => Funnel(mesh, length, start, startPoly, end, endPoly, result),
        };
        result.SetCount(count);
        return status;
    }

    private static NavigationRegion2D RegionOf(NavMesh2D mesh, int poly) => mesh.Regions[mesh.PolyRegion[poly]];

    private int EntryPoints(NavMesh2D mesh, int length, Vector2 start, int startPoly, Vector2 end, int endPoly, NavigationPath2D result)
    {
        var points = result.PointBuffer;
        var owners = result.OwnerBuffer;
        var n = 0;
        points[n] = start;
        owners[n++] = RegionOf(mesh, startPoly);
        for (var i = 0; i < length; i++)
        {
            ref readonly var conn = ref mesh.Conns[_corridor[i]];
            if (conn.Link >= 0)
            {
                var link = mesh.Links[conn.Link];
                points[n] = conn.Reverse ? mesh.LinkEnd[conn.Link] : mesh.LinkStart[conn.Link];
                owners[n++] = link;
                points[n] = conn.Reverse ? mesh.LinkStart[conn.Link] : mesh.LinkEnd[conn.Link];
                owners[n++] = link;
            }
            else
            {
                points[n] = _entry[conn.To];
                owners[n++] = RegionOf(mesh, conn.To);
            }
        }

        return End(points, owners, n, end, RegionOf(mesh, endPoly));
    }

    private int Midpoints(NavMesh2D mesh, int length, Vector2 start, int startPoly, Vector2 end, int endPoly, NavigationPath2D result)
    {
        var points = result.PointBuffer;
        var owners = result.OwnerBuffer;
        var n = 0;
        points[n] = start;
        owners[n++] = RegionOf(mesh, startPoly);
        for (var i = 0; i < length; i++)
        {
            ref readonly var conn = ref mesh.Conns[_corridor[i]];
            if (conn.Link >= 0)
            {
                var link = mesh.Links[conn.Link];
                points[n] = conn.Reverse ? mesh.LinkEnd[conn.Link] : mesh.LinkStart[conn.Link];
                owners[n++] = link;
                points[n] = conn.Reverse ? mesh.LinkStart[conn.Link] : mesh.LinkEnd[conn.Link];
                owners[n++] = link;
            }
            else
            {
                points[n] = (conn.Left + conn.Right) * 0.5f;
                owners[n++] = RegionOf(mesh, conn.To);
            }
        }

        return End(points, owners, n, end, RegionOf(mesh, endPoly));
    }

    private static int End(Span<Vector2> points, Span<Node?> owners, int n, Vector2 end, Node owner)
    {
        if (n == 1 || points[n - 1] != end)
        {
            points[n] = end;
            owners[n++] = owner;
        }

        return n;
    }

    // The simple stupid funnel algorithm (Mononen) on each stretch of portals between links.
    private int Funnel(NavMesh2D mesh, int length, Vector2 start, int startPoly, Vector2 end, int endPoly, NavigationPath2D result)
    {
        var points = result.PointBuffer;
        var owners = result.OwnerBuffer;
        Grow(ref _left, length + 2);
        Grow(ref _right, length + 2);
        Grow(ref _owner, length + 2);
        var n = 0;
        points[n] = start;
        owners[n++] = RegionOf(mesh, startPoly);
        _left[0] = _right[0] = start;
        _owner[0] = owners[0];
        var portals = 1;
        for (var i = 0; i < length; i++)
        {
            ref readonly var conn = ref mesh.Conns[_corridor[i]];
            if (conn.Link < 0)
            {
                _left[portals] = conn.Left;
                _right[portals] = conn.Right;
                _owner[portals++] = RegionOf(mesh, conn.To);
                continue;
            }

            var link = mesh.Links[conn.Link];
            var linkFrom = conn.Reverse ? mesh.LinkEnd[conn.Link] : mesh.LinkStart[conn.Link];
            var linkTo = conn.Reverse ? mesh.LinkStart[conn.Link] : mesh.LinkEnd[conn.Link];
            _left[portals] = _right[portals] = linkFrom;
            _owner[portals++] = link;
            n = Pull(portals, points, owners, n);
            if (points[n - 1] != linkTo)
            {
                points[n] = linkTo;
                owners[n++] = link;
            }

            _left[0] = _right[0] = linkTo;
            _owner[0] = link;
            portals = 1;
        }

        _left[portals] = _right[portals] = end;
        _owner[portals++] = RegionOf(mesh, endPoly);
        n = Pull(portals, points, owners, n);
        if (n == 1)
        {
            points[n] = end;
            owners[n++] = RegionOf(mesh, endPoly);
        }

        return n;
    }

    // Funnels portals [0, count) (portal 0 is the stretch's start, the last its end), appending the corners and the end.
    private int Pull(int count, Span<Vector2> points, Span<Node?> owners, int n)
    {
        var apex = _left[0];
        var left = apex;
        var right = apex;
        int leftIndex = 0, rightIndex = 0;
        for (var i = 1; i < count; i++)
        {
            var l = _left[i];
            var r = _right[i];

            // The right side: tighten while the new right is not left of the left side; else the left corner is a turn.
            if (Cross(apex, right, r) >= 0f)
            {
                if (apex == right || Cross(apex, left, r) < 0f)
                {
                    right = r;
                    rightIndex = i;
                }
                else
                {
                    n = Emit(points, owners, n, left, _owner[leftIndex]);
                    apex = left;
                    right = left;
                    rightIndex = leftIndex;
                    i = leftIndex;
                    continue;
                }
            }

            if (Cross(apex, left, l) <= 0f)
            {
                if (apex == left || Cross(apex, right, l) > 0f)
                {
                    left = l;
                    leftIndex = i;
                }
                else
                {
                    n = Emit(points, owners, n, right, _owner[rightIndex]);
                    apex = right;
                    left = right;
                    leftIndex = rightIndex;
                    i = rightIndex;
                }
            }
        }

        return Emit(points, owners, n, _left[count - 1], _owner[count - 1]);
    }

    private static int Emit(Span<Vector2> points, Span<Node?> owners, int n, Vector2 point, Node? owner)
    {
        if (n > 0 && points[n - 1] == point)
            return n;
        points[n] = point;
        owners[n] = owner;
        return n + 1;
    }

    private static float Cross(Vector2 a, Vector2 b, Vector2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private void Visit(int poly, float g, Vector2 entry, int parent, int conn, float h)
    {
        _stamp[poly] = _query;
        _g[poly] = g;
        _entry[poly] = entry;
        _parent[poly] = parent;
        _parentConn[poly] = conn;
        _f[poly] = g + h;
        if (parent < 0 || _heapIndex[poly] < 0 || _heapIndex[poly] >= _heapCount || _heap[_heapIndex[poly]] != poly)
        {
            _heap[_heapCount] = poly;
            _heapIndex[poly] = _heapCount++;
        }

        Up(_heapIndex[poly]);
    }

    private int Pop()
    {
        var top = _heap[0];
        _heapIndex[top] = -1;
        var last = _heap[--_heapCount];
        if (_heapCount > 0)
        {
            _heap[0] = last;
            _heapIndex[last] = 0;
            Down(0);
        }

        return top;
    }

    private bool Less(int a, int b) => _f[a] < _f[b] || _f[a] == _f[b] && a < b;

    private void Up(int i)
    {
        var node = _heap[i];
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            var other = _heap[parent];
            if (!Less(node, other))
                break;
            _heap[i] = other;
            _heapIndex[other] = i;
            i = parent;
        }

        _heap[i] = node;
        _heapIndex[node] = i;
    }

    private void Down(int i)
    {
        var node = _heap[i];
        while (true)
        {
            var child = 2 * i + 1;
            if (child >= _heapCount)
                break;
            if (child + 1 < _heapCount && Less(_heap[child + 1], _heap[child]))
                child++;
            var other = _heap[child];
            if (!Less(other, node))
                break;
            _heap[i] = other;
            _heapIndex[other] = i;
            i = child;
        }

        _heap[i] = node;
        _heapIndex[node] = i;
    }

    private static void Grow<T>(ref T[] array, int count)
    {
        if (count > array.Length)
            Array.Resize(ref array, Math.Max(count, array.Length * 2));
    }
}
