using Silk.NET.Vulkan;
using VkBufferHandle = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// Batched CPU→GPU uploads. Data is copied into a persistently mapped staging ring when enqueued; the copies,
/// layout transitions and mip generation are recorded at the start of the next frame's command buffer
/// (before the shadow pass) with one barrier batch to the consumer stages. Nothing waits on the queue: the
/// staging space is reclaimed when that frame's fence signals.
/// </summary>
/// <remarks>
/// <para>Resources created at load time or in <c>OnUpdate</c> are ready for the same frame's
/// draws. Creating one while a frame is being recorded (inside the shadow or main pass callbacks) cannot
/// join that command buffer; the GPU wrappers then call <see cref="FlushIfRecording"/>, which submits the
/// pending work in a one-shot command buffer and waits on its fence (never <c>vkQueueWaitIdle</c>).</para>
/// <para>Uploads larger than half the ring use a temporary staging buffer, released through the
/// <see cref="DeletionQueue"/>. Render thread only.</para>
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It is a queue of pending GPU uploads; the name is the established graphics term.")]
public sealed unsafe class UploadQueue : IDisposable
{
    /// <summary>Default staging ring size.</summary>
    public const ulong DefaultRingSize = 32ul << 20;

    private enum OpKind : byte { CopyBuffer, CopyImage, Transition, ClearDepth }

    private struct Op
    {
        public OpKind Kind;
        public VkBufferHandle Src;
        public ulong SrcOffset;
        public ulong Size;
        public VkBufferHandle DstBuffer;
        public ulong DstOffset;
        public Image Image;
        public ImageAspectFlags Aspect;
        public uint Width, Height, Layers, Mips;
        public ImageLayout OldLayout, NewLayout;
        public PipelineStageFlags DstStage;
        public AccessFlags DstAccess;
    }

    private readonly Vk _vk;
    private readonly Device _device;
    private readonly IVulkanContext _ctx;
    private readonly GpuAllocator _allocator;
    private readonly DeletionQueue _deletions;
    private readonly StagingRing _ring;
    private readonly VkBufferHandle _ringBuffer;
    private readonly GpuAllocation _ringMemory;
    private readonly List<Op> _ops = new(32);
    private readonly List<ImageMemoryBarrier> _barriers = new(32);
    private readonly List<(VkBufferHandle Buffer, GpuAllocation Memory)> _tempStaging = [];
    private Fence _oneShotFence;
    private bool _disposed;

    internal UploadQueue(IVulkanContext ctx, GpuAllocator allocator, DeletionQueue deletions, ulong ringSize = DefaultRingSize)
    {
        _ctx = ctx;
        _vk = ctx.Vk;
        _device = ctx.Device;
        _allocator = allocator;
        _deletions = deletions;
        _ring = new StagingRing(ringSize);
        (_ringBuffer, _ringMemory) = CreateStaging(ringSize);
    }

    /// <summary>Operations waiting to be recorded.</summary>
    public int PendingCount => _ops.Count;

    /// <summary>Staging ring capacity and the bytes held by uploads whose frames have not finished.</summary>
    public ulong RingCapacity => _ring.Capacity;
    public ulong RingUsedBytes => _ring.UsedBytes;

    /// <summary>Bytes uploaded since startup (diagnostics).</summary>
    public ulong TotalUploadedBytes { get; private set; }

    // ── Enqueue ─────────────────────────────────────────────────────────────────

    /// <summary>Copies <paramref name="data"/> into <paramref name="dst"/> at <paramref name="dstOffset"/> (needs TRANSFER_DST usage).</summary>
    public void UploadBuffer(VkBufferHandle dst, ulong dstOffset, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.IsEmpty) return;
        var (src, srcOffset) = Stage(data, 16);
        _ops.Add(new Op { Kind = OpKind.CopyBuffer, Src = src, SrcOffset = srcOffset, Size = (ulong)data.Length, DstBuffer = dst, DstOffset = dstOffset });
    }

    /// <summary>
    /// Uploads tightly packed pixels for mip 0 of every layer of <paramref name="image"/> (layer after layer),
    /// generates the other mips by blitting when <see cref="GpuImage.MipLevels"/> &gt; 1, and leaves the
    /// image in <c>SHADER_READ_ONLY_OPTIMAL</c> for fragment and vertex shaders.
    /// </summary>
    public void UploadImage(GpuImage image, ReadOnlySpan<byte> pixels)
    {
        ArgumentNullException.ThrowIfNull(image);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var expected = (ulong)image.Width * image.Height * image.ArrayLayers * FormatInfo.BytesPerPixel(image.Format);
        if ((ulong)pixels.Length != expected)
            throw new ArgumentException($"Expected {expected} bytes of pixels for {image.Width}x{image.Height}x{image.ArrayLayers} {image.Format}, got {pixels.Length}.", nameof(pixels));

        var (src, srcOffset) = Stage(pixels, 16);
        _ops.Add(new Op
        {
            Kind = OpKind.CopyImage,
            Src = src,
            SrcOffset = srcOffset,
            Size = (ulong)pixels.Length,
            Image = image.Handle,
            Aspect = ImageAspectFlags.ColorBit,
            Width = image.Width,
            Height = image.Height,
            Layers = image.ArrayLayers,
            Mips = image.MipLevels,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            DstStage = PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.VertexShaderBit,
            DstAccess = AccessFlags.ShaderReadBit,
        });
    }

    /// <summary>Transitions every layer and mip of an image without uploading (initial layouts for render targets, shadow maps).</summary>
    public void TransitionImage(Image image, ImageAspectFlags aspect, uint layers, uint mips, ImageLayout oldLayout,
        ImageLayout newLayout, PipelineStageFlags dstStage, AccessFlags dstAccess)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ops.Add(new Op
        {
            Kind = OpKind.Transition,
            Image = image,
            Aspect = aspect,
            Layers = layers,
            Mips = mips,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            DstStage = dstStage,
            DstAccess = dstAccess,
        });
    }

    /// <summary>Clears a depth image to <c>1.0</c> and leaves it in <c>DEPTH_STENCIL_READ_ONLY_OPTIMAL</c> for fragment shaders.</summary>
    public void ClearDepthToFar(Image image, ImageAspectFlags barrierAspect, uint layers)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _ops.Add(new Op
        {
            Kind = OpKind.ClearDepth,
            Image = image,
            Aspect = barrierAspect,
            Layers = layers,
            Mips = 1,
            NewLayout = ImageLayout.DepthStencilReadOnlyOptimal,
            DstStage = PipelineStageFlags.FragmentShaderBit,
            DstAccess = AccessFlags.ShaderReadBit,
        });
    }

    private (VkBufferHandle Buffer, ulong Offset) Stage(ReadOnlySpan<byte> data, ulong alignment)
    {
        var size = (ulong)data.Length;
        TotalUploadedBytes += size;
        var frame = _deletions.CurrentFrame + 1; // recorded at the start of the next frame
        if (size <= _ring.Capacity / 2 && _ring.TryAllocate(size, alignment, frame, out var offset))
        {
            data.CopyTo(new Span<byte>((void*)(_ringMemory.MappedPointer + (nint)offset), data.Length));
            return (_ringBuffer, offset);
        }

        var (buffer, memory) = CreateStaging(size);
        data.CopyTo(new Span<byte>((void*)memory.MappedPointer, data.Length));
        _tempStaging.Add((buffer, memory));
        return (buffer, 0);
    }

    private (VkBufferHandle, GpuAllocation) CreateStaging(ulong size)
    {
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = BufferUsageFlags.TransferSrcBit,
            SharingMode = SharingMode.Exclusive,
        };
        _vk.CreateBuffer(_device, in info, null, out var buffer).Check("vkCreateBuffer (staging)");
        _vk.GetBufferMemoryRequirements(_device, buffer, out var req);
        var memory = _allocator.Allocate(req, GpuMemoryUsage.Staging, GpuResourceKind.Linear);
        _vk.BindBufferMemory(_device, buffer, memory.Memory, memory.Offset).Check("vkBindBufferMemory (staging)");
        return (buffer, memory);
    }

    // ── Record / submit ─────────────────────────────────────────────────────────

    /// <summary>Renderer: frame <paramref name="completedFrame"/> has finished; its staging space is free again.</summary>
    internal void Release(ulong completedFrame) => _ring.Release(completedFrame);

    /// <summary>
    /// Records every pending operation into <paramref name="cb"/> (no render pass active). Called by the renderer
    /// right after the frame's command buffer begins.
    /// </summary>
    internal void Record(CommandBuffer cb)
    {
        if (_ops.Count == 0)
            return;

        var ops = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_ops);

        // 1. Images that receive data or a clear: UNDEFINED → TRANSFER_DST, one batched barrier.
        foreach (ref readonly var op in ops)
        {
            if (op.Kind is OpKind.CopyImage or OpKind.ClearDepth)
                AddBarrier(op.Image, op.Aspect, 0, op.Mips, op.Layers, ImageLayout.Undefined, ImageLayout.TransferDstOptimal,
                    AccessFlags.None, AccessFlags.TransferWriteBit);
        }

        // Source: every stage that reads uploaded resources, so earlier frames' reads of a resource that is being
        // re-uploaded (GpuBuffer.Upload on an existing buffer, a replaced image) finish before it is overwritten.
        const PipelineStageFlags consumers = PipelineStageFlags.VertexInputBit | PipelineStageFlags.VertexShaderBit |
                                             PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.TransferBit;
        var anyBufferCopy = false;
        foreach (ref readonly var op in ops)
            anyBufferCopy |= op.Kind == OpKind.CopyBuffer;
        if (_barriers.Count > 0)
            FlushBarriers(cb, consumers, PipelineStageFlags.TransferBit);
        else if (anyBufferCopy)
            _vk.CmdPipelineBarrier(cb, consumers, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 0, null); // execution dependency (WAR)

        // 2. Copies and clears.
        var buffersWritten = false;
        foreach (ref readonly var op in ops)
        {
            switch (op.Kind)
            {
                case OpKind.CopyBuffer:
                    {
                        var region = new BufferCopy { SrcOffset = op.SrcOffset, DstOffset = op.DstOffset, Size = op.Size };
                        _vk.CmdCopyBuffer(cb, op.Src, op.DstBuffer, 1, &region);
                        buffersWritten = true;
                        break;
                    }
                case OpKind.CopyImage:
                    {
                        var layerBytes = op.Size / op.Layers;
                        for (uint layer = 0; layer < op.Layers; layer++)
                        {
                            var region = new BufferImageCopy
                            {
                                BufferOffset = op.SrcOffset + layer * layerBytes,
                                ImageSubresource = new ImageSubresourceLayers(op.Aspect, 0, layer, 1),
                                ImageExtent = new Extent3D(op.Width, op.Height, 1),
                            };
                            _vk.CmdCopyBufferToImage(cb, op.Src, op.Image, ImageLayout.TransferDstOptimal, 1, &region);
                        }

                        break;
                    }
                case OpKind.ClearDepth:
                    {
                        var clear = new ClearDepthStencilValue { Depth = 1f };
                        var range = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, op.Layers);
                        _vk.CmdClearDepthStencilImage(cb, op.Image, ImageLayout.TransferDstOptimal, &clear, 1, &range);
                        break;
                    }
            }
        }

        // 3. Mip chains (each level blitted from the previous one), then final layouts in one batch.
        foreach (ref readonly var op in ops)
        {
            if (op.Kind == OpKind.CopyImage && op.Mips > 1)
                GenerateMips(cb, op);
        }

        var finalStages = PipelineStageFlags.None;
        var transitionsOnly = PipelineStageFlags.None;
        foreach (ref readonly var op in ops)
        {
            if (op.Kind is OpKind.CopyImage or OpKind.ClearDepth)
            {
                // Mipped images: every level but the last is already final (GenerateMips).
                var baseMip = op.Mips > 1 ? op.Mips - 1 : 0;
                var count = op.Mips > 1 ? 1 : op.Mips;
                AddBarrier(op.Image, op.Aspect, baseMip, count, op.Layers, ImageLayout.TransferDstOptimal, op.NewLayout,
                    AccessFlags.TransferWriteBit, op.DstAccess);
                finalStages |= op.DstStage;
            }
        }

        FlushBarriers(cb, PipelineStageFlags.TransferBit, finalStages);

        foreach (ref readonly var op in ops)
        {
            if (op.Kind == OpKind.Transition)
            {
                AddBarrier(op.Image, op.Aspect, 0, op.Mips, op.Layers, op.OldLayout, op.NewLayout, AccessFlags.None, op.DstAccess);
                transitionsOnly |= op.DstStage;
            }
        }

        FlushBarriers(cb, PipelineStageFlags.TopOfPipeBit, transitionsOnly);

        // 4. Buffer data: one barrier for every consumer.
        if (buffersWritten)
        {
            var barrier = new MemoryBarrier
            {
                SType = StructureType.MemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.VertexAttributeReadBit | AccessFlags.IndexReadBit |
                                AccessFlags.UniformReadBit | AccessFlags.ShaderReadBit,
            };
            _vk.CmdPipelineBarrier(cb, PipelineStageFlags.TransferBit,
                PipelineStageFlags.VertexInputBit | PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit,
                0, 1, &barrier, 0, null, 0, null);
        }

        _ops.Clear();

        // Temporary staging buffers are referenced by this command buffer: free them after this frame.
        foreach (var (buffer, memory) in _tempStaging)
            _deletions.Enqueue(GpuDeletion.Of(buffer, memory));
        _tempStaging.Clear();
    }

    private void GenerateMips(CommandBuffer cb, in Op op)
    {
        int w = (int)op.Width, h = (int)op.Height;
        for (uint level = 1; level < op.Mips; level++)
        {
            // Previous level: TRANSFER_DST → TRANSFER_SRC.
            ImageBarrier(cb, op.Image, op.Aspect, level - 1, 1, op.Layers, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
                AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit);

            int nw = Math.Max(1, w / 2), nh = Math.Max(1, h / 2);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(op.Aspect, level - 1, 0, op.Layers),
                DstSubresource = new ImageSubresourceLayers(op.Aspect, level, 0, op.Layers),
            };
            blit.SrcOffsets[1] = new Offset3D(w, h, 1);
            blit.DstOffsets[1] = new Offset3D(nw, nh, 1);
            _vk.CmdBlitImage(cb, op.Image, ImageLayout.TransferSrcOptimal, op.Image, ImageLayout.TransferDstOptimal, 1, &blit, Filter.Linear);

            // Previous level is final.
            ImageBarrier(cb, op.Image, op.Aspect, level - 1, 1, op.Layers, ImageLayout.TransferSrcOptimal, op.NewLayout,
                AccessFlags.TransferReadBit, op.DstAccess, PipelineStageFlags.TransferBit, op.DstStage);
            w = nw;
            h = nh;
        }
    }

    private void ImageBarrier(CommandBuffer cb, Image image, ImageAspectFlags aspect, uint baseMip, uint mips, uint layers,
        ImageLayout oldLayout, ImageLayout newLayout, AccessFlags srcAccess, AccessFlags dstAccess,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        AddBarrier(image, aspect, baseMip, mips, layers, oldLayout, newLayout, srcAccess, dstAccess);
        FlushBarriers(cb, srcStage, dstStage);
    }

    private void AddBarrier(Image image, ImageAspectFlags aspect, uint baseMip, uint mips, uint layers,
        ImageLayout oldLayout, ImageLayout newLayout, AccessFlags srcAccess, AccessFlags dstAccess)
    {
        _barriers.Add(new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, baseMip, mips, 0, layers),
        });
    }

    private void FlushBarriers(CommandBuffer cb, PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        if (_barriers.Count == 0)
            return;
        var barriers = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_barriers);
        fixed (ImageMemoryBarrier* p = barriers)
            _vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, (uint)barriers.Length, p);
        _barriers.Clear();
    }

    /// <summary>
    /// Records and submits everything pending in a one-shot command buffer and waits on its fence. For work
    /// that must be complete before the next frame is recorded; normal uploads just wait for
    /// <see cref="Record"/>.
    /// </summary>
    public void SubmitAndWait()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_ops.Count == 0)
            return;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _ctx.CommandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        _vk.AllocateCommandBuffers(_device, in allocInfo, out var cb).Check("vkAllocateCommandBuffers (upload)");
        try
        {
            var begin = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo, Flags = CommandBufferUsageFlags.OneTimeSubmitBit };
            _vk.BeginCommandBuffer(cb, in begin).Check("vkBeginCommandBuffer (upload)");
            Record(cb);
            _vk.EndCommandBuffer(cb).Check("vkEndCommandBuffer (upload)");

            if (_oneShotFence.Handle == 0)
            {
                var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo };
                _vk.CreateFence(_device, in fenceInfo, null, out _oneShotFence).Check("vkCreateFence (upload)");
            }

            var submit = new SubmitInfo { SType = StructureType.SubmitInfo, CommandBufferCount = 1, PCommandBuffers = &cb };
            _vk.QueueSubmit(_ctx.GraphicsQueue, 1, in submit, _oneShotFence).Check("vkQueueSubmit (upload)");
            _vk.WaitForFences(_device, 1, in _oneShotFence, true, ulong.MaxValue).Check("vkWaitForFences (upload)");
            _vk.ResetFences(_device, 1, in _oneShotFence).Check("vkResetFences (upload)");
        }
        finally
        {
            _vk.FreeCommandBuffers(_device, _ctx.CommandPool, 1, &cb);
        }
    }

    /// <summary>
    /// Submits pending work now when a frame is being recorded (its command buffer has already taken this
    /// frame's uploads), so a resource created mid-frame is usable in the same frame.
    /// </summary>
    public void FlushIfRecording()
    {
        if (_ctx.FrameStarted && _ops.Count > 0)
            SubmitAndWait();
    }

    /// <summary>Releases the staging ring and fence. The device must be idle.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ops.Clear();
        foreach (var (buffer, memory) in _tempStaging)
        {
            _vk.DestroyBuffer(_device, buffer, null);
            _allocator.Free(memory);
        }

        _tempStaging.Clear();
        _vk.DestroyBuffer(_device, _ringBuffer, null);
        _allocator.Free(_ringMemory);
        if (_oneShotFence.Handle != 0)
            _vk.DestroyFence(_device, _oneShotFence, null);
    }
}
