using System.Numerics;

namespace MainframeEngine;

/// <summary>A square region of the shadow atlas, in texels (<see cref="Size"/> 0 = no tile).</summary>
public readonly record struct ShadowAtlasTile(int X, int Y, int Size)
{
    public bool IsEmpty => Size == 0;

    /// <summary>True when the two tiles share at least one texel.</summary>
    public bool Overlaps(in ShadowAtlasTile other) =>
        !IsEmpty && !other.IsEmpty &&
        X < other.X + other.Size && other.X < X + Size &&
        Y < other.Y + other.Size && other.Y < Y + Size;
}

/// <summary>
/// Quadtree (buddy) allocator for square power-of-two tiles in a power-of-two shadow atlas. A request takes the
/// first free node of its size in Z order, splitting larger free nodes; freeing merges four free siblings back into
/// their parent. <see cref="Pack"/> places a whole set of requests largest first, which never fragments: every set
/// whose total area fits the atlas is placed at full size. Allocation-free after construction.
/// </summary>
public sealed class ShadowAtlasAllocator
{
    private const byte FreeNode = 0, SplitNode = 1, UsedNode = 2;

    /// <summary>Most requests <see cref="Pack"/> accepts in one call.</summary>
    public const int MaxPackRequests = 64;

    private readonly byte[] _nodes;
    private readonly int _levels;

    /// <summary>An empty atlas of <paramref name="size"/>² texels with tiles of at least <paramref name="minTileSize"/>².</summary>
    public ShadowAtlasAllocator(int size, int minTileSize = 64)
    {
        if (size <= 0 || !BitOperations.IsPow2(size))
            throw new ArgumentOutOfRangeException(nameof(size), size, "The atlas size must be a positive power of two.");
        if (minTileSize <= 0 || !BitOperations.IsPow2(minTileSize) || minTileSize > size)
            throw new ArgumentOutOfRangeException(nameof(minTileSize), minTileSize, "The minimum tile must be a power of two no larger than the atlas.");

        Size = size;
        MinTileSize = minTileSize;
        _levels = BitOperations.Log2((uint)(size / minTileSize)) + 1;
        var count = 0;
        for (int level = 0, nodesAtLevel = 1; level < _levels; level++, nodesAtLevel *= 4)
            count += nodesAtLevel;
        _nodes = new byte[count];
    }

    /// <summary>Width and height of the atlas in texels.</summary>
    public int Size { get; }

    /// <summary>Smallest tile; smaller requests are rounded up to it.</summary>
    public int MinTileSize { get; }

    /// <summary>Texels covered by allocated tiles.</summary>
    public long UsedArea { get; private set; }

    /// <summary>Allocated tiles.</summary>
    public int TileCount { get; private set; }

    /// <summary>Frees every tile.</summary>
    public void Clear()
    {
        Array.Clear(_nodes);
        UsedArea = 0;
        TileCount = 0;
    }

    /// <summary>The tile size a request of <paramref name="size"/> texels takes: a power of two, at least <see cref="MinTileSize"/>.</summary>
    public int TileSizeFor(int size) => Math.Max(MinTileSize, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, size)));

    /// <summary>Allocates a tile of <see cref="TileSizeFor"/>(<paramref name="size"/>); false when no free node of that size exists.</summary>
    public bool TryAllocate(int size, out ShadowAtlasTile tile)
    {
        tile = default;
        var tileSize = TileSizeFor(size);
        if (tileSize > Size)
            return false;
        var target = BitOperations.Log2((uint)(Size / tileSize));
        if (!Find(0, 0, 0, 0, Size, target, out tile))
            return false;
        UsedArea += (long)tile.Size * tile.Size;
        TileCount++;
        return true;
    }

    private bool Find(int node, int level, int x, int y, int nodeSize, int target, out ShadowAtlasTile tile)
    {
        tile = default;
        var state = _nodes[node];
        if (state == UsedNode)
            return false;
        if (level == target)
        {
            if (state != FreeNode)
                return false;
            _nodes[node] = UsedNode;
            tile = new ShadowAtlasTile(x, y, nodeSize);
            return true;
        }

        if (state == FreeNode)
            _nodes[node] = SplitNode; // its children are free (merging resets them)

        var half = nodeSize / 2;
        var first = node * 4 + 1;
        for (var c = 0; c < 4; c++)
        {
            if (Find(first + c, level + 1, x + (c & 1) * half, y + (c >> 1) * half, half, target, out tile))
                return true;
        }

        return false;
    }

    /// <summary>Returns <paramref name="tile"/> to the atlas, merging free siblings.</summary>
    public void Free(in ShadowAtlasTile tile)
    {
        if (tile.IsEmpty || !BitOperations.IsPow2(tile.Size) || tile.Size < MinTileSize || tile.Size > Size ||
            tile.X % tile.Size != 0 || tile.Y % tile.Size != 0 || tile.X < 0 || tile.Y < 0 || tile.X >= Size || tile.Y >= Size)
            throw new ArgumentException($"{tile} is not a tile of this {Size}² atlas.", nameof(tile));

        var target = BitOperations.Log2((uint)(Size / tile.Size));
        Span<int> path = stackalloc int[_levels];
        var node = 0;
        var nodeSize = Size;
        for (var level = 0; level < target; level++)
        {
            path[level] = node;
            if (_nodes[node] != SplitNode)
                throw new ArgumentException($"{tile} is not allocated.", nameof(tile));
            nodeSize /= 2;
            var child = (tile.X / nodeSize & 1) | (tile.Y / nodeSize & 1) << 1;
            node = node * 4 + 1 + child;
        }

        if (_nodes[node] != UsedNode)
            throw new ArgumentException($"{tile} is not allocated.", nameof(tile));
        _nodes[node] = FreeNode;
        UsedArea -= (long)tile.Size * tile.Size;
        TileCount--;

        for (var level = target - 1; level >= 0; level--)
        {
            var parent = path[level];
            var first = parent * 4 + 1;
            if (_nodes[first] != FreeNode || _nodes[first + 1] != FreeNode || _nodes[first + 2] != FreeNode || _nodes[first + 3] != FreeNode)
                break;
            _nodes[parent] = FreeNode;
        }
    }

    /// <summary>
    /// Clears the atlas and places every request, largest first (ties in request order); <paramref name="results"/>[i]
    /// receives the tile of <paramref name="requests"/>[i]. When the requests do not fit, the largest tiles are halved
    /// first until they do, so every light keeps a shadow at a lower resolution; requests are dropped (empty tile) only
    /// when every tile is already at <see cref="MinTileSize"/>, last requests first. Returns how many tiles were shrunk
    /// or dropped.
    /// </summary>
    public int Pack(ReadOnlySpan<int> requests, Span<ShadowAtlasTile> results)
    {
        if (requests.Length > MaxPackRequests)
            throw new ArgumentOutOfRangeException(nameof(requests), requests.Length, $"At most {MaxPackRequests} requests.");
        if (results.Length < requests.Length)
            throw new ArgumentException("One result per request is needed.", nameof(results));

        Clear();
        var count = requests.Length;
        Span<int> sizes = stackalloc int[count];
        long area = 0;
        var degraded = 0;
        for (var i = 0; i < count; i++)
        {
            sizes[i] = Math.Min(TileSizeFor(requests[i]), Size);
            area += (long)sizes[i] * sizes[i];
        }

        // Over budget: halve the largest tile (the last of equal ones), or drop the last request once all are minimal.
        var capacity = (long)Size * Size;
        while (area > capacity)
        {
            var largest = -1;
            for (var i = 0; i < count; i++)
                if (sizes[i] > 0 && (largest < 0 || sizes[i] >= sizes[largest]))
                    largest = i;
            var size = sizes[largest];
            area -= (long)size * size;
            if (size > MinTileSize)
            {
                sizes[largest] = size / 2;
                area += (long)sizes[largest] * sizes[largest];
            }
            else
            {
                for (var i = count - 1; i >= 0; i--)
                {
                    if (sizes[i] > 0)
                    {
                        area += (long)size * size; // put the minimal tile back, drop the last one instead
                        area -= (long)sizes[i] * sizes[i];
                        sizes[i] = 0;
                        break;
                    }
                }
            }
        }

        // Insertion sort of the indices, descending by size (stable: equal sizes keep request order). Power-of-two
        // squares placed largest first never fragment the atlas: whatever fits by area fits.
        Span<int> order = stackalloc int[count];
        for (var i = 0; i < count; i++)
            order[i] = i;
        for (var i = 1; i < count; i++)
        {
            var value = order[i];
            var j = i - 1;
            while (j >= 0 && sizes[order[j]] < sizes[value])
            {
                order[j + 1] = order[j];
                j--;
            }

            order[j + 1] = value;
        }

        foreach (var index in order)
        {
            results[index] = sizes[index] > 0 && TryAllocate(sizes[index], out var tile) ? tile : default;
            if (results[index].Size != TileSizeFor(requests[index]))
                degraded++;
        }

        return degraded;
    }

    /// <summary>
    /// The smallest power-of-two atlas, between <paramref name="minimumSize"/> and <paramref name="maximumSize"/>,
    /// that holds every request at full size (<paramref name="maximumSize"/> when none does).
    /// </summary>
    public static int RequiredSize(ReadOnlySpan<int> requests, int minTileSize, int minimumSize, int maximumSize)
    {
        long area = 0;
        var largest = 0;
        foreach (var request in requests)
        {
            var tile = Math.Max(minTileSize, (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(1, request)));
            largest = Math.Max(largest, tile);
            area += (long)tile * tile;
        }

        var size = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(minimumSize, largest));
        // Power-of-two squares packed largest first fill the atlas perfectly: area alone decides.
        while (size < maximumSize && (long)size * size < area)
            size *= 2;
        return Math.Min(size, maximumSize);
    }
}
