using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The primary directional light's contact shadows for this frame's post effects (ADR 0167): the render server fills it
/// from the root world's first shadowed <see cref="DirectionalLight"/> when the light asks for them
/// (<see cref="DirectionalLight.ContactShadows"/>) and the shadow system allows them (<see cref="ShadowSystem.ContactShadows"/>).
/// </summary>
/// <param name="Enabled">The light has contact shadows this frame.</param>
/// <param name="TowardsLight">World direction towards the light (normalized).</param>
/// <param name="Length">How far the march reaches, in metres (<see cref="DirectionalLight.ContactShadowLength"/>).</param>
internal readonly record struct ContactShadowSettings(bool Enabled, Vector3 TowardsLight, float Length)
{
    /// <summary>The light's contact shadows when they apply (<paramref name="allowed"/>: the shadow system allows them).</summary>
    public static ContactShadowSettings For(DirectionalLight? light, bool allowed) =>
        light is { CastsShadows: true, ContactShadows: true } && allowed && light.Direction.LengthSquared() > 1e-12f
            ? new ContactShadowSettings(true, -Vector3.Normalize(light.Direction), light.ContactShadowLength)
            : default;
}

/// <summary>
/// Screen-space contact shadows (ADR 0167, G8e.2), an <see cref="PostStage.AfterPrepass"/> effect after SSAO: one
/// full-resolution pass (<c>Post/ContactShadows.vk.frag</c>) marches <see cref="Steps"/> steps from every prepass pixel
/// towards the primary light over its <see cref="DirectionalLight.ContactShadowLength"/>, against the prepass depth, with a
/// <see cref="Thickness"/> test; with TAA the start of each ray is jittered by interleaved gradient noise every frame (TAA
/// averages it), without TAA every ray starts half a step out. Its <c>R16G16_SFLOAT</c> output (shadow, view depth) is bound at set 0, binding 6
/// (<see cref="FrameContext.ContactShadowBinding"/>) for the lit shaders, which multiply the primary light's cascaded
/// shadow by it on the surface the prepass drew. A separate target from SSAO's (lane S owns binding 5): G8e.1 folds it into
/// the AO target's G channel. Allocates nothing per frame.
/// </summary>
internal sealed unsafe class ContactShadows() : PostEffect("contact shadows", PostStage.AfterPrepass, PostEffectOrder.ContactShadows)
{
    /// <summary>The output: r = shadow (1 = lit), g = the pixel's view depth.</summary>
    public const Format OutputFormat = Format.R16G16Sfloat;

    /// <summary>March steps per pixel.</summary>
    public const int Steps = 12;

    /// <summary>How thick an occluder is assumed to be (m; grows by 1 % of the view depth).</summary>
    public const float Thickness = 0.2f;

    /// <summary>View distance where contact shadows end (they fade over its last 30 %): the cascades take over.</summary>
    public const float MaxDistance = 60f;

    private static readonly PostTargetDesc Target = new("contact shadows", OutputFormat);

    private IVulkanContext _ctx = null!;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _set;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private int _targetGeneration = -1;
    private long _id;

    public override PostEffectNeeds Needs => PostEffectNeeds.DepthPrepass;

    public override bool IsEnabled(in PostEffectSettings settings) => settings.ContactShadows.Enabled;

    /// <summary>The frame the output was last written (tests).</summary>
    public ulong DrawnFrame { get; private set; }

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
        var target = context.Targets.Get(Target);
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "contact shadows");
        _pool = PipelineBuilder.CreatePool(ctx, 1, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 }], "contact shadows");
        _set = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "contact shadows");
        WriteSet(context);
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(ContactPush), ShaderStageFlags.FragmentBit, "contact shadows");
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, target.RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/ContactShadows.vk.frag.spv", [], [], "contact shadows");
    }

    // Binds this frame's output for the lit shaders before the frame set is first bound (the image is written in
    // OnRecord, before the scene pass reads it).
    protected override void OnBeginFrame(PostEffectContext context)
    {
        var target = context.Targets.Get(Target);
        if (_targetGeneration != context.Targets.Generation)
        {
            _targetGeneration = context.Targets.Generation;
            _id++;
        }

        context.Vulkan.Frame.SetContactShadows(new DescriptorImageInfo
        {
            Sampler = context.Scene.LinearSampler,
            ImageView = target.GetColor(0).View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        }, _id);
    }

    protected override void OnResize(PostEffectContext context) => WriteSet(context);

    protected override void OnRecord(PostEffectContext context)
    {
        if (!context.HasCamera || !context.Scene.HasPrepass)
            return;
        var camera = context.Camera;
        var projection = camera.JitteredProjection;
        if (projection.M34 == 0f)
            return; // orthographic: contact shadows are a perspective feature (the binding reads 1 anyway)

        var settings = context.Settings.ContactShadows;
        var towards = Vector3.TransformNormal(settings.TowardsLight, camera.View);
        if (towards.LengthSquared() < 1e-12f)
            return;
        var target = context.Targets.Get(Target);
        var extent = target.Extent;
        var temporal = camera.Jitter != Vector2.Zero; // TAA averages a per-frame noise; otherwise it stays put
        var push = new ContactPush
        {
            Projection = new Vector4(projection.M11, projection.M22, projection.M31, projection.M32),
            Depth = new Vector4(projection.M33, projection.M43, extent.Width, extent.Height),
            Light = new Vector4(Vector3.Normalize(towards), settings.Length),
            Params = new Vector4(Thickness, Steps, temporal ? context.FrameNumber % 64 : -1f, MaxDistance),
        };

        var vk = _ctx.Vk;
        var cb = context.CommandBuffer;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var set = _set;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(ContactPush), &push);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
        DrawnFrame = context.FrameNumber;
    }

    private void WriteSet(PostEffectContext context) =>
        PipelineBuilder.WriteImage(_ctx, _set, 0, new DescriptorImageInfo
        {
            Sampler = context.Scene.PointSampler,
            ImageView = context.Scene.Depth,
            ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
        });

    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
    }

    // ContactShadows.vk.frag's push block (std430, 64 bytes).
    private struct ContactPush
    {
        public Vector4 Projection;
        public Vector4 Depth;
        public Vector4 Light;
        public Vector4 Params;
    }
}
