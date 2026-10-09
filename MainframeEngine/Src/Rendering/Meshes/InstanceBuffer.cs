using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Per-frame-slot streaming buffer of <see cref="MeshInstanceData"/>: a bump allocator reset at the start of every
/// frame (keyed by frame number). Persistently mapped, so instances are written straight into it. When a frame
/// needs more than fits, a buffer twice the size replaces it (the old one goes through the deletion queue, still
/// valid for the draws already recorded from it). Steady-state frames allocate nothing.
/// </summary>
internal sealed class InstanceBuffer : IDisposable
{
    private const int Slots = IVulkanContext.MaxFramesInFlight;
    private const int InitialCapacity = 1024;

    private readonly IVulkanContext _ctx;
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[Slots];
    private readonly ulong[] _frame = new ulong[Slots];
    private readonly int[] _used = new int[Slots];
    private bool _disposed;

    public InstanceBuffer(IVulkanContext ctx)
    {
        _ctx = ctx;
    }

    /// <summary>Instances written this frame (diagnostics).</summary>
    public int UsedThisFrame => _frame[_ctx.FrameSlot] == _ctx.FrameNumber ? _used[_ctx.FrameSlot] : 0;

    /// <summary>Current capacity of the frame slot's buffer, in instances.</summary>
    public int Capacity => (int)((_buffers[_ctx.FrameSlot]?.Size ?? 0) / MeshInstanceData.Size);

    /// <summary>
    /// Reserves <paramref name="count"/> contiguous instances in this frame's buffer: bind <paramref name="buffer"/>
    /// at binding 1 and draw with <c>firstInstance</c> = <paramref name="first"/> + i. Only while a frame is recording.
    /// </summary>
    public Span<MeshInstanceData> Allocate(int count, out GpuBuffer buffer, out uint first)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ctx.FrameStarted)
            throw new InvalidOperationException("Instances can only be written while a frame is recording.");
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        var slot = _ctx.FrameSlot;
        if (_frame[slot] != _ctx.FrameNumber)
        {
            _frame[slot] = _ctx.FrameNumber; // the slot's fence has been waited: its buffer is free again
            _used[slot] = 0;
        }

        var current = _buffers[slot];
        var capacity = (int)((current?.Size ?? 0) / MeshInstanceData.Size);
        if (current is null || _used[slot] + count > capacity)
        {
            var newCapacity = Math.Max(InitialCapacity, Math.Max(capacity * 2, count));
            current?.Dispose(); // draws recorded earlier this frame keep it alive until the frame finishes
            current = GpuBuffer.Create(_ctx, (ulong)newCapacity * MeshInstanceData.Size, BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
            _buffers[slot] = current;
            _used[slot] = 0;
        }

        buffer = current;
        first = (uint)_used[slot];
        _used[slot] += count;
        var all = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, MeshInstanceData>(current.MappedSpan);
        return all.Slice((int)first, count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = 0; i < Slots; i++)
        {
            _buffers[i]?.Dispose();
            _buffers[i] = null;
        }
    }
}

/// <summary>
/// Descriptor sets of one layout (material set 2: a UBO, a sampler and four images; the terrain splat set: a UBO, two
/// samplers and five images): fixed-size pools created with FREE_DESCRIPTOR_SET and a new
/// pool when every pool is full. A freed set is returned to its pool once the frames that may still bind it have
/// finished (tracked like the <see cref="DeletionQueue"/>, by frame number), so each pool's live count is exact
/// and allocation never has to probe a full pool.
/// </summary>
internal sealed unsafe class MaterialDescriptorAllocator : IDisposable
{
    private const int SetsPerPool = 64;

    private sealed class PoolEntry(DescriptorPool pool)
    {
        public DescriptorPool Pool { get; } = pool;
        public int Live { get; set; }
    }

    private readonly IVulkanContext _ctx;
    private readonly DescriptorSetLayout _layout;
    private readonly int _samplers;
    private readonly int _images;
    private readonly List<PoolEntry> _pools = [];
    private readonly Queue<(ulong Frame, PoolEntry Pool, DescriptorSet Set)> _pendingFrees = new();
    private bool _disposed;

    /// <param name="samplers">Sampler descriptors per set.</param>
    /// <param name="images">Sampled-image descriptors per set.</param>
    public MaterialDescriptorAllocator(IVulkanContext ctx, DescriptorSetLayout layout, int samplers = 1, int images = 4)
    {
        _ctx = ctx;
        _layout = layout;
        _samplers = samplers;
        _images = images;
    }

    public int PoolCount => _pools.Count;

    /// <summary>Sets allocated and not yet returned.</summary>
    public int LiveSets
    {
        get
        {
            var live = 0;
            foreach (var p in _pools)
                live += p.Live;
            return live;
        }
    }

    public DescriptorSet Allocate(out DescriptorPool pool)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Collect();
        PoolEntry? target = null;
        for (var i = _pools.Count - 1; i >= 0 && target is null; i--)
            if (_pools[i].Live < SetsPerPool)
                target = _pools[i];
        if (target is null)
        {
            target = new PoolEntry(CreatePool());
            _pools.Add(target);
        }

        var layout = _layout;
        var info = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = target.Pool,
            DescriptorSetCount = 1,
            PSetLayouts = &layout,
        };
        _ctx.Vk.AllocateDescriptorSets(_ctx.Device, in info, out var set).Check("vkAllocateDescriptorSets (material)");
        target.Live++;
        pool = target.Pool;
        return set;
    }

    /// <summary>Returns <paramref name="set"/> to <paramref name="pool"/> once frames in flight no longer use it.</summary>
    public void Free(DescriptorPool pool, DescriptorSet set)
    {
        if (set.Handle == 0 || _disposed)
            return;
        var deletions = _ctx.Deletions;
        var frame = deletions.IsRecording ? deletions.CurrentFrame : deletions.CurrentFrame + 1;
        foreach (var entry in _pools)
        {
            if (entry.Pool.Handle == pool.Handle)
            {
                _pendingFrees.Enqueue((frame, entry, set));
                return;
            }
        }
    }

    /// <summary>Frees the sets whose frames have completed (also done by <see cref="Allocate"/>).</summary>
    public void Collect()
    {
        var completed = _ctx.Deletions.CompletedFrame;
        while (_pendingFrees.TryPeek(out var head) && head.Frame <= completed)
        {
            _pendingFrees.Dequeue();
            var set = head.Set;
            _ctx.Vk.FreeDescriptorSets(_ctx.Device, head.Pool.Pool, 1, &set).Check("vkFreeDescriptorSets (material)");
            head.Pool.Live--;
        }
    }

    private DescriptorPool CreatePool()
    {
        var sizes = stackalloc DescriptorPoolSize[3];
        sizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = SetsPerPool };
        sizes[1] = new DescriptorPoolSize { Type = DescriptorType.Sampler, DescriptorCount = (uint)(SetsPerPool * _samplers) };
        sizes[2] = new DescriptorPoolSize { Type = DescriptorType.SampledImage, DescriptorCount = (uint)(SetsPerPool * _images) };
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = SetsPerPool,
            PoolSizeCount = 3,
            PPoolSizes = sizes,
        };
        _ctx.Vk.CreateDescriptorPool(_ctx.Device, in info, null, out var pool).Check("vkCreateDescriptorPool (materials)");
        return pool;
    }

    /// <summary>Destroys the pools (with every set still in them) once frames in flight no longer use them.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pendingFrees.Clear(); // destroying a pool frees its sets
        foreach (var entry in _pools)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(entry.Pool));
        _pools.Clear();
    }
}
