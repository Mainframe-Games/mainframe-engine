namespace MainframeEngine;

/// <summary>
/// 64-bit sort keys for mesh draws. Opaque (and cutout) draws sort by pipeline, then material, then mesh and
/// surface, so state changes are minimal and equal draws end up adjacent (one instanced draw per run). Transparent
/// draws sort by material render priority, then back to front by view depth.
/// </summary>
public static class DrawSortKey
{
    private const int PipelineBits = 12, MaterialBits = 20, MeshBits = 20, SurfaceBits = 12;

    /// <summary>[pipeline 12][material 20][mesh 20][surface 12], most significant first.</summary>
    public static ulong Opaque(int pipelineId, int materialId, int meshId, int surface) =>
        ((ulong)(uint)pipelineId & Mask(PipelineBits)) << (MaterialBits + MeshBits + SurfaceBits) |
        ((ulong)(uint)materialId & Mask(MaterialBits)) << (MeshBits + SurfaceBits) |
        ((ulong)(uint)meshId & Mask(MeshBits)) << SurfaceBits |
        ((ulong)(uint)surface & Mask(SurfaceBits));

    /// <summary>
    /// [render priority 8][inverted depth 32][pipeline 12][material 12]: lower priority first, then farther first
    /// (back to front), ties grouped by pipeline and material.
    /// </summary>
    public static ulong Transparent(int renderPriority, float viewDepth, int pipelineId, int materialId)
    {
        var priority = (ulong)(byte)(Math.Clamp(renderPriority, -128, 127) + 128);
        // Non-negative floats order like their bit patterns; inverting sorts the farthest first.
        var depthBits = ~BitConverter.SingleToUInt32Bits(float.IsNaN(viewDepth) ? 0f : Math.Max(viewDepth, 0f));
        return priority << 56 | (ulong)depthBits << 24 |
               ((ulong)(uint)pipelineId & Mask(12)) << 12 | ((ulong)(uint)materialId & Mask(12));
    }

    private static ulong Mask(int bits) => (1ul << bits) - 1;
}

/// <summary>
/// A reusable list of draw items with sort keys: <see cref="Add"/> during the frame's build, <see cref="Sort"/>
/// once, then read <see cref="this[int]"/> in key order. Sorting moves only (key, index) pairs. Grows as needed and
/// never shrinks, so steady-state frames allocate nothing.
/// </summary>
internal sealed class DrawList<T> where T : struct
{
    private ulong[] _keys;
    private int[] _order;
    private T[] _items;

    public DrawList(int capacity = 64)
    {
        capacity = Math.Max(capacity, 1);
        _keys = new ulong[capacity];
        _order = new int[capacity];
        _items = new T[capacity];
    }

    public int Count { get; private set; }

    /// <summary>The item at position <paramref name="sortedIndex"/> in key order (after <see cref="Sort"/>).</summary>
    public ref T this[int sortedIndex] => ref _items[_order[sortedIndex]];

    /// <summary>The key at position <paramref name="sortedIndex"/> in key order (after <see cref="Sort"/>).</summary>
    public ulong KeyAt(int sortedIndex) => _keys[sortedIndex];

    public void Clear()
    {
        // Drop references held by the items (materials, meshes) so a cleared list does not keep them alive.
        if (System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<T>())
            Array.Clear(_items, 0, Count);
        Count = 0;
    }

    public void Add(ulong key, in T item)
    {
        if (Count == _keys.Length)
            Grow();
        _keys[Count] = key;
        _order[Count] = Count;
        _items[Count] = item;
        Count++;
    }

    /// <summary>Sorts by key (ascending; equal keys keep no particular order).</summary>
    public void Sort()
    {
        if (Count > 1)
            _keys.AsSpan(0, Count).Sort(_order.AsSpan(0, Count));
    }

    private void Grow()
    {
        var capacity = _keys.Length * 2;
        Array.Resize(ref _keys, capacity);
        Array.Resize(ref _order, capacity);
        Array.Resize(ref _items, capacity);
    }
}
