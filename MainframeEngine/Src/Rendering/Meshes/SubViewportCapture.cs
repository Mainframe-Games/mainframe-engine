using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Copies a <see cref="SubViewport"/>'s tonemapped image (<see cref="SubViewport.ColorImage"/>, sRGB-encoded RGBA8) to
/// the CPU after the view renders (ADR 0136; Godot's <c>get_texture().get_image()</c> on a viewport). Requests made
/// before a render are copied in that render's command buffer and delivered by <see cref="Collect"/> once the frame's
/// fence has signalled. Not a per-frame path: each delivery allocates its pixels.
/// </summary>
internal sealed unsafe class SubViewportCapture : IDisposable
{
    private const int Slots = IVulkanContext.MaxFramesInFlight;

    private readonly IVulkanContext _ctx;
    private readonly GpuBuffer?[] _readback = new GpuBuffer?[Slots];
    private readonly List<Action<FrameCapture>>[] _inFlight = [[], []];
    private readonly ulong[] _inFlightFrame = new ulong[Slots];
    private readonly (int Width, int Height)[] _inFlightSize = new (int, int)[Slots];
    private readonly List<Action<FrameCapture>> _queued = [];
    private bool _disposed;

    public SubViewportCapture(IVulkanContext ctx)
    {
        _ctx = ctx;
    }

    public bool HasQueued => _queued.Count > 0;

    public bool HasInFlight => _inFlight[0].Count > 0 || _inFlight[1].Count > 0;

    public void Request(Action<FrameCapture> onCaptured)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _queued.Add(onCaptured);
    }

    /// <summary>Records the copy of <paramref name="target"/>'s colour (in shader-read layout after its pass) for the queued requests.</summary>
    public void Copy(CommandBuffer cb, RenderTarget target)
    {
        if (_queued.Count == 0)
            return;
        var slot = _ctx.FrameSlot;
        if (_inFlight[slot].Count > 0)
            return; // the slot's previous copy is collected first (Collect runs before rendering)

        var extent = target.Extent;
        var size = (ulong)extent.Width * extent.Height * 4;
        if (_readback[slot] is not { } buffer || buffer.Size < size)
        {
            _readback[slot]?.Dispose();
            buffer = _readback[slot] = GpuBuffer.Create(_ctx, size, BufferUsageFlags.TransferDstBit, GpuMemoryUsage.Readback);
        }

        var image = target.GetColor(0).Handle;
        var vk = _ctx.Vk;
        Barrier(cb, image, ImageLayout.ShaderReadOnlyOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.ShaderReadBit | AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit,
            PipelineStageFlags.FragmentShaderBit | PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit);
        var region = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageExtent = new Extent3D(extent.Width, extent.Height, 1),
        };
        vk.CmdCopyImageToBuffer(cb, image, ImageLayout.TransferSrcOptimal, buffer.Handle, 1, &region);
        Barrier(cb, image, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferReadBit, AccessFlags.ShaderReadBit,
            PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit);

        // The copy must be visible to the host once the frame's fence has signalled.
        var host = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
        };
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 1, &host, 0, null, 0, null);

        _inFlight[slot].AddRange(_queued);
        _queued.Clear();
        _inFlightFrame[slot] = _ctx.FrameNumber;
        _inFlightSize[slot] = ((int)extent.Width, (int)extent.Height);
    }

    /// <summary>Gathers the copies whose frames have completed into <paramref name="completions"/>.</summary>
    public void Collect(List<(Action<FrameCapture> Callback, FrameCapture Capture)> completions)
    {
        if (!HasInFlight)
            return;
        var completedFrame = _ctx.Deletions.CompletedFrame;
        for (var slot = 0; slot < Slots; slot++)
        {
            var batch = _inFlight[slot];
            if (batch.Count == 0 || _inFlightFrame[slot] > completedFrame)
                continue;
            var (width, height) = _inFlightSize[slot];
            var pixels = _readback[slot]!.MappedSpan[..(width * height * 4)].ToArray();
            var capture = new FrameCapture(width, height, pixels);
            foreach (var callback in batch)
                completions.Add((callback, capture));
            batch.Clear();
        }
    }

    private void Barrier(CommandBuffer cb, Image image, ImageLayout from, ImageLayout to, AccessFlags srcAccess, AccessFlags dstAccess,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage)
    {
        var barrier = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = from,
            NewLayout = to,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        _ctx.Vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var buffer in _readback)
            buffer?.Dispose();
        _queued.Clear();
        foreach (var batch in _inFlight)
            batch.Clear();
    }
}
