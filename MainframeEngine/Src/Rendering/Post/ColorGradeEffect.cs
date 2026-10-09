using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The colour grade and film effects (ADR 0168) as one <see cref="PostStage.AfterTonemap"/> pass after FXAA
/// (<see cref="PostEffectOrder.ColorGrade"/>; <c>Post/ColorGrade.vk.frag</c>): chromatic aberration, Godot's
/// <c>adjustment_*</c> brightness/contrast/saturation, the <see cref="PostProcessSettings.AdjustmentColorCorrection"/>
/// 3D LUT with its strength, the vignette and temporally varying film grain (seeded by the frame number, so
/// <c>--fixed-fps</c> runs are deterministic). Enabled by <see cref="PostProcessSettings.ColorGradeEnabled"/>. Reads the
/// display-encoded LDR image, as Godot's adjustments do (they run after the tonemap's sRGB encode).
/// </summary>
/// <remarks>
/// The LUT is uploaded the first frame it is used (<see cref="Texture3DGpu"/>) and bound at set 1. Its descriptor sets
/// form a ring of <c>MaxFramesInFlight + 1</c>: a new LUT (another texture, or the same one changed) is written into the
/// slot no frame in flight can still bind, so no set in use is ever rewritten; the previous texture goes through the
/// deletion queue. Without a LUT the set binds a 2³ identity volume. Allocates nothing per frame.
/// </remarks>
internal sealed unsafe class ColorGradeEffect() : PostEffect("color grade", PostStage.AfterTonemap, PostEffectOrder.ColorGrade)
{
    private const int LutSlots = IVulkanContext.MaxFramesInFlight + 1;

    private IVulkanContext _ctx = null!;
    private Sampler _sampler;
    private DescriptorSetLayout _inputLayout;
    private DescriptorSetLayout _lutLayout;
    private DescriptorPool _pool;
    private PipelineLayout _layout;
    private readonly LdrInputSets _inputs = new();
    private readonly PassPipelines _pipelines = new();
    private GpuTexture _identity = null!;

    // The LUT ring: what each slot binds (a Texture3DGpu generation) and the last frame that bound it.
    private readonly DescriptorSet[] _lutSets = new DescriptorSet[LutSlots];
    private readonly ulong[] _lutUsedFrame = new ulong[LutSlots];
    private Texture3DGpu? _lut;
    private int _lutGeneration = -1; // the Texture3DGpu generation _lutSerial counts
    private int _lutSerial;          // bumped on every new LUT upload
    private int _lutKey;             // what the current slot binds: 0 the identity, else a _lutSerial
    private int _lutSlot = -1;

    public override bool IsEnabled(in PostEffectSettings settings) => settings.World.ColorGradeEnabled;

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
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
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sampler).Check("vkCreateSampler (color grade)");

        var binding = new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit };
        _inputLayout = PipelineBuilder.CreateSetLayout(ctx, [binding], "color grade input");
        _lutLayout = PipelineBuilder.CreateSetLayout(ctx, [binding], "color grade lut");
        const uint sets = LdrInputSets.Capacity + LutSlots;
        _pool = PipelineBuilder.CreatePool(ctx, sets, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = sets }], "color grade");
        _inputs.Allocate(ctx, _pool, _inputLayout, "color grade input");
        for (var i = 0; i < LutSlots; i++)
            _lutSets[i] = PipelineBuilder.AllocateSet(ctx, _pool, _lutLayout, "color grade lut");
        _layout = PipelineBuilder.CreateLayout(ctx, [_inputLayout, _lutLayout], (uint)sizeof(GradePush), ShaderStageFlags.FragmentBit, "color grade");

        // A 2³ identity volume for frames without a LUT (the set must bind something).
        Span<byte> identity = stackalloc byte[2 * 2 * 2 * 4];
        for (var z = 0; z < 2; z++)
            for (var y = 0; y < 2; y++)
                for (var x = 0; x < 2; x++)
                {
                    var i = ((z * 2 + y) * 2 + x) * 4;
                    identity[i] = (byte)(x * 255);
                    identity[i + 1] = (byte)(y * 255);
                    identity[i + 2] = (byte)(z * 255);
                    identity[i + 3] = 255;
                }

        _identity = GpuTexture.Create3D(ctx, 2, 2, 2, Format.R8G8B8A8Unorm, identity, TextureSampling.LinearClamp);
    }

    /// <summary>After a resize (device idle): the LDR images are new, so every input set is rewritten on use.</summary>
    protected override void OnResize(PostEffectContext context) => _inputs.Forget();

    protected override void OnRecord(PostEffectContext context)
    {
        var vk = _ctx.Vk;
        var cb = context.CommandBuffer;
        var extent = context.Scene.Extent;
        var settings = context.Settings.World;
        var pass = context.OutputRenderPass;
        if (!_pipelines.TryGet(pass, out var pipeline))
            pipeline = _pipelines.Add(pass, PipelineBuilder.Create(_ctx, new PipelineState(), _layout, pass,
                "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/ColorGrade.vk.frag.spv", [], [], "color grade"));

        var useLut = settings.AdjustmentEnabled && settings.AdjustmentColorCorrection is { IsEmpty: false } && settings.AdjustmentColorCorrectionStrength > 0f;
        var lutSize = useLut ? settings.AdjustmentColorCorrection!.Width : 2;
        var sets = stackalloc DescriptorSet[2];
        sets[0] = _inputs.Get(_ctx, context.Scene.Ldr, _sampler);
        sets[1] = LutSet(context.FrameNumber, useLut ? settings.AdjustmentColorCorrection : null, ref useLut);

        var flags = 0u;
        if (settings.AdjustmentEnabled)
            flags |= 1u;
        if (useLut)
            flags |= 2u;
        if (context.OutputEncodesSrgb)
            flags |= 4u;
        var push = new GradePush
        {
            RcpWidth = 1f / extent.Width,
            RcpHeight = 1f / extent.Height,
            Aspect = (float)extent.Width / extent.Height,
            Flags = flags,
            Brightness = settings.AdjustmentBrightness,
            Contrast = settings.AdjustmentContrast,
            Saturation = settings.AdjustmentSaturation,
            LutStrength = Math.Clamp(settings.AdjustmentColorCorrectionStrength, 0f, 1f),
            LutScale = (lutSize - 1f) / lutSize,
            LutOffset = 0.5f / lutSize,
            Vignette = Math.Max(settings.VignetteIntensity, 0f),
            Roundness = settings.VignetteRoundness,
            Grain = Math.Max(settings.FilmGrainIntensity, 0f),
            GrainSize = settings.FilmGrainSize,
            GrainSeed = (uint)context.FrameNumber,
            Aberration = Math.Max(settings.ChromaticAberrationIntensity, 0f),
        };

        context.BeginOutput();
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 2, sets, 0, null);
        vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(GradePush), &push);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        context.EndOutput();
    }

    // The set binding the LUT for this frame, written into a ring slot no frame in flight can bind when the LUT changed.
    // Each frame binds one slot, so of MaxFramesInFlight + 1 slots one is always free. useLut turns false when the LUT is
    // empty.
    private DescriptorSet LutSet(ulong frame, Texture3D? texture, ref bool useLut)
    {
        if (texture is not null)
        {
            if (_lut is null || !ReferenceEquals(_lut.Texture, texture))
            {
                _lut?.Dispose(); // deletion-queued; a slot that binds it is never bound again before it is rewritten
                _lut = new Texture3DGpu(_ctx, texture);
                _lutGeneration = -1;
            }

            if (!_lut.Update())
                useLut = false;
            else if (_lut.Generation != _lutGeneration)
            {
                _lutGeneration = _lut.Generation;
                _lutSerial++;
            }
        }

        var wanted = useLut ? _lutSerial : 0; // 0: the identity volume
        if (_lutSlot >= 0 && _lutKey == wanted)
        {
            _lutUsedFrame[_lutSlot] = frame;
            return _lutSets[_lutSlot];
        }

        for (var i = 1; i <= LutSlots; i++)
        {
            var slot = (_lutSlot + i + LutSlots) % LutSlots;
            if (_lutUsedFrame[slot] != 0 && frame - _lutUsedFrame[slot] < (ulong)IVulkanContext.MaxFramesInFlight)
                continue;
            PipelineBuilder.WriteImage(_ctx, _lutSets[slot], 0, useLut ? _lut!.Gpu!.Descriptor : _identity.Descriptor);
            _lutSlot = slot;
            _lutKey = wanted;
            _lutUsedFrame[slot] = frame;
            return _lutSets[slot];
        }

        throw new InvalidOperationException("Every colour-grade LUT set is in flight."); // unreachable: one slot per frame
    }

    /// <summary>Destroys everything (the caller has waited for the device to be idle).</summary>
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        _pipelines.Destroy(_ctx);
        _lut?.Dispose();
        _identity.Dispose();
        vk.DestroyPipelineLayout(_ctx.Device, _layout, null);
        vk.DestroyDescriptorPool(_ctx.Device, _pool, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _inputLayout, null);
        vk.DestroyDescriptorSetLayout(_ctx.Device, _lutLayout, null);
        vk.DestroySampler(_ctx.Device, _sampler, null);
    }

    // ColorGrade.vk.frag's push block (std430).
    private struct GradePush
    {
        public float RcpWidth;
        public float RcpHeight;
        public float Aspect;
        public uint Flags;
        public float Brightness;
        public float Contrast;
        public float Saturation;
        public float LutStrength;
        public float LutScale;
        public float LutOffset;
        public float Vignette;
        public float Roundness;
        public float Grain;
        public float GrainSize;
        public uint GrainSeed;
        public float Aberration;
    }
}
