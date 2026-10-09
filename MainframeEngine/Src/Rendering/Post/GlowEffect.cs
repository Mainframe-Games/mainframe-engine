using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// Godot 4.7's glow blur chain (ADR 0124; <c>copy_effects.cpp</c> <c>gaussian_glow</c> + <c>copy.glsl</c>) as raster
/// passes: level k (k = 0..6) is the scene at <c>1 / 2^(k+1)</c> resolution, each built from the level before it by a
/// horizontal pass (downsample + 9-tap blur, into <c>temp[k]</c>) and a vertical pass (9-tap blur + strength, and on
/// level 0 the firefly undo, exposure (× auto exposure's when it is on, ADR 0154), HDR threshold and luminance cap, into
/// <c>level[k]</c>). Only the levels up to the
/// highest weighted one are drawn each frame (Godot's <c>max_glow_index</c>); every level image exists and is cleared
/// once after creation, so the tonemap pass can bind all seven. A <see cref="PostStage.BeforeTonemap"/> effect (ADR 0163)
/// after auto exposure, enabled whenever the post tonemap pass runs (it binds every level either way); allocates nothing
/// per frame.
/// <para><see cref="GlowQuality.High"/> (ADR 0168) builds the same images differently: <c>temp[k]</c> holds Jimenez's
/// 13-tap downsample of the level above (<c>GlowDownsample</c>, Karis-averaged with exposure and threshold on level 0)
/// and <c>level[k]</c> the tent-filtered chain coming back up (<c>GlowUpsample</c>: <c>level[k] = tent(level[k+1]) +
/// weight_k · temp[k]</c>), so <c>level[0]</c> holds the whole weighted glow and the tonemap reads it alone
/// (<see cref="CombinedWeights"/>).</para>
/// </summary>
internal sealed unsafe class GlowEffect(AutoExposure autoExposure) : PostEffect("glow", PostStage.BeforeTonemap, PostEffectOrder.Glow)
{
    public const int LevelCount = PostProcessSettings.GlowLevelCount;
    private const Format LevelFormat = Format.R16G16B16A16Sfloat;

    private IVulkanContext _ctx = null!;
    private readonly RenderTarget[] _temp = new RenderTarget[LevelCount];
    private readonly RenderTarget[] _levels = new RenderTarget[LevelCount];
    private readonly DescriptorSet[] _tempSets = new DescriptorSet[LevelCount];
    private readonly DescriptorSet[] _levelSets = new DescriptorSet[LevelCount];
    private Sampler _sampler;
    private DescriptorSetLayout _setLayout;
    private DescriptorPool _pool;
    private DescriptorSet _sceneSet;
    private PipelineLayout _layout;
    private Pipeline _pipeline;
    private DescriptorImageInfo _adaptedLuminance;
    private bool _needsClear = true;

    // GlowQuality.High (ADR 0168), created the first frame it is used: the downsample reads one set (the existing scene
    // and temp sets), the upsample two (set 0: the level below's result, set 1: this level's downsample).
    private Pipeline _downPipeline;
    private PipelineLayout _upLayout;
    private Pipeline _upPipeline;

    /// <summary>Runs whenever the post tonemap pass does: the tonemap binds every level either way.</summary>
    public override bool IsEnabled(in PostEffectSettings settings) => settings.PostTonemap;

    protected override void OnCreate(PostEffectContext context)
    {
        autoExposure.Create(context); // its adapted luminance is read by the first level
        var ctx = context.Vulkan;
        var sceneExtent = context.Scene.Extent;
        var sceneView = context.Scene.Color;
        _ctx = ctx;
        _adaptedLuminance = autoExposure.AdaptedDescriptor;
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sampler).Check("vkCreateSampler (glow)");

        for (var k = 0; k < LevelCount; k++)
        {
            var size = LevelExtent(sceneExtent, k);
            _temp[k] = new RenderTarget(ctx, new RenderTargetDesc($"glow temp {k}", [RenderTargetAttachment.Sampled(LevelFormat)], null), size);
            _levels[k] = new RenderTarget(ctx, new RenderTargetDesc($"glow level {k}", [RenderTargetAttachment.Sampled(LevelFormat)], null), size);
        }

        // Binding 1: auto exposure's adapted luminance (ADR 0154), read by the first level's vertical pass.
        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "glow");
        const uint sets = 1 + 2 * LevelCount;
        _pool = PipelineBuilder.CreatePool(ctx, sets, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 2 * sets }], "glow");
        _sceneSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "glow scene");
        for (var k = 0; k < LevelCount; k++)
        {
            _tempSets[k] = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "glow temp");
            _levelSets[k] = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "glow level");
        }

        WriteSets(sceneView);
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(GlowPush), ShaderStageFlags.FragmentBit, "glow");
        _pipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _temp[0].RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/GlowBlur.vk.frag.spv", [], [], "glow");
    }

    /// <summary>The sampler the tonemap reads the levels with (linear, clamp to edge).</summary>
    public Sampler Sampler => _sampler;

    /// <summary>Level <paramref name="k"/>'s image, in shader-read layout once <see cref="Record"/> has run.</summary>
    public ImageView LevelView(int k) => _levels[k].GetColor(0).View;

    /// <summary>Godot's BLUR_1 mip k: half the scene size, halved per level, at least one pixel.</summary>
    public static Extent2D LevelExtent(Extent2D scene, int k) =>
        new(Math.Max(1u, scene.Width >> (k + 1)), Math.Max(1u, scene.Height >> (k + 1)));

    /// <summary>After a swapchain resize (device idle): resized levels, rewritten sets; they are cleared again.</summary>
    protected override void OnResize(PostEffectContext context)
    {
        var sceneExtent = context.Scene.Extent;
        var sceneView = context.Scene.Color;
        for (var k = 0; k < LevelCount; k++)
        {
            var size = LevelExtent(sceneExtent, k);
            _temp[k].Resize(size);
            _levels[k].Resize(size);
        }

        WriteSets(sceneView);
        _needsClear = true;
    }

    protected override void OnRecord(PostEffectContext context) =>
        Record(context.CommandBuffer, context.Settings.World, context.Exposure);

    /// <summary>Records the blur chain for <paramref name="settings"/> (nothing when its glow is off, after the first clear).</summary>
    private void Record(CommandBuffer cb, in PostProcessSettings settings, float exposure)
    {
        if (_needsClear)
        {
            // Every level the tonemap binds must hold defined data in shader-read layout, drawn this frame or not.
            for (var k = 0; k < LevelCount; k++)
            {
                _temp[k].Begin(cb, default);
                _temp[k].End(cb);
                _levels[k].Begin(cb, default);
                _levels[k].End(cb);
            }

            _needsClear = false;
        }

        var max = settings.GlowMaxLevel;
        if (settings.GlowQuality == GlowQuality.High)
        {
            RecordHigh(cb, settings, exposure, max);
            return;
        }

        for (var k = 0; k <= max; k++)
        {
            var first = k == 0;
            var push = new GlowPush
            {
                Strength = settings.GlowStrength,
                Exposure = exposure,
                Threshold = settings.GlowHdrThreshold,
                Scale = settings.GlowHdrScale,
                Bloom = settings.GlowBloom,
                LuminanceCap = settings.GlowHdrLuminanceCap,
                AutoExposureScale = settings.AutoExposureScale,
            };
            var autoExposure = settings.AutoExposureEnabled ? 4u : 0u;
            push.Flags = 1u | (first ? 2u : 0u);
            Pass(cb, _temp[k], first ? _sceneSet : _levelSets[k - 1], ref push);
            push.Flags = (first ? 2u : 0u) | autoExposure;
            Pass(cb, _levels[k], _tempSets[k], ref push);
        }
    }

    /// <summary>
    /// The tent's tap spacing in texels of the lower level on the way up (<see cref="GlowQuality.High"/>): 2 spreads each
    /// level about as wide as Godot's 9-tap Gaussian does, so the level weights keep their meaning.
    /// </summary>
    public const float UpsampleRadius = 2f;

    /// <summary>
    /// The tonemap's level weights for <see cref="GlowQuality.High"/>: level 0 already holds the weighted sum of every
    /// level, so it is read alone at weight 1.
    /// </summary>
    public static void CombinedWeights(Span<float> weights)
    {
        weights.Clear();
        weights[0] = 1f;
    }

    // GlowQuality.High: 13-tap downsamples into temp[0..max], then the tent chain up into level[max..0].
    private void RecordHigh(CommandBuffer cb, in PostProcessSettings settings, float exposure, int max)
    {
        if (max < 0)
            return;
        EnsureHigh();
        var vk = _ctx.Vk;
        for (var k = 0; k <= max; k++)
        {
            var first = k == 0;
            var push = new GlowPush
            {
                Strength = settings.GlowStrength,
                Exposure = exposure,
                Threshold = settings.GlowHdrThreshold,
                Scale = settings.GlowHdrScale,
                Bloom = settings.GlowBloom,
                LuminanceCap = settings.GlowHdrLuminanceCap,
                AutoExposureScale = settings.AutoExposureScale,
                Flags = (first ? 2u : 0u) | (settings.AutoExposureEnabled ? 4u : 0u),
            };
            Pass(cb, _temp[k], first ? _sceneSet : _tempSets[k - 1], ref push, _downPipeline);
        }

        Span<float> weights = stackalloc float[LevelCount];
        settings.GetGlowWeights(weights);
        var sets = stackalloc DescriptorSet[2];
        for (var k = max; k >= 0; k--)
        {
            var target = _levels[k];
            var extent = target.Extent;
            var top = k == max;
            var push = new UpPush { DstWidth = extent.Width, DstHeight = extent.Height, Flags = top ? 1u : 0u, Weight = weights[k], Radius = UpsampleRadius };
            sets[0] = top ? _tempSets[k] : _levelSets[k + 1]; // the top level reads no lower level (any valid set)
            sets[1] = _tempSets[k];
            target.Begin(cb, default);
            vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _upPipeline);
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _upLayout, 0, 2, sets, 0, null);
            vk.CmdPushConstants(cb, _upLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(UpPush), &push);
            PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
            vk.CmdDraw(cb, 3, 1, 0, 0);
            target.End(cb);
        }
    }

    private void EnsureHigh()
    {
        if (_upPipeline.Handle != 0)
            return;
        _downPipeline = PipelineBuilder.Create(_ctx, new PipelineState(), _layout, _temp[0].RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/GlowDownsample.vk.frag.spv", [], [], "glow downsample");
        _upLayout = PipelineBuilder.CreateLayout(_ctx, [_setLayout, _setLayout], (uint)sizeof(UpPush), ShaderStageFlags.FragmentBit, "glow upsample");
        _upPipeline = PipelineBuilder.Create(_ctx, new PipelineState(), _upLayout, _levels[0].RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/GlowUpsample.vk.frag.spv", [], [], "glow upsample");
    }

    private void Pass(CommandBuffer cb, RenderTarget target, DescriptorSet source, ref GlowPush push) =>
        Pass(cb, target, source, ref push, _pipeline);

    private void Pass(CommandBuffer cb, RenderTarget target, DescriptorSet source, ref GlowPush push, Pipeline pipeline)
    {
        var vk = _ctx.Vk;
        var extent = target.Extent;
        push.DstWidth = extent.Width;
        push.DstHeight = extent.Height;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &source, 0, null);
        fixed (GlowPush* p = &push)
            vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(GlowPush), p);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
    }

    private void WriteSets(ImageView sceneView)
    {
        Write(_sceneSet, sceneView);
        for (var k = 0; k < LevelCount; k++)
        {
            Write(_tempSets[k], _temp[k].GetColor(0).View);
            Write(_levelSets[k], _levels[k].GetColor(0).View);
        }
    }

    private void Write(DescriptorSet set, ImageView view)
    {
        PipelineBuilder.WriteImage(_ctx, set, 0, new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = view,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });
        PipelineBuilder.WriteImage(_ctx, set, 1, _adaptedLuminance);
    }

    /// <summary>Destroys everything (the caller has waited for the device to be idle).</summary>
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        vk.DestroyPipeline(_ctx.Device, _pipeline, null);
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        if (_upPipeline.Handle != 0)
        {
            vk.DestroyPipeline(_ctx.Device, _downPipeline, null);
            vk.DestroyPipeline(_ctx.Device, _upPipeline, null);
            vk.DestroyPipelineLayout(_ctx.Device, _upLayout, null);
        }
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _setLayout, null);
        vk.DestroySampler(_ctx.Device, _sampler, null);
        for (var k = 0; k < LevelCount; k++)
        {
            _temp[k].Dispose();
            _levels[k].Dispose();
        }
    }

    // GlowUpsample.vk.frag's push block (std430).
    private struct UpPush
    {
        public float DstWidth;
        public float DstHeight;
        public uint Flags;
        public float Weight;
        public float Radius;
    }

    // GlowBlur.vk.frag's (and GlowDownsample.vk.frag's) push block (std430).
    private struct GlowPush
    {
        public float DstWidth;
        public float DstHeight;
        public uint Flags;
        public float Strength;
        public float Exposure;
        public float Threshold;
        public float Scale;
        public float Bloom;
        public float LuminanceCap;
        public float AutoExposureScale;
    }
}
