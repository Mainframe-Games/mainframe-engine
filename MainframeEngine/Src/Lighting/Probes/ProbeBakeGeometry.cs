using System.Numerics;

namespace MainframeEngine;

/// <summary>The closest solid surface a bake ray met (ADR 0170).</summary>
internal struct ProbeHit
{
    /// <summary>Distance along the ray (world metres).</summary>
    public float T;

    /// <summary>The surface normal facing the ray's origin side it was hit from (unit, world).</summary>
    public Vector3 Normal;

    /// <summary>Linear diffuse albedo.</summary>
    public Vector3 Albedo;

    /// <summary>The ray hit the surface's back (its origin is inside or below it): counts against the probe's validity.</summary>
    public bool BackFace;
}

/// <summary>
/// A bounding volume hierarchy over primitives with boxes (median split on the longest axis, ≤ 4 per leaf), stored flat:
/// node i has bounds <c>Min[i]</c>/<c>Max[i]</c>; an inner node's children are <c>Left[i]</c> and <c>Left[i] + 1</c>, a
/// leaf (<c>Count[i]</c> &gt; 0) holds <c>Order[Left[i] .. + Count[i]]</c>. Built once, read by many threads.
/// </summary>
internal sealed class ProbeBvh
{
    private const int LeafSize = 4;

    public readonly Vector3[] Min;
    public readonly Vector3[] Max;
    public readonly int[] Left;
    public readonly int[] Count;
    public readonly int[] Order;
    public readonly int NodeCount;

    public ProbeBvh(ReadOnlySpan<Vector3> primitiveMin, ReadOnlySpan<Vector3> primitiveMax)
    {
        var n = primitiveMin.Length;
        Order = new int[n];
        for (var i = 0; i < n; i++)
            Order[i] = i;
        var capacity = Math.Max(1, 2 * n);
        Min = new Vector3[capacity];
        Max = new Vector3[capacity];
        Left = new int[capacity];
        Count = new int[capacity];
        var centres = new float[n * 3];
        for (var i = 0; i < n; i++)
        {
            var c = (primitiveMin[i] + primitiveMax[i]) * 0.5f;
            centres[i * 3] = c.X;
            centres[i * 3 + 1] = c.Y;
            centres[i * 3 + 2] = c.Z;
        }

        var nodes = 1;
        if (n == 0)
        {
            Min[0] = Vector3.Zero;
            Max[0] = -Vector3.One; // empty: no ray enters
            NodeCount = 1;
            return;
        }

        var keys = new float[n];
        var stack = new Stack<(int Node, int Start, int Count)>();
        stack.Push((0, 0, n));
        var minArray = primitiveMin.ToArray();
        var maxArray = primitiveMax.ToArray();
        while (stack.Count > 0)
        {
            var (node, start, count) = stack.Pop();
            var bmin = new Vector3(float.MaxValue);
            var bmax = new Vector3(float.MinValue);
            var cmin = new Vector3(float.MaxValue);
            var cmax = new Vector3(float.MinValue);
            for (var i = start; i < start + count; i++)
            {
                var p = Order[i];
                bmin = Vector3.Min(bmin, minArray[p]);
                bmax = Vector3.Max(bmax, maxArray[p]);
                var c = new Vector3(centres[p * 3], centres[p * 3 + 1], centres[p * 3 + 2]);
                cmin = Vector3.Min(cmin, c);
                cmax = Vector3.Max(cmax, c);
            }

            Min[node] = bmin;
            Max[node] = bmax;
            var extent = cmax - cmin;
            if (count <= LeafSize || MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) <= 0f)
            {
                Left[node] = start;
                Count[node] = count;
                continue;
            }

            var axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            for (var i = 0; i < count; i++)
                keys[i] = centres[Order[start + i] * 3 + axis];
            var span = Order.AsSpan(start, count);
            var keySpan = keys.AsSpan(0, count);
            keySpan.Sort(span);
            var half = count / 2;
            var left = nodes;
            nodes += 2;
            Left[node] = left;
            Count[node] = 0;
            stack.Push((left + 1, start + half, count - half));
            stack.Push((left, start, half));
        }

        NodeCount = nodes;
    }

    /// <summary>Slab test: the entry distance of the ray (1 / direction <paramref name="inv"/>) into node <paramref name="node"/> before <paramref name="tMax"/>, or +∞.</summary>
    public float Enter(int node, Vector3 origin, Vector3 inv, float tMax)
    {
        var t0 = (Min[node] - origin) * inv;
        var t1 = (Max[node] - origin) * inv;
        var tn = Vector3.Min(t0, t1);
        var tf = Vector3.Max(t0, t1);
        var enter = MathF.Max(MathF.Max(tn.X, tn.Y), MathF.Max(tn.Z, 0f));
        var exit = MathF.Min(MathF.Min(tf.X, tf.Y), MathF.Min(tf.Z, tMax));
        return enter <= exit ? enter : float.PositiveInfinity;
    }
}

/// <summary>Triangles in local space with a BVH and an albedo per triangle (a static mesh, or a tree's bark without a skeleton).</summary>
internal sealed class ProbeTriangleMesh
{
    private readonly Vector3[] _a, _e1, _e2;
    private readonly Vector3[] _albedo;
    private readonly ProbeBvh _bvh;

    public ProbeTriangleMesh(List<Vector3> positions, List<Vector3> albedos)
    {
        var count = positions.Count / 3;
        _a = new Vector3[count];
        _e1 = new Vector3[count];
        _e2 = new Vector3[count];
        _albedo = albedos.ToArray();
        var min = new Vector3[count];
        var max = new Vector3[count];
        for (var i = 0; i < count; i++)
        {
            var a = positions[i * 3];
            var b = positions[i * 3 + 1];
            var c = positions[i * 3 + 2];
            _a[i] = a;
            _e1[i] = b - a;
            _e2[i] = c - a;
            min[i] = Vector3.Min(a, Vector3.Min(b, c));
            max[i] = Vector3.Max(a, Vector3.Max(b, c));
        }

        _bvh = new ProbeBvh(min, max);
        Bounds = count == 0 ? new Aabb(Vector3.Zero, Vector3.Zero) : new Aabb(_bvh.Min[0], _bvh.Max[0]);
    }

    public int TriangleCount => _a.Length;

    public Aabb Bounds { get; }

    /// <summary>
    /// The closest triangle (both sides) before <paramref name="best"/>.T in local space; fills the hit's distance,
    /// normal (towards the origin) and back-face flag (the ray met the side the winding's normal points away from).
    /// </summary>
    public bool Intersect(Vector3 origin, Vector3 dir, ref ProbeHit best)
    {
        if (_a.Length == 0)
            return false;
        var inv = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        var hit = false;
        var bvh = _bvh;
        while (top > 0)
        {
            var node = stack[--top];
            if (bvh.Enter(node, origin, inv, best.T) == float.PositiveInfinity)
                continue;
            var count = bvh.Count[node];
            if (count == 0)
            {
                var left = bvh.Left[node];
                if (top + 2 > stack.Length)
                    continue; // degenerate depth (never for median splits under 2^60 primitives)
                // Nearer child last, so it is popped first.
                var dl = bvh.Enter(left, origin, inv, best.T);
                var dr = bvh.Enter(left + 1, origin, inv, best.T);
                if (dl <= dr)
                {
                    if (dr != float.PositiveInfinity) stack[top++] = left + 1;
                    if (dl != float.PositiveInfinity) stack[top++] = left;
                }
                else
                {
                    if (dl != float.PositiveInfinity) stack[top++] = left;
                    if (dr != float.PositiveInfinity) stack[top++] = left + 1;
                }

                continue;
            }

            var first = bvh.Left[node];
            for (var k = first; k < first + count; k++)
            {
                var tri = bvh.Order[k];
                var e1 = _e1[tri];
                var e2 = _e2[tri];
                var p = Vector3.Cross(dir, e2);
                var det = Vector3.Dot(e1, p);
                if (MathF.Abs(det) < 1e-12f)
                    continue;
                var invDet = 1f / det;
                var s = origin - _a[tri];
                var u = Vector3.Dot(s, p) * invDet;
                if (u < 0f || u > 1f)
                    continue;
                var q = Vector3.Cross(s, e1);
                var v = Vector3.Dot(dir, q) * invDet;
                if (v < 0f || u + v > 1f)
                    continue;
                var t = Vector3.Dot(e2, q) * invDet;
                if (t <= 1e-4f || t >= best.T)
                    continue;
                var n = Vector3.Normalize(Vector3.Cross(e1, e2));
                var back = Vector3.Dot(n, dir) > 0f;
                best.T = t;
                best.Normal = back ? -n : n;
                best.BackFace = back;
                best.Albedo = _albedo[tri];
                hit = true;
            }
        }

        return hit;
    }

    /// <summary>True when any triangle lies on the segment (shadow rays).</summary>
    public bool Occluded(Vector3 origin, Vector3 dir, float tMax)
    {
        var hit = new ProbeHit { T = tMax };
        return Intersect(origin, dir, ref hit);
    }
}

/// <summary>A tree's branches as capsules (segments with a radius) in the tree's local space, with a BVH.</summary>
internal sealed class ProbeCapsules
{
    private readonly Vector3[] _a, _b;
    private readonly float[] _r;
    private readonly Vector3[] _albedo;
    private readonly ProbeBvh _bvh;

    /// <summary>Capsules (each with its albedo, or one for all from <see cref="Intersect"/>'s caller when <paramref name="albedos"/> is null).</summary>
    public ProbeCapsules(List<(Vector3 A, Vector3 B, float Radius)> capsules, List<Vector3>? albedos = null)
    {
        var n = capsules.Count;
        _albedo = albedos?.ToArray() ?? [];
        _a = new Vector3[n];
        _b = new Vector3[n];
        _r = new float[n];
        var min = new Vector3[n];
        var max = new Vector3[n];
        for (var i = 0; i < n; i++)
        {
            var (a, b, r) = capsules[i];
            _a[i] = a;
            _b[i] = b;
            _r[i] = r;
            min[i] = Vector3.Min(a, b) - new Vector3(r);
            max[i] = Vector3.Max(a, b) + new Vector3(r);
        }

        _bvh = new ProbeBvh(min, max);
        Bounds = n == 0 ? new Aabb(Vector3.Zero, Vector3.Zero) : new Aabb(_bvh.Min[0], _bvh.Max[0]);
    }

    public int Count => _a.Length;

    /// <summary>Capsule <paramref name="index"/>.</summary>
    public (Vector3 A, Vector3 B, float Radius) this[int index] => (_a[index], _b[index], _r[index]);

    public Aabb Bounds { get; }

    /// <summary>The closest capsule surface (with its own albedo) before <paramref name="best"/>.T.</summary>
    public bool Intersect(Vector3 origin, Vector3 dir, ref ProbeHit best) => Intersect(origin, dir, ref best, Vector3.Zero);

    /// <summary>The closest capsule surface before <paramref name="best"/>.T (local space); an origin inside a capsule hits its inside (a back face) at the exit.</summary>
    public bool Intersect(Vector3 origin, Vector3 dir, ref ProbeHit best, Vector3 albedo)
    {
        if (_a.Length == 0)
            return false;
        var inv = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        Span<int> stack = stackalloc int[64];
        var top = 0;
        stack[top++] = 0;
        var hit = false;
        var bvh = _bvh;
        while (top > 0)
        {
            var node = stack[--top];
            if (bvh.Enter(node, origin, inv, best.T) == float.PositiveInfinity)
                continue;
            var count = bvh.Count[node];
            if (count == 0)
            {
                if (top + 2 > stack.Length)
                    continue;
                var left = bvh.Left[node];
                var dl = bvh.Enter(left, origin, inv, best.T);
                var dr = bvh.Enter(left + 1, origin, inv, best.T);
                if (dl <= dr)
                {
                    if (dr != float.PositiveInfinity) stack[top++] = left + 1;
                    if (dl != float.PositiveInfinity) stack[top++] = left;
                }
                else
                {
                    if (dl != float.PositiveInfinity) stack[top++] = left;
                    if (dr != float.PositiveInfinity) stack[top++] = left + 1;
                }

                continue;
            }

            var first = bvh.Left[node];
            for (var k = first; k < first + count; k++)
            {
                var c = bvh.Order[k];
                if (CapsuleIntersect(origin, dir, _a[c], _b[c], _r[c], out var t, out var inside) && t < best.T)
                {
                    var p = origin + dir * t;
                    var n = CapsuleNormal(p, _a[c], _b[c], _r[c]);
                    best.T = t;
                    best.BackFace = inside;
                    best.Normal = inside ? -n : n;
                    best.Albedo = _albedo.Length > 0 ? _albedo[c] : albedo;
                    hit = true;
                }
            }
        }

        return hit;
    }

    // Ray–capsule (Íñigo Quílez's capIntersect): the entry distance, or with the origin inside, the exit.
    internal static bool CapsuleIntersect(Vector3 ro, Vector3 rd, Vector3 pa, Vector3 pb, float r, out float t, out bool inside)
    {
        t = 0f;
        inside = false;
        var ba = pb - pa;
        var oa = ro - pa;
        var baba = Vector3.Dot(ba, ba);
        var bard = Vector3.Dot(ba, rd);
        var baoa = Vector3.Dot(ba, oa);
        var rdoa = Vector3.Dot(rd, oa);
        var oaoa = Vector3.Dot(oa, oa);

        // Inside test: distance from the origin to the segment.
        var h0 = baba > 0f ? Math.Clamp(baoa / baba, 0f, 1f) : 0f;
        var d0 = oa - ba * h0;
        if (Vector3.Dot(d0, d0) < r * r)
        {
            inside = true;
            // Exit: march the far root of the cylinder or the caps. Approximate with the farther sphere/cylinder root.
            t = ExitDistance(ro, rd, pa, pb, r);
            return t > 0f;
        }

        var a = baba - bard * bard;
        var b = baba * rdoa - baoa * bard;
        var c = baba * oaoa - baoa * baoa - r * r * baba;
        var h = b * b - a * c;
        if (h >= 0f && a > 1e-12f)
        {
            var tc = (-b - MathF.Sqrt(h)) / a;
            var y = baoa + tc * bard;
            if (y > 0f && y < baba && tc > 0f)
            {
                t = tc;
                return true;
            }
        }

        // Caps: the sphere at the end the cylinder test pointed at.
        var best = float.PositiveInfinity;
        SphereEntry(ro, rd, pa, r, ref best);
        SphereEntry(ro, rd, pb, r, ref best);
        if (best == float.PositiveInfinity)
            return false;
        t = best;
        return true;
    }

    private static void SphereEntry(Vector3 ro, Vector3 rd, Vector3 centre, float r, ref float best)
    {
        var oc = ro - centre;
        var b = Vector3.Dot(oc, rd);
        var c = Vector3.Dot(oc, oc) - r * r;
        var h = b * b - c;
        if (h < 0f)
            return;
        var t = -b - MathF.Sqrt(h);
        if (t > 0f && t < best)
            best = t;
    }

    private static float ExitDistance(Vector3 ro, Vector3 rd, Vector3 pa, Vector3 pb, float r)
    {
        // Step out until the point leaves the capsule (radii are centimetres to a metre: a few bisections suffice).
        float lo = 0f, hi = 2f * r + Vector3.Distance(pa, pb);
        for (var i = 0; i < 16; i++)
        {
            var mid = 0.5f * (lo + hi);
            if (DistanceToSegment(ro + rd * mid, pa, pb) < r)
                lo = mid;
            else
                hi = mid;
        }

        return hi;
    }

    private static float DistanceToSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var len2 = Vector3.Dot(ab, ab);
        var h = len2 > 0f ? Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        return Vector3.Distance(p, a + ab * h);
    }

    private static Vector3 CapsuleNormal(Vector3 p, Vector3 a, Vector3 b, float r)
    {
        var ab = b - a;
        var len2 = Vector3.Dot(ab, ab);
        var h = len2 > 0f ? Math.Clamp(Vector3.Dot(p - a, ab) / len2, 0f, 1f) : 0f;
        var n = p - (a + ab * h);
        var l = n.Length();
        return l > 1e-6f ? n / l : Vector3.UnitY;
    }
}

/// <summary>
/// The leaves of a tree as a leaf-area density grid in its local space (ADR 0170): per cell, one-sided leaf area × alpha
/// coverage / cell volume. Light through it falls off by Beer–Lambert with the extinction <c>G · density</c>
/// (<see cref="G"/> = 0.5: randomly oriented leaves). Scale-free: optical depth along a local segment is the same as along
/// the scaled world segment.
/// </summary>
internal sealed class ProbeLeafGrid
{
    /// <summary>The leaf projection function for randomly oriented leaves.</summary>
    public const float G = 0.5f;

    private readonly float[] _sigma; // extinction per local metre
    private readonly Vector3 _origin;
    private readonly float _cell;
    private readonly int _nx, _ny, _nz;

    /// <summary>
    /// Deposits the triangles' area (× <paramref name="coverage"/>) into cells of <paramref name="cellSize"/> metres: each
    /// triangle is sampled at points about every 10 cm so large cards spread over the cells they cover.
    /// </summary>
    public ProbeLeafGrid(ReadOnlySpan<Vector3> positions, ReadOnlySpan<int> indices, float coverage, float cellSize)
    {
        _cell = cellSize;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var i in indices)
        {
            min = Vector3.Min(min, positions[i]);
            max = Vector3.Max(max, positions[i]);
        }

        if (indices.Length == 0)
        {
            _sigma = [];
            Bounds = new Aabb(Vector3.Zero, Vector3.Zero);
            return;
        }

        _origin = min - new Vector3(cellSize * 0.5f);
        var size = max - min + new Vector3(cellSize);
        _nx = Math.Max(1, (int)MathF.Ceiling(size.X / cellSize));
        _ny = Math.Max(1, (int)MathF.Ceiling(size.Y / cellSize));
        _nz = Math.Max(1, (int)MathF.Ceiling(size.Z / cellSize));
        _sigma = new float[_nx * _ny * _nz];
        Bounds = new Aabb(_origin, _origin + new Vector3(_nx, _ny, _nz) * cellSize);
        var volume = cellSize * cellSize * cellSize;
        for (var t = 0; t + 2 < indices.Length; t += 3)
        {
            var a = positions[indices[t]];
            var b = positions[indices[t + 1]];
            var c = positions[indices[t + 2]];
            var area = 0.5f * Vector3.Cross(b - a, c - a).Length();
            if (!(area > 0f))
                continue;
            var steps = Math.Clamp((int)MathF.Ceiling(MathF.Sqrt(area) / 0.1f), 1, 16);
            var samples = steps * (steps + 1) / 2;
            var share = area * coverage / samples / volume * G;
            for (var i = 0; i < steps; i++)
            {
                for (var j = 0; j < steps - i; j++)
                {
                    var u = (i + 1f / 3f) / steps;
                    var v = (j + 1f / 3f) / steps;
                    var p = a + (b - a) * u + (c - a) * v;
                    var cell = CellOf(p);
                    if (cell >= 0)
                        _sigma[cell] += share;
                }
            }
        }
    }

    public Aabb Bounds { get; }

    public bool IsEmpty => _sigma.Length == 0;

    /// <summary>Total extinction × cell volume (tests: proportional to the leaf area).</summary>
    public float TotalExtinctionVolume
    {
        get
        {
            var sum = 0.0;
            foreach (var s in _sigma)
                sum += s;
            return (float)(sum * _cell * _cell * _cell);
        }
    }

    private int CellOf(Vector3 p)
    {
        var q = (p - _origin) / _cell;
        int x = (int)MathF.Floor(q.X), y = (int)MathF.Floor(q.Y), z = (int)MathF.Floor(q.Z);
        if ((uint)x >= (uint)_nx || (uint)y >= (uint)_ny || (uint)z >= (uint)_nz)
            return -1;
        return (z * _ny + y) * _nx + x;
    }

    /// <summary>
    /// Marches the ray (local space, unit <paramref name="dir"/>) through the grid between <paramref name="t0"/> and
    /// <paramref name="t1"/>, adding the optical depth to <paramref name="tau"/>. When the depth first passes
    /// <paramref name="eventTau"/>, <paramref name="eventT"/> gets the distance (a leaf scatters the ray there) unless it
    /// is already set (≥ 0).
    /// </summary>
    public void March(Vector3 origin, Vector3 dir, float t0, float t1, ref float tau, float eventTau, ref float eventT,
        float tauLimit = float.PositiveInfinity)
    {
        if (_sigma.Length == 0 || !(t1 > t0))
            return;
        // Clip to the grid's box.
        var inv = new Vector3(1f / dir.X, 1f / dir.Y, 1f / dir.Z);
        var a = (Bounds.Min - origin) * inv;
        var b = (Bounds.Max - origin) * inv;
        var tn = Vector3.Min(a, b);
        var tf = Vector3.Max(a, b);
        var enter = MathF.Max(MathF.Max(tn.X, tn.Y), MathF.Max(tn.Z, t0));
        var exit = MathF.Min(MathF.Min(tf.X, tf.Y), MathF.Min(tf.Z, t1));
        if (!(enter < exit))
            return;

        var p = (origin + dir * enter - _origin) / _cell;
        int x = Math.Clamp((int)MathF.Floor(p.X), 0, _nx - 1);
        int y = Math.Clamp((int)MathF.Floor(p.Y), 0, _ny - 1);
        int z = Math.Clamp((int)MathF.Floor(p.Z), 0, _nz - 1);
        int sx = dir.X > 0f ? 1 : -1, sy = dir.Y > 0f ? 1 : -1, sz = dir.Z > 0f ? 1 : -1;
        var dx = MathF.Abs(_cell * inv.X);
        var dy = MathF.Abs(_cell * inv.Y);
        var dz = MathF.Abs(_cell * inv.Z);
        var nextX = dir.X != 0f ? (_origin.X + (x + (sx > 0 ? 1 : 0)) * _cell - origin.X) * inv.X : float.PositiveInfinity;
        var nextY = dir.Y != 0f ? (_origin.Y + (y + (sy > 0 ? 1 : 0)) * _cell - origin.Y) * inv.Y : float.PositiveInfinity;
        var nextZ = dir.Z != 0f ? (_origin.Z + (z + (sz > 0 ? 1 : 0)) * _cell - origin.Z) * inv.Z : float.PositiveInfinity;
        var t = enter;
        while (t < exit && tau < tauLimit)
        {
            var next = MathF.Min(nextX, MathF.Min(nextY, nextZ));
            var end = MathF.Min(next, exit);
            var sigma = _sigma[(z * _ny + y) * _nx + x];
            if (sigma > 0f)
            {
                var add = sigma * (end - t);
                if (eventT < 0f && tau + add >= eventTau)
                    eventT = t + (eventTau - tau) / sigma;
                tau += add;
            }

            t = end;
            if (next == nextX)
            {
                x += sx;
                nextX += dx;
                if ((uint)x >= (uint)_nx) break;
            }
            else if (next == nextY)
            {
                y += sy;
                nextY += dy;
                if ((uint)y >= (uint)_ny) break;
            }
            else
            {
                z += sz;
                nextZ += dz;
                if ((uint)z >= (uint)_nz) break;
            }
        }
    }
}
