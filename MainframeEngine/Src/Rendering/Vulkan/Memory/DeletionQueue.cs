using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>Kind of Vulkan object in a <see cref="GpuDeletion"/>.</summary>
public enum GpuObjectKind : byte
{
    /// <summary>Only the memory (no object handle).</summary>
    Allocation,
    Buffer,
    Image,
    ImageView,
    Sampler,
    Framebuffer,
    RenderPass,
    Pipeline,
    PipelineLayout,
    DescriptorPool,
    DescriptorSetLayout,
    ShaderModule,

    /// <summary>A descriptor set returned to its pool (<see cref="GpuDeletion.Parent"/>, created with FREE_DESCRIPTOR_SET).</summary>
    DescriptorSet,
    QueryPool,
}

/// <summary>
/// One deferred destruction: an object handle and, for buffers and images, the memory bound to it; for descriptor
/// sets, the pool they return to (<see cref="Parent"/>).
/// </summary>
public readonly record struct GpuDeletion(GpuObjectKind Kind, ulong Handle, GpuAllocation Allocation = default, ulong Parent = 0)
{
    /// <summary>Frees <paramref name="set"/> back to <paramref name="pool"/> (which must allow freeing individual sets).</summary>
    public static GpuDeletion Of(DescriptorPool pool, DescriptorSet set) => new(GpuObjectKind.DescriptorSet, set.Handle, default, pool.Handle);
    public static GpuDeletion Of(Silk.NET.Vulkan.Buffer buffer, in GpuAllocation allocation) => new(GpuObjectKind.Buffer, buffer.Handle, allocation);
    public static GpuDeletion Of(Image image, in GpuAllocation allocation) => new(GpuObjectKind.Image, image.Handle, allocation);
    public static GpuDeletion Of(ImageView view) => new(GpuObjectKind.ImageView, view.Handle);
    public static GpuDeletion Of(Sampler sampler) => new(GpuObjectKind.Sampler, sampler.Handle);
    public static GpuDeletion Of(Framebuffer framebuffer) => new(GpuObjectKind.Framebuffer, framebuffer.Handle);
    public static GpuDeletion Of(RenderPass renderPass) => new(GpuObjectKind.RenderPass, renderPass.Handle);
    public static GpuDeletion Of(Pipeline pipeline) => new(GpuObjectKind.Pipeline, pipeline.Handle);
    public static GpuDeletion Of(PipelineLayout layout) => new(GpuObjectKind.PipelineLayout, layout.Handle);
    public static GpuDeletion Of(DescriptorPool pool) => new(GpuObjectKind.DescriptorPool, pool.Handle);
    public static GpuDeletion Of(DescriptorSetLayout layout) => new(GpuObjectKind.DescriptorSetLayout, layout.Handle);
    public static GpuDeletion Of(ShaderModule module) => new(GpuObjectKind.ShaderModule, module.Handle);
    public static GpuDeletion Of(QueryPool pool) => new(GpuObjectKind.QueryPool, pool.Handle);
    public static GpuDeletion Of(in GpuAllocation allocation) => new(GpuObjectKind.Allocation, 0, allocation);
}

/// <summary>Destroys what a <see cref="DeletionQueue"/> releases (Vulkan in the engine, a recorder in tests).</summary>
internal interface IGpuDestroyer
{
    void Destroy(in GpuDeletion deletion);
}

/// <summary>
/// Defers destroying GPU objects until the frames that may still use them have finished, instead of
/// <c>vkDeviceWaitIdle</c> in every <c>Dispose</c>. An object enqueued while frame <i>N</i> is being recorded is
/// destroyed once frame <i>N</i>'s frame-slot fence has signalled, which the renderer observes at the start of
/// frame <i>N</i> + <see cref="IVulkanContext.MaxFramesInFlight"/>. An object enqueued between frames (load time,
/// <c>OnUpdate</c>) waits for the <i>next</i> frame too, because that frame records the upload queue's pending
/// copies, which may still reference it.
/// </summary>
/// <remarks>
/// Keyed by frame number rather than slot index so objects released between frames (after a submit, before
/// the next acquire) wait for the right fence. FIFO: the queue is ordered by frame, so collection stops at
/// the first entry that is still in use. Allocation-free once the backing array has grown. Render thread only.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "It is a queue (FIFO by frame); the name is the established graphics term.")]
public sealed class DeletionQueue
{
    private readonly IGpuDestroyer _destroyer;
    private readonly Queue<(ulong Frame, GpuDeletion Item)> _pending = new(64);

    internal DeletionQueue(IGpuDestroyer destroyer)
    {
        _destroyer = destroyer;
    }

    /// <summary>The newest frame number that has started recording (objects released now may be used by it).</summary>
    public ulong CurrentFrame { get; private set; }

    /// <summary>True between the renderer's <see cref="BeginFrame"/> and <see cref="EndFrame"/>.</summary>
    public bool IsRecording { get; private set; }

    /// <summary>Every frame up to and including this one has finished on the GPU.</summary>
    public ulong CompletedFrame { get; private set; }

    /// <summary>Objects waiting for their frames to finish.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Destroys <paramref name="item"/> once every frame that may reference it has finished.</summary>
    public void Enqueue(in GpuDeletion item)
    {
        if (item.Handle == 0 && item.Allocation.IsNull)
            return;
        _pending.Enqueue((IsRecording ? CurrentFrame : CurrentFrame + 1, item));
    }

    /// <summary>Renderer: frame <paramref name="frame"/> starts recording.</summary>
    internal void BeginFrame(ulong frame)
    {
        if (frame < CurrentFrame)
            throw new ArgumentOutOfRangeException(nameof(frame), "Frame numbers only increase.");
        CurrentFrame = frame;
        IsRecording = true;
    }

    /// <summary>Renderer: the current frame has been submitted.</summary>
    internal void EndFrame() => IsRecording = false;

    /// <summary>Renderer: every frame up to <paramref name="completedFrame"/> has finished; destroy what they held.</summary>
    internal void Collect(ulong completedFrame)
    {
        if (completedFrame > CompletedFrame)
            CompletedFrame = completedFrame;

        while (_pending.TryPeek(out var head) && head.Frame <= CompletedFrame)
        {
            _pending.Dequeue();
            _destroyer.Destroy(head.Item);
        }
    }

    /// <summary>Destroys everything now. Only when the device is idle (swapchain teardown, shutdown).</summary>
    internal void FlushAll()
    {
        IsRecording = false;
        CompletedFrame = CurrentFrame;
        while (_pending.TryDequeue(out var entry))
            _destroyer.Destroy(entry.Item);
    }
}

/// <summary><see cref="IGpuDestroyer"/> for a Vulkan device: destroys the handle, then frees its memory.</summary>
internal sealed unsafe class VulkanDestroyer(Vk vk, Device device, GpuAllocator allocator) : IGpuDestroyer
{
    public void Destroy(in GpuDeletion d)
    {
        switch (d.Kind)
        {
            case GpuObjectKind.Allocation: break;
            case GpuObjectKind.Buffer: vk.DestroyBuffer(device, new Silk.NET.Vulkan.Buffer(d.Handle), null); break;
            case GpuObjectKind.Image: vk.DestroyImage(device, new Image(d.Handle), null); break;
            case GpuObjectKind.ImageView: vk.DestroyImageView(device, new ImageView(d.Handle), null); break;
            case GpuObjectKind.Sampler: vk.DestroySampler(device, new Sampler(d.Handle), null); break;
            case GpuObjectKind.Framebuffer: vk.DestroyFramebuffer(device, new Framebuffer(d.Handle), null); break;
            case GpuObjectKind.RenderPass: vk.DestroyRenderPass(device, new RenderPass(d.Handle), null); break;
            case GpuObjectKind.Pipeline: vk.DestroyPipeline(device, new Pipeline(d.Handle), null); break;
            case GpuObjectKind.PipelineLayout: vk.DestroyPipelineLayout(device, new PipelineLayout(d.Handle), null); break;
            case GpuObjectKind.DescriptorPool: vk.DestroyDescriptorPool(device, new DescriptorPool(d.Handle), null); break;
            case GpuObjectKind.DescriptorSetLayout: vk.DestroyDescriptorSetLayout(device, new DescriptorSetLayout(d.Handle), null); break;
            case GpuObjectKind.ShaderModule: vk.DestroyShaderModule(device, new ShaderModule(d.Handle), null); break;
            case GpuObjectKind.QueryPool: vk.DestroyQueryPool(device, new QueryPool(d.Handle), null); break;
            case GpuObjectKind.DescriptorSet:
                {
                    var set = new DescriptorSet(d.Handle);
                    vk.FreeDescriptorSets(device, new DescriptorPool(d.Parent), 1, &set).Check("vkFreeDescriptorSets");
                    break;
                }
            default: throw new ArgumentOutOfRangeException(nameof(d), d.Kind, "Unknown GPU object kind.");
        }

        allocator.Free(d.Allocation);
    }
}
