using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// G3.9: a map's navigation mesh, rebuilt at a sync from its <see cref="NavigationRegion2D"/>s and
/// <see cref="NavigationLink2D"/>s. Polygons are put in global space with their vertices welded on the map's
/// <see cref="NavigationMap2D.CellSize"/> grid and wound counter-clockwise (maths sense), so two polygons with an edge on
/// the same two welded vertices are connected — inside a region and across regions. Edges of different regions that
/// still have no neighbour are connected where they run within <see cref="NavigationMap2D.EdgeConnectionMargin"/> of each
/// other (the overlap is the portal), and each link joins the polygons nearest its ends within
/// <see cref="NavigationMap2D.LinkConnectionRadius"/>. A uniform grid of polygon bounds answers point lookups. The build
/// allocates only when the mesh grows (main thread, at a sync); queries go through a <see cref="NavMeshSearch"/>.
/// </summary>
internal sealed class NavMesh2D
{
    internal struct Connection
    {
        public int To;        // the polygon entered
        public Vector2 Left;  // the portal as seen leaving `from` (links: the link's entry point twice)
        public Vector2 Right;
        public int Link;      // -1 for an edge, else the link index
        public bool Reverse;  // a bidirectional link crossed from its end to its start
    }

    private readonly List<NavigationRegion2D> _regionNodes = [];
    private readonly List<NavigationLink2D> _linkNodes = [];
    private readonly Dictionary<(long, long), int> _weld = [];
    private readonly Dictionary<long, int> _halfEdges = [];
    private readonly List<(int From, Connection C)> _pending = [];
    private readonly List<int> _free = [];  // edge ids (a polygon's first vertex slot + the edge) with no exact twin
    private readonly List<int> _scratch = [];
    private FreeEdge[] _sorted = new FreeEdge[64];
    private bool[] _joined = new bool[64];
    private bool _warnedInvalid;

    internal Vector2[] Vertices = new Vector2[64];
    internal int VertexCount;
    internal int[] PolyVerts = new int[256];   // vertex ids, polygon after polygon
    internal int[] PolyStart = new int[64];
    internal int[] EdgePoly = new int[256];    // vertex slot → its polygon (edge k of p is slot PolyStart[p] + k)
    internal int[] PolySize = new int[64];
    internal int[] PolyRegion = new int[64];    // index into Regions
    internal Vector2[] PolyMin = new Vector2[64];
    internal Vector2[] PolyMax = new Vector2[64];
    internal int PolyCount;
    internal int[] ConnStart = new int[65];
    internal Connection[] Conns = new Connection[256];
    internal int ConnCount;

    // Per region (index): layers, costs, node.
    internal uint[] RegionLayers = new uint[8];
    internal float[] RegionEnter = new float[8];
    internal float[] RegionTravel = new float[8];

    // Per link (index): ends (global), costs, layers, whether it is connected (both ends found a polygon).
    internal Vector2[] LinkStart = new Vector2[4];
    internal Vector2[] LinkEnd = new Vector2[4];
    internal float[] LinkEnter = new float[4];
    internal float[] LinkTravel = new float[4];
    internal uint[] LinkLayers = new uint[4];
    internal bool[] LinkConnected = new bool[4];

    // Debug draw: border edges (no neighbour) and edge connections (portals between regions).
    internal readonly List<(Vector2 A, Vector2 B)> BorderEdges = [];
    internal readonly List<(Vector2 A, Vector2 B)> EdgeConnections = [];

    // Lookup grid of polygon bounds.
    internal Vector2 GridOrigin;
    internal float GridCell = 1f;
    internal int GridWidth, GridHeight;
    internal int[] GridStart = new int[2];
    internal int[] GridItems = new int[64];

    public IReadOnlyList<NavigationRegion2D> Regions => _regionNodes;

    public IReadOnlyList<NavigationLink2D> Links => _linkNodes;

    /// <summary>Bumped by every build (search scratch re-sizes against it).</summary>
    public int Generation { get; private set; }

    public NavMeshSearch Search { get; } = new();

    public void Build(NavigationMap2D map, List<NavigationRegion2D> regions, List<NavigationLink2D> links)
    {
        Generation++;
        _regionNodes.Clear();
        _linkNodes.Clear();
        _weld.Clear();
        _halfEdges.Clear();
        _pending.Clear();
        _free.Clear();
        BorderEdges.Clear();
        EdgeConnections.Clear();
        VertexCount = 0;
        PolyCount = 0;
        ConnCount = 0;
        var cell = map.CellSize;
        var vertexCursor = 0;

        foreach (var region in regions)
        {
            if (!region.Enabled || !region.IsInsideTree || region.NavigationPolygon is not { } mesh)
                continue;
            var r = _regionNodes.Count;
            _regionNodes.Add(region);
            Grow(ref RegionLayers, r + 1);
            Grow(ref RegionEnter, r + 1);
            Grow(ref RegionTravel, r + 1);
            RegionLayers[r] = region.NavigationLayers;
            RegionEnter[r] = region.EnterCost;
            RegionTravel[r] = region.TravelCost;
            var transform = region.GlobalTransform;
            var source = mesh.Vertices;
            foreach (var polygon in mesh.Polygons)
            {
                if (polygon is not { Length: >= 3 })
                    continue;
                var start = vertexCursor;
                var valid = true;
                foreach (var index in polygon)
                {
                    if ((uint)index >= (uint)source.Length)
                    {
                        valid = false;
                        break;
                    }

                    var id = Weld(transform.TransformPoint(source[index]), cell);
                    if (vertexCursor > start && PolyVerts[vertexCursor - 1] == id)
                        continue;
                    Grow(ref PolyVerts, vertexCursor + 1);
                    PolyVerts[vertexCursor++] = id;
                }

                if (vertexCursor - start >= 2 && PolyVerts[vertexCursor - 1] == PolyVerts[start])
                    vertexCursor--;
                var size = vertexCursor - start;
                var area = valid && size >= 3 ? SignedArea(start, size) : 0f;
                if (!valid || area == 0f)
                {
                    if (!valid && !_warnedInvalid)
                    {
                        _warnedInvalid = true;
                        Log.Warning($"[Navigation] '{region.Name}' has a navigation polygon with a vertex index out of range: it is skipped.");
                    }

                    vertexCursor = start;
                    continue;
                }

                if (area < 0f)
                    Array.Reverse(PolyVerts, start, size);
                var p = PolyCount++;
                GrowPolys(PolyCount);
                PolyStart[p] = start;
                Grow(ref EdgePoly, vertexCursor);
                for (var k = start; k < vertexCursor; k++)
                    EdgePoly[k] = p;
                PolySize[p] = size;
                PolyRegion[p] = r;
                var min = new Vector2(float.PositiveInfinity);
                var max = new Vector2(float.NegativeInfinity);
                for (var k = 0; k < size; k++)
                {
                    var v = Vertices[PolyVerts[start + k]];
                    min = Vector2.Min(min, v);
                    max = Vector2.Max(max, v);
                }

                PolyMin[p] = min;
                PolyMax[p] = max;
            }
        }

        ConnectSharedEdges();
        if (map.UseEdgeConnections)
            ConnectNearEdges(map.EdgeConnectionMargin);
        CollectBorders();
        BuildGrid();
        Search.Reset(this);
        ConnectLinks(map, links);
        Compact();
    }

    // Exact twins: the half-edge u→v of one polygon and v→u of another.
    private void ConnectSharedEdges()
    {
        for (var p = 0; p < PolyCount; p++)
        {
            for (var k = 0; k < PolySize[p]; k++)
            {
                var (u, v) = Edge(p, k);
                var twin = Key(v, u);
                if (_halfEdges.TryGetValue(twin, out var other) && other >= 0)
                {
                    var q = EdgePoly[other];
                    _halfEdges[twin] = -1; // used: a third polygon on the same edge stays unconnected
                    _halfEdges[Key(u, v)] = -1;
                    _pending.Add((p, new Connection { To = q, Left = Vertices[v], Right = Vertices[u], Link = -1 }));
                    _pending.Add((q, new Connection { To = p, Left = Vertices[u], Right = Vertices[v], Link = -1 }));
                }
                else
                {
                    _halfEdges.TryAdd(Key(u, v), PolyStart[p] + k);
                }
            }
        }

        // What is left in the dictionary with an id is a border (or an edge-connection candidate), in a fixed order.
        for (var p = 0; p < PolyCount; p++)
        {
            for (var k = 0; k < PolySize[p]; k++)
            {
                var (u, v) = Edge(p, k);
                if (_halfEdges.TryGetValue(Key(u, v), out var id) && id == PolyStart[p] + k)
                    _free.Add(id);
            }
        }
    }

    // Godot's edge connection margin: free edges of different regions that run against each other within the margin.
    // A sweep over the edges sorted by their left end (then id: deterministic).
    private void ConnectNearEdges(float margin)
    {
        var n = _free.Count;
        if (n < 2)
            return;
        Grow(ref _sorted, n);
        Grow(ref _joined, n);
        for (var i = 0; i < n; i++)
            _sorted[i] = new FreeEdge(MinX(_free[i]), _free[i]);
        _sorted.AsSpan(0, n).Sort(); // IComparable: no comparer delegate
        Array.Clear(_joined, 0, n);
        var any = false;
        for (var i = 0; i < n; i++)
        {
            var e1 = _sorted[i].Id;
            var p = EdgePoly[e1];
            if (!_regionNodes[PolyRegion[p]].UseEdgeConnections)
                continue;
            var (u1, v1) = EdgeOf(e1);
            Vector2 a1 = Vertices[u1], b1 = Vertices[v1];
            var maxX = MathF.Max(a1.X, b1.X) + margin;
            for (var j = i + 1; j < n && _sorted[j].MinX <= maxX; j++)
            {
                var e2 = _sorted[j].Id;
                var q = EdgePoly[e2];
                if (PolyRegion[q] == PolyRegion[p] || !_regionNodes[PolyRegion[q]].UseEdgeConnections)
                    continue;
                var (u2, v2) = EdgeOf(e2);
                Vector2 a2 = Vertices[u2], b2 = Vertices[v2];
                if (MathF.Min(a2.Y, b2.Y) > MathF.Max(a1.Y, b1.Y) + margin || MathF.Max(a2.Y, b2.Y) < MathF.Min(a1.Y, b1.Y) - margin)
                    continue;
                if (!TryOverlap(a1, b1, a2, b2, margin, out var r1, out var l1, out var r2, out var l2))
                    continue;
                _pending.Add((p, new Connection { To = q, Left = l1, Right = r1, Link = -1 }));
                _pending.Add((q, new Connection { To = p, Left = l2, Right = r2, Link = -1 }));
                EdgeConnections.Add((r1, l1));
                _joined[i] = _joined[j] = true;
                any = true;
            }
        }

        if (!any)
            return;
        _free.Clear();
        for (var i = 0; i < n; i++)
            if (!_joined[i])
                _free.Add(_sorted[i].Id);
        _free.Sort(); // back to id order (the border list's order)
    }

    // Two opposite edges within `margin` of each other's lines: the overlap seen from each side (right end, left end).
    private static bool TryOverlap(Vector2 a1, Vector2 b1, Vector2 a2, Vector2 b2, float margin,
        out Vector2 right1, out Vector2 left1, out Vector2 right2, out Vector2 left2)
    {
        right1 = left1 = right2 = left2 = default;
        var d1 = b1 - a1;
        var d2 = b2 - a2;
        var len1 = d1.LengthSquared();
        var len2 = d2.LengthSquared();
        if (len1 <= 0f || len2 <= 0f || Vector2.Dot(d1, d2) >= 0f)
            return false;
        if (LineDistance(a1, d1, len1, a2) > margin || LineDistance(a1, d1, len1, b2) > margin
            || LineDistance(a2, d2, len2, a1) > margin || LineDistance(a2, d2, len2, b1) > margin)
            return false;
        var t0 = Math.Max(0f, Math.Min(Vector2.Dot(a2 - a1, d1), Vector2.Dot(b2 - a1, d1)) / len1);
        var t1 = Math.Min(1f, Math.Max(Vector2.Dot(a2 - a1, d1), Vector2.Dot(b2 - a1, d1)) / len1);
        if ((t1 - t0) * MathF.Sqrt(len1) <= 1e-3f)
            return false;
        right1 = a1 + d1 * t0; // edge 1 runs right → left as seen leaving its polygon
        left1 = a1 + d1 * t1;
        var s0 = Math.Clamp(Vector2.Dot(right1 - a2, d2) / len2, 0f, 1f);
        var s1 = Math.Clamp(Vector2.Dot(left1 - a2, d2) / len2, 0f, 1f);
        right2 = a2 + d2 * Math.Min(s0, s1);
        left2 = a2 + d2 * Math.Max(s0, s1);
        return true;
    }

    private static float LineDistance(Vector2 a, Vector2 d, float lengthSquared, Vector2 p)
    {
        var cross = d.X * (p.Y - a.Y) - d.Y * (p.X - a.X);
        return MathF.Abs(cross) / MathF.Sqrt(lengthSquared);
    }

    private void CollectBorders()
    {
        foreach (var id in _free)
        {
            var (u, v) = EdgeOf(id);
            BorderEdges.Add((Vertices[u], Vertices[v]));
        }
    }

    private void ConnectLinks(NavigationMap2D map, List<NavigationLink2D> links)
    {
        var radius = map.LinkConnectionRadius;
        foreach (var link in links)
        {
            if (!link.Enabled || !link.IsInsideTree)
                continue;
            var l = _linkNodes.Count;
            _linkNodes.Add(link);
            Grow(ref LinkStart, l + 1);
            Grow(ref LinkEnd, l + 1);
            Grow(ref LinkEnter, l + 1);
            Grow(ref LinkTravel, l + 1);
            Grow(ref LinkLayers, l + 1);
            Grow(ref LinkConnected, l + 1);
            var start = link.GetGlobalStartPosition();
            var end = link.GetGlobalEndPosition();
            LinkStart[l] = start;
            LinkEnd[l] = end;
            LinkEnter[l] = link.EnterCost;
            LinkTravel[l] = link.TravelCost;
            LinkLayers[l] = link.NavigationLayers;
            var from = Search.ClosestPolygon(this, start, uint.MaxValue, radius, out _);
            var to = Search.ClosestPolygon(this, end, uint.MaxValue, radius, out _);
            LinkConnected[l] = from >= 0 && to >= 0 && from != to;
            if (!LinkConnected[l])
                continue;
            _pending.Add((from, new Connection { To = to, Left = start, Right = start, Link = l }));
            if (link.Bidirectional)
                _pending.Add((to, new Connection { To = from, Left = end, Right = end, Link = l, Reverse = true }));
        }
    }

    // Pending connections → compressed rows per polygon (a stable counting sort: insertion order within a polygon).
    private void Compact()
    {
        Grow(ref ConnStart, PolyCount + 1);
        Array.Clear(ConnStart, 0, PolyCount + 1);
        foreach (var (from, _) in _pending)
            ConnStart[from + 1]++;
        for (var p = 0; p < PolyCount; p++)
            ConnStart[p + 1] += ConnStart[p];
        Grow(ref Conns, _pending.Count);
        _scratch.Clear();
        for (var p = 0; p < PolyCount; p++)
            _scratch.Add(ConnStart[p]);
        foreach (var (from, c) in _pending)
            Conns[_scratch[from]++] = c;
        ConnCount = _pending.Count;
    }

    private void BuildGrid()
    {
        if (PolyCount == 0)
        {
            GridWidth = GridHeight = 0;
            return;
        }

        var min = new Vector2(float.PositiveInfinity);
        var max = new Vector2(float.NegativeInfinity);
        float extent = 0f;
        for (var p = 0; p < PolyCount; p++)
        {
            min = Vector2.Min(min, PolyMin[p]);
            max = Vector2.Max(max, PolyMax[p]);
            var size = PolyMax[p] - PolyMin[p];
            extent += MathF.Max(size.X, size.Y);
        }

        // About one polygon per cell: cells the size of an average polygon.
        var cell = MathF.Max(extent / PolyCount, 1f);
        var span = max - min;
        while ((long)(span.X / cell + 1) * (long)(span.Y / cell + 1) > 1 << 20)
            cell *= 2f;
        GridOrigin = min;
        GridCell = cell;
        GridWidth = (int)(span.X / cell) + 1;
        GridHeight = (int)(span.Y / cell) + 1;
        var cells = GridWidth * GridHeight;
        Grow(ref GridStart, cells + 1);
        Array.Clear(GridStart, 0, cells + 1);
        var total = 0;
        for (var pass = 0; pass < 2; pass++)
        {
            if (pass == 1)
            {
                for (var c = 0; c < cells; c++)
                    GridStart[c + 1] += GridStart[c];
                total = GridStart[cells];
                Grow(ref GridItems, total);
                _scratch.Clear();
                for (var c = 0; c < cells; c++)
                    _scratch.Add(GridStart[c]);
            }

            for (var p = 0; p < PolyCount; p++)
            {
                var (x0, y0) = CellOf(PolyMin[p]);
                var (x1, y1) = CellOf(PolyMax[p]);
                for (var y = y0; y <= y1; y++)
                    for (var x = x0; x <= x1; x++)
                    {
                        var c = y * GridWidth + x;
                        if (pass == 0)
                            GridStart[c + 1]++;
                        else
                            GridItems[_scratch[c]++] = p;
                    }
            }
        }
    }

    internal (int X, int Y) CellOf(Vector2 point)
    {
        var x = (int)MathF.Floor((point.X - GridOrigin.X) / GridCell);
        var y = (int)MathF.Floor((point.Y - GridOrigin.Y) / GridCell);
        return (Math.Clamp(x, 0, GridWidth - 1), Math.Clamp(y, 0, GridHeight - 1));
    }

    internal (int U, int V) Edge(int poly, int k)
    {
        var start = PolyStart[poly];
        var size = PolySize[poly];
        return (PolyVerts[start + k], PolyVerts[start + (k + 1) % size]);
    }

    internal bool Contains(int poly, Vector2 point)
    {
        var start = PolyStart[poly];
        var size = PolySize[poly];
        var a = Vertices[PolyVerts[start + size - 1]];
        for (var k = 0; k < size; k++)
        {
            var b = Vertices[PolyVerts[start + k]];
            if ((b.X - a.X) * (point.Y - a.Y) - (b.Y - a.Y) * (point.X - a.X) < -1e-4f * Vector2.Distance(a, b))
                return false;
            a = b;
        }

        return true;
    }

    internal Vector2 ClosestPoint(int poly, Vector2 point)
    {
        if (Contains(poly, point))
            return point;
        var start = PolyStart[poly];
        var size = PolySize[poly];
        var best = point;
        var bestDistance = float.PositiveInfinity;
        var a = Vertices[PolyVerts[start + size - 1]];
        for (var k = 0; k < size; k++)
        {
            var b = Vertices[PolyVerts[start + k]];
            var c = ClosestOnSegment(point, a, b);
            var d = Vector2.DistanceSquared(c, point);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = c;
            }

            a = b;
        }

        return best;
    }

    internal static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var length = Vector2.Dot(ab, ab);
        if (length <= 0f)
            return a;
        var t = Math.Clamp(Vector2.Dot(p - a, ab) / length, 0f, 1f);
        return a + ab * t;
    }

    private (int U, int V) EdgeOf(int id)
    {
        var p = EdgePoly[id];
        return Edge(p, id - PolyStart[p]);
    }

    private float MinX(int id)
    {
        var (u, v) = EdgeOf(id);
        return MathF.Min(Vertices[u].X, Vertices[v].X);
    }

    private int Weld(Vector2 point, float cell)
    {
        var key = ((long)MathF.Round(point.X / cell), (long)MathF.Round(point.Y / cell));
        if (_weld.TryGetValue(key, out var id))
            return id;
        id = VertexCount++;
        Grow(ref Vertices, VertexCount);
        Vertices[id] = new Vector2(key.Item1 * cell, key.Item2 * cell);
        _weld.Add(key, id);
        return id;
    }

    private float SignedArea(int start, int size)
    {
        var sum = 0f;
        var a = Vertices[PolyVerts[start + size - 1]];
        for (var k = 0; k < size; k++)
        {
            var b = Vertices[PolyVerts[start + k]];
            sum += a.X * b.Y - b.X * a.Y;
            a = b;
        }

        return sum;
    }

    private readonly record struct FreeEdge(float MinX, int Id) : IComparable<FreeEdge>
    {
        public int CompareTo(FreeEdge other)
        {
            var c = MinX.CompareTo(other.MinX);
            return c != 0 ? c : Id.CompareTo(other.Id);
        }
    }

    private static long Key(int u, int v) => (long)u << 32 | (uint)v;

    private void GrowPolys(int count)
    {
        if (count <= PolyStart.Length)
            return;
        var size = Math.Max(count, PolyStart.Length * 2);
        Array.Resize(ref PolyStart, size);
        Array.Resize(ref PolySize, size);
        Array.Resize(ref PolyRegion, size);
        Array.Resize(ref PolyMin, size);
        Array.Resize(ref PolyMax, size);
    }

    private static void Grow<T>(ref T[] array, int count)
    {
        if (count > array.Length)
            Array.Resize(ref array, Math.Max(count, array.Length * 2));
    }
}
