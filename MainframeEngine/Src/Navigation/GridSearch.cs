namespace MainframeEngine;

/// <summary>
/// The scratch of an A* search over dense node indices (grid cells or point-graph slots): g and h per node, the parent
/// links, a per-query stamp that marks nodes open or closed without clearing arrays, and an indexed binary min-heap.
/// Sized once (<see cref="EnsureCapacity"/>); a query allocates nothing. The heap orders by f = g + h, then lower h,
/// then lower index, so a search is deterministic.
/// </summary>
internal sealed class GridSearch
{
    private float[] _g = [];
    private float[] _h = [];
    private int[] _parent = [];
    private uint[] _stamp = [];   // _query: open (g valid), _query | 1: closed; anything else: unseen this query
    private int[] _heapPos = [];  // index into _heap while open
    private int[] _heap = [];
    private int _heapCount;
    private uint _query;

    /// <summary>The node count the arrays hold.</summary>
    public int Capacity => _g.Length;

    /// <summary>The current query's stamp (even, ≥ 2); exposed for the wrap-around test.</summary>
    internal uint QueryStamp
    {
        get => _query;
        set => _query = value & ~1u;
    }

    /// <summary>Grows the arrays to hold <paramref name="count"/> nodes (allocates; never per query).</summary>
    public void EnsureCapacity(int count)
    {
        if (count <= _g.Length)
            return;
        _g = new float[count];
        _h = new float[count];
        _parent = new int[count];
        _stamp = new uint[count];
        _heapPos = new int[count];
        _heap = new int[count];
        _query = 0;
        _heapCount = 0;
    }

    /// <summary>Starts a query: every node unseen, the heap empty. Clears the stamps only when the counter wraps.</summary>
    public void Begin()
    {
        _query += 2;
        if (_query >= 0xFFFF_FFF0u || _query < 2)
        {
            Array.Clear(_stamp);
            _query = 2;
        }

        _heapCount = 0;
    }

    public int OpenCount => _heapCount;

    public bool IsSeen(int node) => (_stamp[node] | 1u) == (_query | 1u);

    public bool IsOpen(int node) => _stamp[node] == _query;

    public bool IsClosed(int node) => _stamp[node] == (_query | 1u);

    public float G(int node) => _g[node];

    public float H(int node) => _h[node];

    public int Parent(int node) => _parent[node];

    /// <summary>Opens an unseen node.</summary>
    public void Open(int node, float g, float h, int parent)
    {
        _g[node] = g;
        _h[node] = h;
        _parent[node] = parent;
        _stamp[node] = _query;
        var i = _heapCount++;
        _heap[i] = node;
        _heapPos[node] = i;
        SiftUp(i);
    }

    /// <summary>Lowers the g of an open node.</summary>
    public void Decrease(int node, float g, int parent)
    {
        _g[node] = g;
        _parent[node] = parent;
        SiftUp(_heapPos[node]);
    }

    /// <summary>Removes the open node with the lowest f and closes it.</summary>
    public int PopMin()
    {
        var top = _heap[0];
        var last = _heap[--_heapCount];
        if (_heapCount > 0)
        {
            _heap[0] = last;
            _heapPos[last] = 0;
            SiftDown(0);
        }

        _stamp[top] = _query | 1u;
        return top;
    }

    private bool Less(int a, int b)
    {
        var fa = _g[a] + _h[a];
        var fb = _g[b] + _h[b];
        if (fa != fb)
            return fa < fb;
        if (_h[a] != _h[b])
            return _h[a] < _h[b];
        return a < b;
    }

    private void SiftUp(int i)
    {
        var node = _heap[i];
        while (i > 0)
        {
            var parent = (i - 1) >> 1;
            var p = _heap[parent];
            if (!Less(node, p))
                break;
            _heap[i] = p;
            _heapPos[p] = i;
            i = parent;
        }

        _heap[i] = node;
        _heapPos[node] = i;
    }

    private void SiftDown(int i)
    {
        var node = _heap[i];
        var half = _heapCount >> 1;
        while (i < half)
        {
            var child = 2 * i + 1;
            var c = _heap[child];
            var right = child + 1;
            if (right < _heapCount && Less(_heap[right], c))
            {
                child = right;
                c = _heap[child];
            }

            if (!Less(c, node))
                break;
            _heap[i] = c;
            _heapPos[c] = i;
            i = child;
        }

        _heap[i] = node;
        _heapPos[node] = i;
    }
}
