using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The editor grid (<see cref="GridStyle.Analytic"/>): one camera-centred quad on a horizontal plane whose fragment
/// shader (<c>Shaders/SceneGrid/GridAnalytic</c>) draws minor and major lines anti-aliased at their width in pixels,
/// steps the cell size ×10 as the lines crowd (cross-fading), colours the X and Z axes and fades towards the horizon.
/// Depth-tested against the scene, no depth writes; drawn after post in a post-processed view like the line grid.
/// </summary>
internal sealed class SceneGridAnalytic : IDisposable
{
    private readonly IVulkanContext? _ctx;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private RenderPass _afterPostPass;
    private Pipeline _afterPostPipeline;
    private bool _disposed;

    private struct Push
    {
        public Vector4 Params; // x = pixel scale, y = plane height, z = base cell size
    }

    public SceneGridAnalytic(IRenderer renderer)
    {
        if (renderer is not IVulkanContext ctx)
            return;
        _ctx = ctx;
        _layout = ctx.Frame.CreatePipelineLayout(null, [], "analytic grid");
        _pipeline = CreatePipeline(ctx, ctx.RenderPass, displayOutput: false);
    }

    /// <summary>Line widths are in points: framebuffer pixels per point (the editor's view scale).</summary>
    public float PixelScale { get; set; } = 1f;

    /// <summary>Height of the grid plane.</summary>
    public float PlaneY { get; set; }

    /// <summary>The minor cell size in world units at the finest level (1 m).</summary>
    public float CellSize { get; set; } = 1f;

    public void Draw(ICamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (_ctx is { FrameStarted: true })
            Draw(camera, _pipeline);
    }

    /// <summary>Draws into a post-processed sub-viewport's overlay pass (ADR 0169), after its effects.</summary>
    public void DrawAfterPost(ICamera camera, RenderPass renderPass)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (_ctx is not { FrameStarted: true } ctx)
            return;
        if (_afterPostPass.Handle != renderPass.Handle)
        {
            if (_afterPostPipeline.Handle != 0)
                ctx.Deletions.Enqueue(GpuDeletion.Of(_afterPostPipeline));
            _afterPostPipeline = CreatePipeline(ctx, renderPass, displayOutput: true);
            _afterPostPass = renderPass;
        }

        Draw(camera, _afterPostPipeline);
    }

    private unsafe void Draw(ICamera camera, Pipeline pipeline)
    {
        var ctx = _ctx!;
        var vk = ctx.Vk;
        var cb = ctx.CurrentCommandBuffer;
        ctx.Frame.EnsureCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);
        PipelineBuilder.SetViewport(vk, cb, ctx.Frame.Extent, flipY: true);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        ctx.Frame.Bind(cb, _layout);
        var push = new Push { Params = new Vector4(MathF.Max(PixelScale, 0.25f), PlaneY, MathF.Max(CellSize, 1e-4f), 0f) };
        vk.CmdPushConstants(cb, _layout, FrameContext.PushConstantStages, 0, (uint)sizeof(Push), &push);
        vk.CmdDraw(cb, 6, 1, 0, 0);
    }

    private unsafe Pipeline CreatePipeline(IVulkanContext ctx, RenderPass renderPass, bool displayOutput)
    {
        var state = new PipelineState
        {
            DepthTest = true,
            DepthWrite = false,
            DepthCompare = CompareOp.LessOrEqual,
            Blend = displayOutput ? BlendMode.Alpha : BlendMode.AlphaKeepSourceAlpha,
        };
        var display = displayOutput ? 1u : 0u;
        var entry = new SpecializationMapEntry { ConstantID = 0, Offset = 0, Size = sizeof(uint) };
        var specialization = new SpecializationInfo { MapEntryCount = 1, PMapEntries = &entry, DataSize = sizeof(uint), PData = &display };
        return PipelineBuilder.Create(ctx, state, _layout, renderPass,
            "Shaders/SceneGrid/GridAnalytic.vk.vert.spv", "Shaders/SceneGrid/GridAnalytic.vk.frag.spv",
            [], [], displayOutput ? "analytic grid (after post)" : "analytic grid", &specialization);
    }

    /// <summary>Releases the GPU objects through the deletion queue (safe while frames are in flight).</summary>
    public void Dispose()
    {
        if (_ctx is null || _disposed)
            return;
        _disposed = true;
        if (_pipeline.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_pipeline));
        if (_afterPostPipeline.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_afterPostPipeline));
        if (_layout.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_layout));
    }
}
