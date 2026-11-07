using Clipper2Lib;

namespace MainframeEngine;

/// <summary>
/// G3.9: splits a polygon with holes into convex polygons, in exact integer arithmetic (the bake's Clipper2 grid). Holes
/// are bridged into the outer ring (Eberly, "Triangulation by Ear Clipping"), the ring is ear clipped, and the triangles
/// are merged greedily while the result stays convex (Hertel–Mehlhorn: at most four times the optimal count). Rings may
/// come in either winding; the output polygons are counter-clockwise in the maths sense (<c>cross &gt; 0</c> with y as
/// given, so clockwise on a y-down screen). Deterministic: no hashing order, no floating point in any decision except the
/// bridge's ray intersection, which only picks a candidate that exact tests then confirm.
/// </summary>
internal sealed class PolygonPartition
{
    private readonly List<Point64> _points = [];
    private readonly List<int> _vid = [];   // node → point index
    private readonly List<int> _prev = [];
    private readonly List<int> _next = [];
    private readonly List<int> _triangles = [];

    /// <summary>
    /// Partitions <paramref name="outer"/> minus <paramref name="holes"/> (holes inside the outer ring, not touching each
    /// other's interiors — Clipper2's output) into convex polygons, appended to <paramref name="polygons"/> as lists of
    /// indices into <paramref name="points"/>, which receives the vertices (shared by position through
    /// <paramref name="weld"/>).
    /// </summary>
    public void Partition(Path64 outer, IReadOnlyList<Path64> holes, List<Point64> points, Dictionary<Point64, int> weld,
        List<int[]> polygons)
    {
        _points.Clear();
        _vid.Clear();
        _prev.Clear();
        _next.Clear();
        _triangles.Clear();

        var start = AddRing(outer, clockwise: false);
        if (start < 0)
            return;
        if (holes.Count > 0)
            start = EliminateHoles(holes, start);
        ClipEars(start);

        // Triangles → welded point indices, then merge.
        var map = new int[_points.Count];
        for (var i = 0; i < _points.Count; i++)
        {
            if (!weld.TryGetValue(_points[i], out var id))
            {
                id = points.Count;
                points.Add(_points[i]);
                weld.Add(_points[i], id);
            }

            map[i] = id;
        }

        var tris = new List<List<int>>(_triangles.Count / 3);
        for (var i = 0; i < _triangles.Count; i += 3)
        {
            int a = map[_triangles[i]], b = map[_triangles[i + 1]], c = map[_triangles[i + 2]];
            if (a != b && b != c && c != a)
                tris.Add([a, b, c]);
        }

        Merge(tris, points);
        foreach (var polygon in tris)
            if (polygon.Count >= 3)
                polygons.Add(polygon.ToArray());
    }

    internal static long Cross(Point64 a, Point64 b, Point64 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    internal static long SignedArea2(Path64 ring)
    {
        long sum = 0;
        for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            sum += ring[j].X * ring[i].Y - ring[i].X * ring[j].Y;
        return sum;
    }

    // Adds a ring as linked nodes in the wanted winding (the outer counter-clockwise, holes clockwise); returns its first
    // node, or -1 for fewer than three points. Consecutive duplicates are skipped.
    private int AddRing(Path64 ring, bool clockwise)
    {
        if (ring.Count < 3)
            return -1;
        var area = SignedArea2(ring);
        if (area == 0)
            return -1;
        var reverse = (area > 0) == clockwise;
        var first = -1;
        var last = -1;
        for (var k = 0; k < ring.Count; k++)
        {
            var p = ring[reverse ? ring.Count - 1 - k : k];
            if (last >= 0 && _points[_vid[last]] == p)
                continue;
            var node = NewNode(p);
            if (first < 0)
                first = node;
            else
            {
                _next[last] = node;
                _prev[node] = last;
            }

            last = node;
        }

        if (last >= 0 && last != first && _points[_vid[last]] == _points[_vid[first]])
        {
            var before = _prev[last];
            _next[before] = first;
            _prev[first] = before;
        }
        else
        {
            _next[last] = first;
            _prev[first] = last;
        }

        return first;
    }

    private int NewNode(Point64 p)
    {
        _points.Add(p);
        var node = _vid.Count;
        _vid.Add(_points.Count - 1);
        _prev.Add(node);
        _next.Add(node);
        return node;
    }

    private int CopyNode(int node)
    {
        var copy = _vid.Count;
        _vid.Add(_vid[node]);
        _prev.Add(copy);
        _next.Add(copy);
        return copy;
    }

    private Point64 P(int node) => _points[_vid[node]];

    private int EliminateHoles(IReadOnlyList<Path64> holes, int outer)
    {
        var entries = new List<(long X, long Y, int Index, int Node)>(holes.Count);
        for (var h = 0; h < holes.Count; h++)
        {
            var node = AddRing(holes[h], clockwise: true);
            if (node < 0)
                continue;
            // The hole's rightmost vertex (lowest y among equals).
            var best = node;
            var n = _next[node];
            while (n != node)
            {
                var p = P(n);
                var b = P(best);
                if (p.X > b.X || p.X == b.X && p.Y < b.Y)
                    best = n;
                n = _next[n];
            }

            entries.Add((P(best).X, P(best).Y, h, best));
        }

        // Right to left, so a bridge never crosses a hole still to come.
        entries.Sort(static (a, b) => a.X != b.X ? b.X.CompareTo(a.X) : a.Y != b.Y ? a.Y.CompareTo(b.Y) : a.Index.CompareTo(b.Index));
        foreach (var entry in entries)
        {
            var bridge = FindBridge(entry.Node, outer);
            if (bridge < 0)
                continue; // a hole outside the outer ring (cannot happen with Clipper's output): dropped
            Split(bridge, entry.Node);
        }

        return outer;
    }

    // Eberly: cast a ray from the hole's rightmost vertex M to +x, take the nearest ring edge it hits (edges going up face
    // it: the ring is counter-clockwise, the interior on their left), that edge's endpoint P with the larger x, then the
    // reflex vertex inside triangle (M, I, P) at the smallest angle to the ray, if any (it blocks the view of P).
    private int FindBridge(int hole, int outer)
    {
        var m = P(hole);
        var p = outer;
        var qx = double.PositiveInfinity;
        var candidate = -1;
        do
        {
            var a = P(p);
            var b = P(_next[p]);
            if (a.Y <= m.Y && m.Y <= b.Y && a.Y != b.Y)
            {
                var x = a.X + (double)(m.Y - a.Y) * (b.X - a.X) / (b.Y - a.Y);
                if (x >= m.X && x < qx)
                {
                    qx = x;
                    candidate = a.X > b.X ? p : _next[p];
                    if (x == m.X)
                    {
                        // M lies on the edge: bridge to an endpoint at M's position, else the edge's right end.
                        if (a == m)
                            return p;
                        if (b == m)
                            return _next[p];
                        return candidate;
                    }
                }
            }

            p = _next[p];
        }
        while (p != outer);

        if (candidate < 0)
            return -1;

        var mp = P(candidate);
        var best = candidate;
        long bestDy = -1, bestDx = 1; // the best tan = dy / dx so far (-1: the candidate itself)
        p = outer;
        do
        {
            var q = P(p);
            if (p != candidate && q.X >= m.X && q.X <= mp.X && q != m && q != mp
                && InTriangle(m.X, m.Y, qx, m.Y, mp.X, mp.Y, q.X, q.Y) && IsReflex(p) && SectorContains(p, m))
            {
                long dy = Math.Abs(q.Y - m.Y), dx = q.X - m.X;
                if (dx > 0)
                {
                    var cmp = bestDy < 0 ? -1 : ((Int128)dy * bestDx).CompareTo((Int128)bestDy * dx);
                    if (cmp < 0 || cmp == 0 && q.X < P(best).X)
                    {
                        best = p;
                        bestDy = dy;
                        bestDx = dx;
                    }
                }
            }

            p = _next[p];
        }
        while (p != outer);

        return best;
    }

    // Inclusive point-in-triangle for either winding (the triangle's I corner is fractional).
    private static bool InTriangle(double ax, double ay, double bx, double by, double cx, double cy, double px, double py)
    {
        var d1 = (bx - ax) * (py - ay) - (by - ay) * (px - ax);
        var d2 = (cx - bx) * (py - by) - (cy - by) * (px - bx);
        var d3 = (ax - cx) * (py - cy) - (ay - cy) * (px - cx);
        return d1 >= 0 && d2 >= 0 && d3 >= 0 || d1 <= 0 && d2 <= 0 && d3 <= 0;
    }

    private bool IsReflex(int node) => Cross(P(_prev[node]), P(node), P(_next[node])) <= 0;

    // Whether the direction node → target lies inside the polygon's interior angle at node (counter-clockwise ring).
    private bool SectorContains(int node, Point64 target)
    {
        var a = P(node);
        var prev = P(_prev[node]);
        var next = P(_next[node]);
        if (Cross(prev, a, next) >= 0)
            return Cross(a, next, target) >= 0 && Cross(a, target, prev) >= 0; // convex: between next and prev
        return Cross(a, prev, target) < 0 || Cross(a, target, next) < 0;       // reflex: anything not in the outside wedge
    }

    // Links the outer node `a` to the hole node `b` with a two-way bridge (copies of both nodes close the ring).
    private void Split(int a, int b)
    {
        var a2 = CopyNode(a);
        var b2 = CopyNode(b);
        var an = _next[a];
        var bp = _prev[b];
        _next[a] = b;
        _prev[b] = a;
        _next[a2] = an;
        _prev[an] = a2;
        _next[b2] = a2;
        _prev[a2] = b2;
        _next[bp] = b2;
        _prev[b2] = bp;
    }

    private void ClipEars(int start)
    {
        var count = 1;
        for (var n = _next[start]; n != start; n = _next[n])
            count++;
        var ear = start;
        var stall = 0;
        var pass = 0;
        while (count > 3)
        {
            var a = _prev[ear];
            var c = _next[ear];
            if (pass == 0 ? IsEar(a, ear, c) : pass == 1 ? Cross(P(a), P(ear), P(c)) == 0 : Cross(P(a), P(ear), P(c)) > 0)
            {
                if (pass != 1)
                {
                    _triangles.Add(_vid[a]);
                    _triangles.Add(_vid[ear]);
                    _triangles.Add(_vid[c]);
                }

                _next[a] = c;
                _prev[c] = a;
                count--;
                ear = _next[c];
                stall = 0;
                pass = 0;
                continue;
            }

            ear = c;
            if (++stall >= count)
            {
                // A whole lap without an ear: drop collinear points, then (degenerate input) clip any convex corner.
                stall = 0;
                if (++pass > 2)
                    return;
            }
        }

        if (count == 3 && Cross(P(_prev[ear]), P(ear), P(_next[ear])) > 0)
        {
            _triangles.Add(_vid[_prev[ear]]);
            _triangles.Add(_vid[ear]);
            _triangles.Add(_vid[_next[ear]]);
        }
    }

    private bool IsEar(int a, int b, int c)
    {
        Point64 pa = P(a), pb = P(b), pc = P(c);
        if (Cross(pa, pb, pc) <= 0)
            return false;
        for (var p = _next[c]; p != a; p = _next[p])
        {
            var q = P(p);
            if (q == pa || q == pb || q == pc)
                continue;
            if (Cross(pa, pb, q) >= 0 && Cross(pb, pc, q) >= 0 && Cross(pc, pa, q) >= 0 && IsReflex(p))
                return false;
        }

        return true;
    }

    // Hertel–Mehlhorn: drop diagonals while both of their ends stay convex.
    private static void Merge(List<List<int>> polygons, List<Point64> points)
    {
        var owner = new Dictionary<(int, int), int>(polygons.Count * 3);
        for (var i = 0; i < polygons.Count; i++)
        {
            var poly = polygons[i];
            for (var k = 0; k < poly.Count; k++)
                owner[(poly[k], poly[(k + 1) % poly.Count])] = i;
        }

        var alive = new bool[polygons.Count];
        Array.Fill(alive, true);
        for (var i = 0; i < polygons.Count; i++)
        {
            var merged = true;
            while (merged && alive[i])
            {
                merged = false;
                var poly = polygons[i];
                for (var k = 0; k < poly.Count && !merged; k++)
                {
                    int u = poly[k], v = poly[(k + 1) % poly.Count];
                    if (!owner.TryGetValue((v, u), out var j) || j == i || !alive[j])
                        continue;
                    var other = polygons[j];
                    var combined = Combine(poly, k, other, u, v);
                    if (combined is null || !ConvexAt(combined, u, points) || !ConvexAt(combined, v, points))
                        continue;
                    owner.Remove((u, v));
                    owner.Remove((v, u));
                    for (var e = 0; e < other.Count; e++)
                    {
                        var key = (other[e], other[(e + 1) % other.Count]);
                        if (owner.TryGetValue(key, out var o) && o == j)
                            owner[key] = i;
                    }

                    alive[j] = false;
                    polygons[i] = combined;
                    merged = true;
                }
            }
        }

        var write = 0;
        for (var i = 0; i < polygons.Count; i++)
            if (alive[i])
                polygons[write++] = polygons[i];
        polygons.RemoveRange(write, polygons.Count - write);
    }

    // poly has edge u→v at k; other has v→u. The merged ring: v … (round poly) … u, then other's vertices after u up to v.
    private static List<int>? Combine(List<int> poly, int k, List<int> other, int u, int v)
    {
        var at = other.IndexOf(v);
        if (at < 0 || other[(at + 1) % other.Count] != u)
            return null;
        var result = new List<int>(poly.Count + other.Count - 2);
        var n = poly.Count;
        for (var s = 1; s <= n; s++)
            result.Add(poly[(k + s) % n]); // v, …, u
        var m = other.Count;
        for (var s = 2; s < m; s++)
            result.Add(other[(at + s) % m]); // after u, before v
        return result;
    }

    private static bool ConvexAt(List<int> ring, int vertex, List<Point64> points)
    {
        var i = ring.IndexOf(vertex);
        var n = ring.Count;
        return Cross(points[ring[(i + n - 1) % n]], points[vertex], points[ring[(i + 1) % n]]) >= 0;
    }
}
