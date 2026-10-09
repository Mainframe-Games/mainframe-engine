using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The physical sky's look-up tables (ADR 0154, Hillaire 2020), each a raster pass with a fullscreen triangle (no
/// compute): transmittance (256 × 64) and multiple scattering (32 × 32) when the atmosphere changes, then the sky view
/// (192 × 108) when the atmosphere, the camera's altitude or the sun's elevation changes. Every LUT is
/// <c>R16G16B16A16_SFLOAT</c>, one render pass each, ended through <see cref="RenderTarget.End"/> and its barrier.
/// Recorded outside a render pass, before anything samples the sky view; allocates nothing per frame.
/// </summary>
internal sealed unsafe class PhysicalSkyLuts : IDisposable
{
    public static readonly Extent2D TransmittanceSize = new(256, 64);
    public static readonly Extent2D MultiScatteringSize = new(32, 32);
    public static readonly Extent2D SkyViewSize = new(192, 108);
    private const Format LutFormat = Format.R16G16B16A16Sfloat;

    private readonly IVulkanContext _ctx;
    private readonly RenderTarget _transmittance;
    private readonly RenderTarget _multiScattering;
    private readonly RenderTarget _skyView;
    private readonly Sampler _sampler;
    private readonly DescriptorSetLayout _setLayout;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet _multiScatteringSet; // binding 0 = transmittance (binding 1 unused, also transmittance)
    private readonly DescriptorSet _skyViewSet;         // binding 0 = transmittance, binding 1 = multiple scattering
    private readonly PipelineLayout _layout;
    private readonly Pipeline _transmittancePipeline;
    private readonly Pipeline _multiScatteringPipeline;
    private readonly Pipeline _skyViewPipeline;
    private AtmosphereParameters _builtAtmosphere;
    private (AtmosphereParameters Atmosphere, float ViewRadius, float CosSunZenith) _builtSkyView;
    private bool _hasAtmosphere;
    private bool _disposed;

    public PhysicalSkyLuts(IVulkanContext ctx)
    {
        _ctx = ctx;
        _transmittance = new RenderTarget(ctx, new RenderTargetDesc("sky transmittance LUT", [RenderTargetAttachment.Sampled(LutFormat)], null), TransmittanceSize);
        _multiScattering = new RenderTarget(ctx, new RenderTargetDesc("sky multiple-scattering LUT", [RenderTargetAttachment.Sampled(LutFormat)], null), MultiScatteringSize);
        _skyView = new RenderTarget(ctx, new RenderTargetDesc("sky-view LUT", [RenderTargetAttachment.Sampled(LutFormat)], null), SkyViewSize);

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
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sampler).Check("vkCreateSampler (sky LUTs)");

        _setLayout = PipelineBuilder.CreateSetLayout(ctx,
        [
            new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ], "sky LUTs");
        _pool = PipelineBuilder.CreatePool(ctx, 2, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 4 }], "sky LUTs");
        _multiScatteringSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "sky multiple-scattering LUT");
        _skyViewSet = PipelineBuilder.AllocateSet(ctx, _pool, _setLayout, "sky-view LUT");
        Write(_multiScatteringSet, 0, _transmittance);
        Write(_multiScatteringSet, 1, _transmittance);
        Write(_skyViewSet, 0, _transmittance);
        Write(_skyViewSet, 1, _multiScattering);

        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], (uint)sizeof(AtmospherePush), ShaderStageFlags.FragmentBit, "sky LUTs");
        const string vertex = "Shaders/Post/Fullscreen.vk.vert.spv";
        _transmittancePipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _transmittance.RenderPass,
            vertex, "Shaders/Sky/AtmosphereTransmittance.vk.frag.spv", [], [], "sky transmittance LUT");
        _multiScatteringPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _multiScattering.RenderPass,
            vertex, "Shaders/Sky/AtmosphereMultiScattering.vk.frag.spv", [], [], "sky multiple-scattering LUT");
        _skyViewPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _layout, _skyView.RenderPass,
            vertex, "Shaders/Sky/AtmosphereSkyView.vk.frag.spv", [], [], "sky-view LUT");
    }

    /// <summary>The sky-view LUT for the sky pass's set (valid once <see cref="Ready"/>), sampled linearly with clamping.</summary>
    public DescriptorImageInfo SkyViewDescriptor => new()
    {
        Sampler = _sampler,
        ImageView = _skyView.GetColor(0).View,
        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
    };

    /// <summary>True once the sky view has been rendered (before that its image holds no defined data).</summary>
    public bool Ready { get; private set; }

    /// <summary>Times the transmittance and multiple-scattering LUTs were rendered (tests).</summary>
    public int AtmosphereBuilds { get; private set; }

    /// <summary>Times the sky-view LUT was rendered (tests).</summary>
    public int SkyViewBuilds { get; private set; }

    /// <summary>
    /// Re-renders what <paramref name="atmosphere"/>, the camera's <paramref name="viewRadius"/> (km from the planet's
    /// centre) and the sun's elevation invalidate. Call with no render pass active.
    /// </summary>
    public void Update(CommandBuffer cb, in AtmosphereParameters atmosphere, float viewRadius, float cosSunZenith)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var push = AtmospherePush.From(atmosphere, viewRadius, cosSunZenith);
        if (!_hasAtmosphere || _builtAtmosphere != atmosphere)
        {
            Pass(cb, _transmittance, _transmittancePipeline, default, ref push);
            Pass(cb, _multiScattering, _multiScatteringPipeline, _multiScatteringSet, ref push);
            _builtAtmosphere = atmosphere;
            _hasAtmosphere = true;
            AtmosphereBuilds++;
        }

        var key = (atmosphere, viewRadius, cosSunZenith);
        if (Ready && _builtSkyView == key)
            return;
        Pass(cb, _skyView, _skyViewPipeline, _skyViewSet, ref push);
        _builtSkyView = key;
        Ready = true;
        SkyViewBuilds++;
    }

    private void Pass(CommandBuffer cb, RenderTarget target, Pipeline pipeline, DescriptorSet set, ref AtmospherePush push)
    {
        var vk = _ctx.Vk;
        target.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        if (set.Handle != 0)
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, 0, 1, &set, 0, null);
        fixed (AtmospherePush* p = &push)
            vk.CmdPushConstants(cb, _layout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(AtmospherePush), p);
        PipelineBuilder.SetViewport(vk, cb, target.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        target.End(cb);
    }

    private void Write(DescriptorSet set, uint binding, RenderTarget source) =>
        PipelineBuilder.WriteImage(_ctx, set, binding, new DescriptorImageInfo
        {
            Sampler = _sampler,
            ImageView = source.GetColor(0).View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });

    /// <summary>Releases everything through the deletion queue (safe while frames are in flight).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_transmittancePipeline));
        deletions.Enqueue(GpuDeletion.Of(_multiScatteringPipeline));
        deletions.Enqueue(GpuDeletion.Of(_skyViewPipeline));
        deletions.Enqueue(GpuDeletion.Of(_layout));
        deletions.Enqueue(GpuDeletion.Of(_pool));
        deletions.Enqueue(GpuDeletion.Of(_setLayout));
        deletions.Enqueue(GpuDeletion.Of(_sampler));
        _transmittance.Dispose();
        _multiScattering.Dispose();
        _skyView.Dispose();
    }

    /// <summary><c>Atmosphere</c> in <c>include/atmosphere.slang</c> (96 bytes).</summary>
    internal struct AtmospherePush
    {
        public Vector4 Rayleigh;      // rgb scattering (1/km), w scale height (km)
        public Vector4 MieScattering; // rgb, w scale height
        public Vector4 MieExtinction; // rgb, w g
        public Vector4 Ozone;         // rgb absorption
        public Vector4 Ground;        // rgb albedo, w planet radius
        public Vector4 View;          // x top radius, y view radius, z cos sun zenith

        public static AtmospherePush From(in AtmosphereParameters a, float viewRadius, float cosSunZenith) => new()
        {
            Rayleigh = new Vector4(a.RayleighScattering, a.RayleighScaleHeight),
            MieScattering = new Vector4(a.MieScattering, a.MieScaleHeight),
            MieExtinction = new Vector4(a.MieExtinction, a.MieG),
            Ozone = new Vector4(a.OzoneAbsorption, 0f),
            Ground = new Vector4(a.GroundAlbedo, a.BottomRadius),
            View = new Vector4(a.TopRadius, viewRadius, cosSunZenith, 0f),
        };
    }
}
