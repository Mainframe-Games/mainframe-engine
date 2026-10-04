using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>What is under a pixel of a view's object-ID target.</summary>
/// <param name="ObjectId">The id written by the ID pass (a <see cref="GeometryInstance3D"/>'s <see cref="Node.Id"/>); 0 = nothing.</param>
/// <param name="Node">The node with that id while it is still inside the tree, else null.</param>
public readonly record struct PickResult(uint ObjectId, Node? Node)
{
    /// <summary>True when the pixel shows a pickable instance.</summary>
    public bool Hit => ObjectId != 0;

    public static PickResult Miss => default;
}

/// <summary>Ticket for a pick request; poll it with <c>TryGetPickResult</c>.</summary>
public readonly record struct PickHandle(ulong Value)
{
    public bool IsValid => Value != 0;
}

/// <summary>
/// GPU picking for one view: an object-ID render target (<c>R32_UINT</c> + depth, see
/// <see cref="MeshRenderer.ObjectIdTargetDesc"/>) and per-frame-slot readback buffers. A request is serviced by the
/// next frame that renders the ID pass: the requested pixels are copied into the frame slot's host-visible buffer
/// after the pass and read once that frame's fence has signalled (two frames later) — the frame never waits.
/// </summary>
internal sealed unsafe class ObjectIdPicker : IDisposable
{
    /// <summary>Requests serviced per frame; more wait for the following frames.</summary>
    public const int MaxRequestsPerFrame = 64;

    private const int Slots = IVulkanContext.MaxFramesInFlight;
    private const int MaxCompletedKept = 256;

    private readonly IVulkanContext _ctx;
    private readonly string _name;
    private readonly GpuBuffer[] _readback = new GpuBuffer[Slots];
    private readonly List<PendingPick> _queued = [];
    private readonly List<PendingPick>[] _inFlight = [[], []];
    private readonly ulong[] _inFlightFrame = new ulong[Slots];
    private readonly List<PendingPick> _misses = [];
    private readonly Dictionary<ulong, PickResult> _completed = [];
    private readonly Queue<ulong> _completedOrder = new();
    private ulong _nextHandle = 1;
    private bool _disposed;

    private readonly record struct PendingPick(ulong Handle, int X, int Y, TaskCompletionSource<PickResult>? Completion);

    public ObjectIdPicker(IVulkanContext ctx, string name)
    {
        _ctx = ctx;
        _name = name;
        for (var i = 0; i < Slots; i++)
            _readback[i] = GpuBuffer.Create(ctx, MaxRequestsPerFrame * sizeof(uint), BufferUsageFlags.TransferDstBit, GpuMemoryUsage.Readback);
    }

    /// <summary>The ID target (created by the first <see cref="Render"/>).</summary>
    public RenderTarget? Target { get; private set; }

    /// <summary>Requests waiting for a frame to render the ID pass.</summary>
    public bool HasQueued => _queued.Count > 0;

    /// <summary>Requests copied but not read back yet (or answered as misses, not yet delivered).</summary>
    public bool HasInFlight => _inFlight[0].Count > 0 || _inFlight[1].Count > 0 || _misses.Count > 0;

    /// <summary>Answers every queued request with a miss at the next <see cref="Collect"/> (the view cannot render).</summary>
    public void MissQueued()
    {
        _misses.AddRange(_queued);
        _queued.Clear();
    }

    public PickHandle Request(int x, int y, TaskCompletionSource<PickResult>? completion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var handle = _nextHandle++;
        _queued.Add(new PendingPick(handle, x, y, completion));
        return new PickHandle(handle);
    }

    /// <summary>The result of a finished request (removed once taken).</summary>
    public bool TryTake(PickHandle handle, out PickResult result) => _completed.Remove(handle.Value, out result);

    /// <summary>
    /// Records the ID pass into <see cref="Target"/> (resized to <paramref name="extent"/>) with
    /// <paramref name="drawIds"/>, then copies the queued pixels for readback. No render pass may be active.
    /// </summary>
    public void Render<TState>(CommandBuffer cb, Extent2D extent, TState state, Action<TState, CommandBuffer> drawIds)
    {
        ArgumentNullException.ThrowIfNull(drawIds);
        if (Target is null)
            Target = new RenderTarget(_ctx, MeshRenderer.ObjectIdTargetDesc(MeshRenderer.FindDepthFormat(_ctx)), extent);
        else
            Target.Resize(extent);

        Target.Begin(cb, default); // ids clear to 0, depth to 1
        drawIds(state, cb);
        Target.End(cb); // explicit barrier: the copies below read the ids the pass wrote
        CopyQueued(cb);
    }

    private void CopyQueued(CommandBuffer cb)
    {
        if (_queued.Count == 0)
            return;

        var slot = _ctx.FrameSlot;
        var batch = _inFlight[slot];
        if (batch.Count > 0)
            return; // the slot's previous requests are read back first (Collect runs before rendering)

        var image = Target!.GetColor(0).Handle;
        var extent = Target.Extent;
        var count = Math.Min(_queued.Count, MaxRequestsPerFrame);
        for (var i = 0; i < count; i++)
        {
            var request = _queued[i];
            batch.Add(request);
            if ((uint)request.X >= extent.Width || (uint)request.Y >= extent.Height)
                continue; // outside the view: reads back as a miss (the buffer slot is cleared below)
            var region = new BufferImageCopy
            {
                BufferOffset = (ulong)i * sizeof(uint),
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
                ImageOffset = new Offset3D(request.X, request.Y, 0),
                ImageExtent = new Extent3D(1, 1, 1),
            };
            _ctx.Vk.CmdCopyImageToBuffer(cb, image, ImageLayout.TransferSrcOptimal, _readback[slot].Handle, 1, &region);
        }

        _queued.RemoveRange(0, count);
        _readback[slot].MappedSpan.Clear();
        _inFlightFrame[slot] = _ctx.FrameNumber;

        // The copies must be visible to the host once the frame's fence has signalled.
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.HostReadBit,
        };
        _ctx.Vk.CmdPipelineBarrier(cb, PipelineStageFlags.TransferBit, PipelineStageFlags.HostBit, 0, 1, &barrier, 0, null, 0, null);
    }

    /// <summary>
    /// Resolves the requests whose frames have finished (<see cref="DeletionQueue.CompletedFrame"/>) through
    /// <paramref name="tree"/>. Polled results are stored; awaited ones are appended to <paramref name="completions"/>
    /// for the caller to complete once it is done iterating (continuations run inline). Allocation-free when nothing
    /// is in flight.
    /// </summary>
    public void Collect(SceneTree? tree, List<(TaskCompletionSource<PickResult> Completion, PickResult Result)> completions)
    {
        ArgumentNullException.ThrowIfNull(completions);
        if (!HasInFlight)
            return;
        foreach (var miss in _misses)
        {
            if (miss.Completion is { } completion)
                completions.Add((completion, PickResult.Miss));
            else
                Store(miss.Handle, PickResult.Miss);
        }

        _misses.Clear();
        var completedFrame = _ctx.Deletions.CompletedFrame;
        for (var slot = 0; slot < Slots; slot++)
        {
            var batch = _inFlight[slot];
            if (batch.Count == 0 || _inFlightFrame[slot] > completedFrame)
                continue;

            var ids = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(_readback[slot].MappedSpan);
            for (var i = 0; i < batch.Count; i++)
            {
                var id = ids[i];
                var result = id == 0 ? PickResult.Miss : new PickResult(id, tree?.Find(new NodeId(id)));
                var request = batch[i];
                if (request.Completion is { } completion)
                    completions.Add((completion, result));
                else
                    Store(request.Handle, result);
            }

            batch.Clear();
        }
    }

    private void Store(ulong handle, PickResult result)
    {
        _completed[handle] = result;
        _completedOrder.Enqueue(handle);
        // Polled results nobody takes are dropped after a while.
        while (_completedOrder.Count > MaxCompletedKept && _completedOrder.TryDequeue(out var old))
            _completed.Remove(old);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var request in _queued)
            request.Completion?.TrySetCanceled();
        foreach (var request in _misses)
            request.Completion?.TrySetCanceled();
        foreach (var batch in _inFlight)
            foreach (var request in batch)
                request.Completion?.TrySetCanceled();
        _queued.Clear();
        foreach (var buffer in _readback)
            buffer.Dispose();
        Target?.Dispose();
        Target = null;
    }

    public override string ToString() => $"ObjectIdPicker ({_name})";
}
