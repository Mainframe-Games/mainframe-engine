using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>A renderer debug view: replaces the main view's final image with an intermediate buffer (ADR 0163).</summary>
public enum RenderDebugView
{
    /// <summary>The normal image.</summary>
    None,

    /// <summary>
    /// The depth prepass's motion vectors (it runs while this is on): red = 128/255 + velocity.x × <see cref="VelocityDebugView.Scale"/>,
    /// green the same for velocity.y (screen UV per frame, +y down), blue = 128/255: still pixels are exactly 128 grey.
    /// Written as display values, so captures decode to the nearest 1/255 (<see cref="VelocityDebugView.Decode"/>).
    /// </summary>
    Velocity,
}

/// <summary>
/// <see cref="RenderDebugView.Velocity"/> as the last <see cref="PostStage.AfterTonemap"/> effect (ADR 0163): draws the
/// velocity image into the swapchain, encoded as <c>0.5 + v × </c><see cref="Scale"/> (<c>Post/VelocityView.vk.frag</c>).
/// It needs velocity, so it turns the prepass on. Allocates nothing per frame.
/// </summary>
internal sealed unsafe class VelocityDebugView() : PostEffect("velocity view", PostStage.AfterTonemap, PostEffectOrder.DebugView)
{
    /// <summary>Display units per screen-UV unit of motion: ±0.5 / 16 = ±1/32 of the screen per frame is the visible range.</summary>
    public const float Scale = 16f;

    /// <summary>The velocity (screen UV per frame) a captured 8-bit channel value of the view stands for.</summary>
    public static float Decode(byte value) => (value - 128) / (255f * Scale);

    private const int Sets = 4; // one per (LDR input, velocity or not): the stage's ping-pong has two inputs

    private IVulkanContext _ctx = null!;
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private PipelineLayout _layout;
    private readonly DescriptorSet[] _sets = new DescriptorSet[Sets];
    private readonly ulong[] _setInputs = new ulong[Sets];
    private readonly ulong[] _setVelocity = new ulong[Sets];
    private readonly PassPipelines _pipelines = new();

    public override PostEffectNeeds Needs => PostEffectNeeds.Velocity;

    public override bool IsEnabled(in PostEffectSettings settings) => settings.DebugView == RenderDebugView.Velocity;

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sampler).Check("vkCreateSampler (velocity view)");
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "velocity view");
        _pool = PipelineBuilder.CreatePool(ctx, Sets,
            [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 2 * Sets }], "velocity view");
        for (var i = 0; i < Sets; i++)
            _sets[i] = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "velocity view");
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(ViewPush), ShaderStageFlags.FragmentBit, "velocity view");
    }

    /// <summary>After a resize (device idle): the images are new, every set is rewritten on use.</summary>
    protected override void OnResize(PostEffectContext context)
    {
        Array.Clear(_setInputs);
        Array.Clear(_setVelocity);
    }

    protected override void OnRecord(PostEffectContext context)
    {
        var vk = _ctx.Vk;
        var cb = context.CommandBuffer;
        var scene = context.Scene;
        var hasVelocity = scene.HasPrepass && scene.Velocity.Handle != 0;
        var velocity = hasVelocity ? scene.Velocity : scene.Ldr; // a placeholder in the right layout when there is none
        var set = GetSet(scene.Ldr, velocity);

        var pass = context.OutputRenderPass;
        if (!_pipelines.TryGet(pass, out var pipeline))
            pipeline = _pipelines.Add(pass, PipelineBuilder.Create(_ctx, new PipelineState(), _layout, pass,
                "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/VelocityView.vk.frag.spv", [], [], "velocity view"));

        context.BeginOutput();
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        var push = new ViewPush
        {
            Scale = Scale,
            Flags = (hasVelocity ? 1u : 0u) | (context.OutputEncodesSrgb ? 2u : 0u),
        };
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(ViewPush), &push);
        PipelineBuilder.SetViewport(vk, cb, scene.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        context.EndOutput();
    }

    // One set per (LDR input, velocity) pair, written once: a set a frame in flight may bind is never rewritten. The
    // images only change on resize (device idle, OnResize forgets them).
    private DescriptorSet GetSet(ImageView input, ImageView velocity)
    {
        for (var i = 0; i < Sets; i++)
            if (_setInputs[i] == input.Handle && _setVelocity[i] == velocity.Handle)
                return _sets[i];
        for (var i = 0; i < Sets; i++)
        {
            if (_setInputs[i] != 0)
                continue;
            _setInputs[i] = input.Handle;
            _setVelocity[i] = velocity.Handle;
            Write(_sets[i], 0, input);
            Write(_sets[i], 1, velocity);
            return _sets[i];
        }

        throw new InvalidOperationException("More inputs than velocity-view sets.");
    }

    private void Write(DescriptorSet set, uint binding, ImageView view) =>
        PipelineBuilder.WriteImage(_ctx, set, binding, new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });

    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        _pipelines.Destroy(_ctx);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _sampler, null);
    }

    // VelocityView.vk.frag's push block.
    private struct ViewPush
    {
        public float Scale;
        public uint Flags;
    }
}
