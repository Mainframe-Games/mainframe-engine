using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The storage and search of <see cref="AStar2D"/> and <see cref="AStar3D"/> (Godot's <c>AStar</c> point graphs): a
/// dictionary from point id to a dense slot, slot arrays (position, weight, enabled, outgoing and incoming links) and the
/// shared <see cref="GridSearch"/> scratch. Positions are <see cref="Vector3"/> (2D points have z = 0, which leaves every
/// distance unchanged). Adding points allocates; queries do not.
/// </summary>
internal sealed class PointGraph
{
    private readonly Dictionary<long, int> _slots = [];
    private readonly Stack<int> _free = new();
    private long[] _ids = [];
    private Vector3[] _positions = [];
    private float[] _weights = [];
    private bool[] _alive = [];
    private bool[] _enabled = [];
    private int[][] _out = [];   // outgoing neighbours (slots) per slot
    private int[] _outCount = [];
    private int[][] _in = [];    // incoming one-way links, for removal (Godot's unlinked_neighbours)
    private int[] _inCount = [];
    private int _slotCount;
    private int _end;

    public readonly GridSearch Search = new();

    public int Count => _slots.Count;

    public int Capacity => _ids.Length;

    public IEnumerable<long> Ids => _slots.Keys;

    public bool Has(long id) => _slots.ContainsKey(id);

    public void Reserve(int capacity)
    {
        if (capacity <= _ids.Length)
            return;
        Array.Resize(ref _ids, capacity);
        Array.Resize(ref _positions, capacity);
        Array.Resize(ref _weights, capacity);
        Array.Resize(ref _alive, capacity);
        Array.Resize(ref _enabled, capacity);
        Array.Resize(ref _out, capacity);
        Array.Resize(ref _outCount, capacity);
        Array.Resize(ref _in, capacity);
        Array.Resize(ref _inCount, capacity);
        Search.EnsureCapacity(capacity);
    }

    public void Add(long id, Vector3 position, float weight)
    {
        if (id < 0)
            throw new ArgumentOutOfRangeException(nameof(id), "Point ids must not be negative.");
        RequireWeight(weight);
        if (_slots.TryGetValue(id, out var existing))
        {
            _positions[existing] = position;
            _weights[existing] = weight;
            return;
        }

        int slot;
        if (_free.Count > 0)
        {
            slot = _free.Pop();
        }
        else
        {
            if (_slotCount == _ids.Length)
                Reserve(Math.Max(16, _ids.Length * 2));
            slot = _slotCount++;
        }

        _slots.Add(id, slot);
        _ids[slot] = id;
        _positions[slot] = position;
        _weights[slot] = weight;
        _alive[slot] = true;
        _enabled[slot] = true;
        _outCount[slot] = 0;
        _inCount[slot] = 0;
        _out[slot] ??= new int[4];
        _in[slot] ??= new int[4];
    }

    public void Remove(long id)
    {
        var slot = RequireSlot(id);
        // Drop every link to and from the point.
        for (var i = 0; i < _outCount[slot]; i++)
        {
            var other = _out[slot][i];
            RemoveLink(_in, _inCount, other, slot);
            RemoveLink(_out, _outCount, other, slot); // a bidirectional link is stored both ways
        }

        for (var i = 0; i < _inCount[slot]; i++)
            RemoveLink(_out, _outCount, _in[slot][i], slot);
        _outCount[slot] = 0;
        _inCount[slot] = 0;
        _alive[slot] = false;
        _slots.Remove(id);
        _free.Push(slot);
    }

    public void Clear()
    {
        _slots.Clear();
        _free.Clear();
        _slotCount = 0;
        Array.Clear(_alive);
    }

    public int RequireSlot(long id) =>
        _slots.TryGetValue(id, out var slot) ? slot : throw new ArgumentException($"There is no point with id {id}.", nameof(id));

    public long IdOf(int slot) => _ids[slot];

    public Vector3 Position(int slot) => _positions[slot];

    public void SetPosition(int slot, Vector3 position) => _positions[slot] = position;

    public float Weight(int slot) => _weights[slot];

    public void SetWeight(int slot, float weight)
    {
        RequireWeight(weight);
        _weights[slot] = weight;
    }

    public bool IsEnabled(int slot) => _enabled[slot];

    public void SetEnabled(int slot, bool enabled) => _enabled[slot] = enabled;

    public ReadOnlySpan<int> Neighbours(int slot) => _out[slot].AsSpan(0, _outCount[slot]);

    public void Connect(long a, long b, bool bidirectional)
    {
        if (a == b)
            throw new ArgumentException("A point cannot connect to itself.", nameof(b));
        var sa = RequireSlot(a);
        var sb = RequireSlot(b);
        if (!Contains(_out, _outCount, sa, sb))
        {
            AddLink(_out, _outCount, sa, sb);
            if (!bidirectional)
                AddLink(_in, _inCount, sb, sa);
        }

        if (bidirectional)
        {
            RemoveLink(_in, _inCount, sb, sa);
            RemoveLink(_in, _inCount, sa, sb);
            if (!Contains(_out, _outCount, sb, sa))
                AddLink(_out, _outCount, sb, sa);
        }
    }

    public void Disconnect(long a, long b, bool bidirectional)
    {
        var sa = RequireSlot(a);
        var sb = RequireSlot(b);
        var hadAb = RemoveLink(_out, _outCount, sa, sb);
        if (bidirectional)
        {
            RemoveLink(_out, _outCount, sb, sa);
            RemoveLink(_in, _inCount, sa, sb);
            RemoveLink(_in, _inCount, sb, sa);
            return;
        }

        if (!hadAb)
            return;
        RemoveLink(_in, _inCount, sb, sa);
        // b → a stays; it was half of a bidirectional link, now a one-way one.
        if (Contains(_out, _outCount, sb, sa))
            AddLink(_in, _inCount, sa, sb);
    }

    public bool AreConnected(long a, long b, bool bidirectional)
    {
        var sa = RequireSlot(a);
        var sb = RequireSlot(b);
        return bidirectional
            ? Contains(_out, _outCount, sa, sb) || Contains(_out, _outCount, sb, sa)
            : Contains(_out, _outCount, sa, sb);
    }

    public long ClosestPoint(Vector3 position, bool includeDisabled)
    {
        var best = -1L;
        var bestDistance = float.PositiveInfinity;
        for (var slot = 0; slot < _slotCount; slot++)
        {
            if (!_alive[slot] || !includeDisabled && !_enabled[slot])
                continue;
            var d = Vector3.DistanceSquared(position, _positions[slot]);
            if (d < bestDistance || d == bestDistance && _ids[slot] < best)
            {
                bestDistance = d;
                best = _ids[slot];
            }
        }

        return best;
    }

    public Vector3 ClosestPositionInSegment(Vector3 position)
    {
        var bestDistance = float.PositiveInfinity;
        var best = position;
        var any = false;
        for (var slot = 0; slot < _slotCount; slot++)
        {
            if (!_alive[slot] || !_enabled[slot])
                continue;
            var count = _outCount[slot];
            for (var i = 0; i < count; i++)
            {
                var other = _out[slot][i];
                if (!_enabled[other])
                    continue;
                var p = ClosestOnSegment(position, _positions[slot], _positions[other]);
                var d = Vector3.DistanceSquared(position, p);
                if (d < bestDistance)
                {
                    bestDistance = d;
                    best = p;
                    any = true;
                }
            }
        }

        return any ? best : position;
    }

    /// <summary>The distance between two points with scalar operations only (no SIMD dot product), so every CPU agrees.</summary>
    public static float Distance(Vector3 a, Vector3 b)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var dz = b.Z - a.Z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static Vector3 ClosestOnSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        var ab = b - a;
        var length = Vector3.Dot(ab, ab);
        if (length <= 0f)
            return a;
        var t = Math.Clamp(Vector3.Dot(p - a, ab) / length, 0f, 1f);
        return a + ab * t;
    }

    /// <summary>A* from slot <paramref name="start"/> to <paramref name="end"/>; <paramref name="last"/> is the path's last slot.</summary>
    public GridPathStatus Solve(int start, int end, bool allowPartial, IPointCosts costs, out int last)
    {
        last = -1;
        if (!_enabled[start])
            return GridPathStatus.InvalidPoint;
        var search = Search;
        search.Begin();
        if (start == end)
        {
            search.Open(start, 0f, 0f, -1);
            last = start;
            return GridPathStatus.Found;
        }

        if (!_enabled[end] && !allowPartial)
            return GridPathStatus.NotFound;
        _end = end;
        search.Open(start, 0f, costs.Estimate(start, end), -1);
        var closest = start;
        while (search.OpenCount > 0)
        {
            var node = search.PopMin();
            if (node == _end)
            {
                last = node;
                return GridPathStatus.Found;
            }

            if (IsCloser(node, closest))
                closest = node;
            var g = search.G(node);
            var count = _outCount[node];
            var links = _out[node];
            for (var i = 0; i < count; i++)
            {
                var next = links[i];
                if (!_enabled[next] || search.IsClosed(next))
                    continue;
                var tentative = g + costs.Compute(node, next) * _weights[next];
                if (!search.IsSeen(next))
                    search.Open(next, tentative, costs.Estimate(next, end), node);
                else if (tentative < search.G(next))
                    search.Decrease(next, tentative, node);
            }
        }

        if (!allowPartial)
            return GridPathStatus.NotFound;
        last = closest;
        return GridPathStatus.Partial;
    }

    private bool IsCloser(int node, int best)
    {
        var h = Search.H(node);
        var bh = Search.H(best);
        if (h != bh)
            return h < bh;
        var g = Search.G(node);
        var bg = Search.G(best);
        if (g != bg)
            return g < bg;
        return _ids[node] < _ids[best];
    }

    public int PathLength(int last)
    {
        var length = 0;
        for (var node = last; node >= 0; node = Search.Parent(node))
            length++;
        return length;
    }

    private static void RequireWeight(float weight)
    {
        if (!(weight >= 0f) || !float.IsFinite(weight))
            throw new ArgumentOutOfRangeException(nameof(weight), "A weight scale must be finite and not negative.");
    }

    private static bool Contains(int[][] links, int[] counts, int slot, int other)
    {
        var list = links[slot];
        for (var i = 0; i < counts[slot]; i++)
            if (list[i] == other)
                return true;
        return false;
    }

    private static void AddLink(int[][] links, int[] counts, int slot, int other)
    {
        if (Contains(links, counts, slot, other))
            return;
        ref var list = ref links[slot];
        if (counts[slot] == list.Length)
            Array.Resize(ref list, list.Length * 2);
        list[counts[slot]++] = other;
    }

    // Order-preserving removal: neighbour order decides tie-breaks, so it must not depend on removals elsewhere.
    private static bool RemoveLink(int[][] links, int[] counts, int slot, int other)
    {
        var list = links[slot];
        var count = counts[slot];
        for (var i = 0; i < count; i++)
        {
            if (list[i] != other)
                continue;
            Array.Copy(list, i + 1, list, i, count - i - 1);
            counts[slot] = count - 1;
            return true;
        }

        return false;
    }
}

/// <summary>The cost callbacks of a <see cref="PointGraph"/> search (implemented by the public A* classes).</summary>
internal interface IPointCosts
{
    float Compute(int fromSlot, int toSlot);

    float Estimate(int fromSlot, int endSlot);
}
