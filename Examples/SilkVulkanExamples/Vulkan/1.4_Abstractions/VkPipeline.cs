using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace SilkVulkanExamples.Vulkan;

/// <summary>
/// Wraps a Vulkan graphics pipeline + layout.
/// </summary>
public unsafe class VkPipeline : IDisposable
{
    private readonly ExampleBase _owner;

    public Pipeline Handle { get; }
    public PipelineLayout Layout { get; }

    public VkPipeline(
        ExampleBase owner,
        PipelineLayoutCreateInfo layoutCreateInfo,
        GraphicsPipelineCreateInfo pipelineCreateInfo)
    {
        _owner = owner;

        if (owner.Vk.CreatePipelineLayout(owner.Device, in layoutCreateInfo, null, out var layout) != Result.Success)
            throw new InvalidOperationException("failed to create pipeline layout!");

        Layout = layout;

        var pipelineInfoWithLayout = pipelineCreateInfo;
        pipelineInfoWithLayout.Layout = layout;

        if (owner.Vk.CreateGraphicsPipelines(owner.Device, default, 1, in pipelineInfoWithLayout, null, out var pipeline) != Result.Success)
            throw new InvalidOperationException("failed to create graphics pipeline!");

        Handle = pipeline;
    }

    public void Dispose()
    {
        _owner.Vk.DestroyPipeline(_owner.Device, Handle, null);
        _owner.Vk.DestroyPipelineLayout(_owner.Device, Layout, null);
    }
}
