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
/// averages it), without TAA every ray starts half a step out. Since ADR 0170 it writes the whole screen-space occlusion
/// image the lit shaders read at set 0 binding 5 (<see cref="FrameContext.AmbientOcclusionBinding"/>,
/// <see cref="OutputFormat"/>): SSAO's AO and bent normal copied from its output when SSAO ran (else 1 and none), the
/// contact shadow in g and the view depth in b, so binding 6 is free for the light probes. The lit shaders multiply the
/// primary light's cascaded shadow by g on the surface the prepass drew. Allocates nothing per frame.
/// </summary>
internal sealed unsafe class ContactShadows() : PostEffect("contact shadows", PostStage.AfterPrepass, PostEffectOrder.ContactShadows)
{
    /// <summary>The output: r = AO, g = shadow (1 = lit), b = the pixel's view depth, a = the packed bent normal.</summary>
    public const Format OutputFormat = Format.R16G16B16A16Sfloat;

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
    private DescriptorSet _set;      // without SSAO: binding 1 is a placeholder the shader never reads
    private DescriptorSet _ssaoSet;  // with SSAO: binding 1 is SSAO's output (written the first frame SSAO is on)
    private bool _ssaoSetWritten;
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
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "contact shadows");
        _pool = PipelineBuilder.CreatePool(ctx, 2, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 4 }], "contact shadows");
        _set = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "contact shadows");
        _ssaoSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "contact shadows + ssao");
        WriteSets(context);
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(ContactPush), ShaderStageFlags.FragmentBit, "contact shadows");
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, target.RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/ContactShadows.vk.frag.spv", [], [], "contact shadows");
    }

    // SSAO runs this frame (it is first in the stage): its output is copied into ours.
    private static bool SsaoRuns(PostEffectContext context) => context.Settings.World.SsaoEnabled && context.Scene.HasPrepass;

    // Binds this frame's output for the lit shaders before the frame set is first bound (the image is written in
    // OnRecord, before the scene pass reads it). It replaces SSAO's binding (set earlier in the stage's OnBeginFrame),
    // keeping SSAO's light-affect settings.
    protected override void OnBeginFrame(PostEffectContext context)
    {
        if (!context.Scene.HasPrepass)
            return; // no AfterPrepass stage this frame: the lit shaders keep the white image
        var target = context.Targets.Get(Target);
        if (_targetGeneration != context.Targets.Generation)
        {
            _targetGeneration = context.Targets.Generation;
            _id++;
        }

        var ssao = SsaoRuns(context);
        var world = context.Settings.World;
        context.Vulkan.Frame.SetAmbientOcclusion(new DescriptorImageInfo
        {
            Sampler = context.Scene.LinearSampler,
            ImageView = target.GetColor(0).View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        }, _id, ssao ? world.SsaoLightAffect : 0f, ssao ? world.SsaoAoChannelAffect : 0f);
    }

    protected override void OnResize(PostEffectContext context) => WriteSets(context);

    protected override void OnRecord(PostEffectContext context)
    {
        if (!context.HasCamera || !context.Scene.HasPrepass)
            return;
        var camera = context.Camera;
        var projection = camera.JitteredProjection;
        var settings = context.Settings.ContactShadows;
        var towards = Vector3.TransformNormal(settings.TowardsLight, camera.View);
        // Orthographic cameras get no contact shadows (a perspective feature), but the image still carries SSAO's.
        var march = projection.M34 != 0f && towards.LengthSquared() >= 1e-12f;
        var ssao = SsaoRuns(context);
        if (ssao && !_ssaoSetWritten)
            WriteSsaoSet(context);

        var target = context.Targets.Get(Target);
        var extent = target.Extent;
        var temporal = camera.Jitter != Vector2.Zero; // TAA averages a per-frame noise; otherwise it stays put
        var push = new ContactPush
        {
            Projection = new Vector4(projection.M11, projection.M22, projection.M31, projection.M32),
            Depth = new Vector4(projection.M33, projection.M43, extent.Width, extent.Height),
            Light = new Vector4(march ? Vector3.Normalize(towards) : Vector3.UnitY, settings.Length),
            Params = new Vector4(Thickness, march ? Steps : 0, temporal ? context.FrameNumber % 64 : -1f, MaxDistance),
            Flags = new Vector4(ssao ? 1f : 0f, 0f, 0f, 0f),
        };

        var vk = _ctx.Vk;
        var cb = context.CommandBuffer;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);
        var set = ssao ? _ssaoSet : _set;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(ContactPush), &push);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
        DrawnFrame = context.FrameNumber;
    }

    private static DescriptorImageInfo Depth(PostEffectContext context) => new()
    {
        Sampler = context.Scene.PointSampler,
        ImageView = context.Scene.Depth,
        ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
    };

    private void WriteSets(PostEffectContext context)
    {
        var depth = Depth(context);
        PipelineBuilder.WriteImage(_ctx, _set, 0, depth);
        PipelineBuilder.WriteImage(_ctx, _set, 1, depth); // unread: every set is complete
        _ssaoSetWritten = false;
        if (SsaoRuns(context))
            WriteSsaoSet(context);
    }

    // The set with SSAO's output at binding 1: written the first frame SSAO is on (it has never been bound, so no frame in
    // flight reads it) and after a resize (device idle). SSAO's target lives in the same pool, sized with ours.
    private void WriteSsaoSet(PostEffectContext context)
    {
        PipelineBuilder.WriteImage(_ctx, _ssaoSet, 0, Depth(context));
        PipelineBuilder.WriteImage(_ctx, _ssaoSet, 1, new DescriptorImageInfo
        {
            Sampler = context.Scene.PointSampler,
            ImageView = context.Targets.Get(SsaoEffect.OutputTarget).GetColor(0).View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });
        _ssaoSetWritten = true;
    }

    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
    }

    // ContactShadows.vk.frag's push block (std430, 80 bytes).
    private struct ContactPush
    {
        public Vector4 Projection;
        public Vector4 Depth;
        public Vector4 Light;
        public Vector4 Params;
        public Vector4 Flags;
    }
}
