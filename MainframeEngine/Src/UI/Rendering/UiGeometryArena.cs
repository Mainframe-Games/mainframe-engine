using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>Where compiled UI geometry lives: a chunk buffer, the first vertex and first index for <c>vkCmdDrawIndexed</c>.</summary>
internal readonly record struct UiGeometryRange(int Chunk, ulong Offset, VkBuffer Buffer, uint FirstIndex, int VertexOffset, uint IndexCount);

/// <summary>
/// The retained geometry arena for RmlUi's compiled geometry: host-visible, persistently mapped chunk buffers
/// (vertex + index usage) sub-allocated with the allocator's free list, so a HUD's hundreds of small meshes share a
/// handful of buffers and draws only rebind on a chunk change. Each allocation holds the vertices (20-byte
/// <see cref="UI.Rml.RmlVertex"/>, start aligned to a whole vertex) followed by the 32-bit indices, so one buffer bind
/// serves both and a draw is <c>vkCmdDrawIndexed(count, 1, firstIndex, vertexOffset, 0)</c>.
/// </summary>
/// <remarks>
/// Ranges are released through <see cref="Release"/> with the frame that may still read them and returned to the free
/// list by <see cref="Collect"/> once that frame has completed, so nothing in flight is overwritten. Render thread only;
/// allocation-free once the chunk list and the pending queue have grown.
/// </remarks>
internal sealed class UiGeometryArena : IDisposable
{
    /// <summary>Default chunk size; larger meshes get a chunk of their own size.</summary>
    public const ulong ChunkSize = 1ul << 20;

    private const ulong VertexSize = 20;
    private const ulong IndexSize = 4;

    private readonly Func<ulong, GpuBuffer> _createBuffer;
    private readonly List<Chunk> _chunks = [];
    private readonly Queue<(ulong Frame, int Chunk, ulong Offset)> _pending = new(64);

    private sealed class Chunk(GpuBuffer buffer, ulong size)
    {
        public GpuBuffer Buffer { get; } = buffer;
        public FreeListBlock Block { get; } = new(size, 1);
    }

    /// <param name="createBuffer">Creates a mapped buffer of the given size (vertex + index usage).</param>
    public UiGeometryArena(Func<ulong, GpuBuffer> createBuffer)
    {
        _createBuffer = createBuffer;
    }

    public UiGeometryArena(IVulkanContext ctx)
        : this(size => GpuBuffer.Create(ctx, size, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.IndexBufferBit, GpuMemoryUsage.Dynamic))
    {
    }

    public int ChunkCount => _chunks.Count;

    public int LiveAllocations { get; private set; }

    public ulong UsedBytes
    {
        get
        {
            ulong used = 0;
            foreach (var chunk in _chunks)
                used += chunk.Block.UsedBytes;
            return used;
        }
    }

    public int PendingReleases => _pending.Count;

    /// <summary>Bytes an allocation of <paramref name="vertices"/> and <paramref name="indices"/> needs (with vertex alignment slack).</summary>
    public static ulong RequiredBytes(int vertices, int indices) =>
        (ulong)vertices * VertexSize + (ulong)indices * IndexSize + VertexSize;

    /// <summary>Copies the geometry into the arena.</summary>
    public unsafe UiGeometryRange Allocate(ReadOnlySpan<UI.Rml.RmlVertex> vertices, ReadOnlySpan<int> indices)
    {
        if (vertices.IsEmpty || indices.IsEmpty)
            throw new ArgumentException("Geometry needs vertices and indices.");

        var size = RequiredBytes(vertices.Length, indices.Length);
        var chunkIndex = -1;
        ulong offset = 0;
        for (var i = 0; i < _chunks.Count; i++)
        {
            if (_chunks[i].Block.TryAllocate(size, IndexSize, GpuResourceKind.Linear, out offset))
            {
                chunkIndex = i;
                break;
            }
        }

        if (chunkIndex < 0)
        {
            var chunkSize = Math.Max(ChunkSize, size);
            var chunk = new Chunk(_createBuffer(chunkSize), chunkSize);
            _chunks.Add(chunk);
            chunkIndex = _chunks.Count - 1;
            if (!chunk.Block.TryAllocate(size, IndexSize, GpuResourceKind.Linear, out offset))
                throw new InvalidOperationException("A fresh geometry chunk could not hold the geometry.");
        }

        var buffer = _chunks[chunkIndex].Buffer;
        var vertexStart = (offset + VertexSize - 1) / VertexSize * VertexSize;
        var indexStart = vertexStart + (ulong)vertices.Length * VertexSize;
        buffer.Write(vertices, vertexStart);
        buffer.Write(indices, indexStart);
        LiveAllocations++;
        return new UiGeometryRange(chunkIndex, offset, buffer.Handle, (uint)(indexStart / IndexSize), (int)(vertexStart / VertexSize),
            (uint)indices.Length);
    }

    /// <summary>Frees <paramref name="range"/> once frame <paramref name="lastUseFrame"/> has completed.</summary>
    public void Release(in UiGeometryRange range, ulong lastUseFrame)
    {
        _pending.Enqueue((lastUseFrame, range.Chunk, range.Offset));
        LiveAllocations--;
    }

    /// <summary>Returns ranges whose last frame is at or before <paramref name="completedFrame"/> to the free lists.</summary>
    public void Collect(ulong completedFrame)
    {
        while (_pending.TryPeek(out var head) && head.Frame <= completedFrame)
        {
            _pending.Dequeue();
            _chunks[head.Chunk].Block.Free(head.Offset);
        }
    }

    /// <summary>Releases every chunk buffer through the deletion queue (pending ranges included).</summary>
    public void Dispose()
    {
        foreach (var chunk in _chunks)
            chunk.Buffer.Dispose();
        _chunks.Clear();
        _pending.Clear();
        LiveAllocations = 0;
    }
}
