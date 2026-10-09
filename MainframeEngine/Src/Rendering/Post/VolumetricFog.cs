using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>
/// The volumetric fog of a view's world (ADR 0171; <see cref="WorldEnvironment"/>'s <c>VolumetricFog*</c> exports, Godot's
/// names), packed for <see cref="VolumetricFogEffect"/>: colours linear, values clamped. <see cref="Height"/> and
/// <see cref="HeightDensity"/> are the height fog's (<see cref="WorldEnvironment.FogHeight"/>,
/// <see cref="WorldEnvironment.FogHeightDensity"/>): the volumetric density has the same shape.
/// </summary>
internal readonly record struct VolumetricFogSettings(
    bool Enabled,
    float Density,
    Vector3 Albedo,
    Vector3 Emission,
    float Anisotropy,
    float Length,
    float DetailSpread,
    float AmbientInject,
    float SkyAffect,
    bool TemporalReprojection,
    float TemporalReprojectionAmount,
    float NoiseScale,
    float NoiseStrength,
    float Height,
    float HeightDensity)
{
    /// <summary>The march runs: enabled with some density over some length.</summary>
    public bool Active => Enabled && Density > 0f && Length > 0f;
}

/// <summary>
/// Volumetric fog lit by the sun through its shadow maps (ADR 0171, G8e.3): a <see cref="PostStage.BeforeTonemap"/>
/// effect, first in the stage (before TAA, which then smooths it with the scene), enabled by
/// <see cref="WorldEnvironment.VolumetricFogEnabled"/>. Three fullscreen raster passes, after the scene pass, from the
/// scene depth (no prepass needed):
/// <list type="number">
/// <item><c>Post/VolumetricFogMarch</c> at half resolution: from the camera to the surface (or
/// <see cref="VolumetricFogSettings.Length"/>), <see cref="Steps"/> steps spaced by the detail spread and offset per pixel
/// (and per frame, with temporal reprojection) by interleaved gradient noise. Per step the height fog's density × the
/// density × a wind-drifted 32³ noise; the sun's visibility from one comparison tap of its cascade (or the far shadow);
/// Henyey–Greenstein in-scattering, the sky's ambient light (× ambient inject) and emission, integrated as Hillaire
/// (2015) does. RGBA16F: in-scattered light (rgb), transmittance (a).</item>
/// <item><c>Post/VolumetricFogTemporal</c> at half resolution (with temporal reprojection): the previous result
/// reprojected through last frame's view-projection, clipped to the 3 × 3 neighbourhood's variance box, blended by
/// the reprojection amount; a ping-pong history in the pool.</item>
/// <item><c>Post/VolumetricFogComposite</c> at full resolution: a depth-aware upsample, <c>colour · T + in-scatter</c>
/// (the sky × <see cref="VolumetricFogSettings.SkyAffect"/>), written back with
/// <see cref="PostEffectContext.CopyToSceneColor"/>.</item>
/// </list>
/// The march binds the view's frame set (set 0: camera, lights, sky irradiance) and the shadow set (set 1), so it reads
/// exactly what the lit shaders read. Beyond the length the analytic fog takes over (<c>applyFog</c> starts there:
/// <see cref="WorldEnvironment.GetFrameEnvironment"/>). Allocates nothing per frame.
/// </summary>
internal sealed unsafe class VolumetricFogEffect() : PostEffect("volumetric fog", PostStage.BeforeTonemap, PostEffectOrder.VolumetricFog)
{
    /// <summary>March steps per ray (the proposal's High).</summary>
    public const int Steps = 24;

    /// <summary>The noise volume's size (texels per side; it tiles).</summary>
    public const int NoiseSize = 32;

    /// <summary>The variance box's half-width for the history clip, in standard deviations.</summary>
    public const float ClipGamma = 1.5f;

    /// <summary>The march's output: in-scattered radiance (rgb) and transmittance (a), half resolution.</summary>
    internal static readonly PostTargetDesc MarchDesc = new("volumetric fog march", Format.R16G16B16A16Sfloat, PostTargetScale.Half);

    /// <summary>The temporal history pair.</summary>
    internal static readonly PostTargetDesc HistoryDesc = new("volumetric fog", Format.R16G16B16A16Sfloat, PostTargetScale.Half);

    /// <summary>The fogged scene before it is copied back.</summary>
    internal static readonly PostTargetDesc CompositeDesc = new("volumetric fog composite", SceneTextures.ColorFormat);

    private static byte[]? s_noise;

    private IVulkanContext _ctx = null!;
    private RenderTarget _march = null!;
    private PostHistory<RenderTarget> _history = null!;
    private RenderTarget _first = null!; // the history pair's first target: temporal set i reads target i as last frame's
    private GpuTexture _noise = null!;
    private Sampler _pointSampler;
    private Sampler _linearSampler;
    private DescriptorSetLayout _marchSetLayout;    // 0 scene depth, 1 noise
    private DescriptorSetLayout _temporalSetLayout; // 0 march, 1 history, 2 scene depth
    private DescriptorSetLayout _compositeSetLayout; // 0 scene colour, 1 fog, 2 scene depth
    private DescriptorPool _pool;
    private DescriptorSet _marchSet;
    private readonly DescriptorSet[] _temporalSets = new DescriptorSet[2];
    private readonly DescriptorSet[] _compositeSets = new DescriptorSet[3]; // fog = history 0, history 1, the march
    private IShadowDescriptors? _shadows;
    private PipelineLayout _marchLayout;
    private PipelineLayout _temporalLayout;
    private PipelineLayout _compositeLayout;
    private Pipeline _marchPipeline;
    private Pipeline _temporalPipeline;
    private Pipeline _compositePipeline;
    private QueryPool _timestamps;
    private readonly bool[] _timestampsPending = new bool[IVulkanContext.MaxFramesInFlight];
    private float _timestampPeriodNs;

    public override bool IsEnabled(in PostEffectSettings settings) => settings.VolumetricFog.Active;

    /// <summary>The frame the fog was last composited (tests).</summary>
    public ulong DrawnFrame { get; private set; }

    /// <summary>Frames composited with a valid history since creation (tests).</summary>
    internal long AccumulatedFrames { get; private set; }

    /// <summary>Frames that started a new history (tests).</summary>
    internal long ResetFrames { get; private set; }

    /// <summary>GPU time of the last measured frame's three passes, in milliseconds (0 without timestamps).</summary>
    public double LastGpuMilliseconds { get; private set; }

    protected override void OnCreate(PostEffectContext context)
    {
        var ctx = context.Vulkan;
        _ctx = ctx;
        var march = _march = context.Targets.Get(MarchDesc);
        _history = context.Targets.GetHistory(HistoryDesc);
        _first = _history.Previous;
        var composite = context.Targets.Get(CompositeDesc);
        _noise = GpuTexture.Create3D(ctx, NoiseSize, NoiseSize, NoiseSize, Format.R8G8B8A8Unorm, Noise(), TextureSampling.LinearRepeat);
        _pointSampler = CreateSampler(ctx, Filter.Nearest);
        _linearSampler = CreateSampler(ctx, Filter.Linear);

        var binding = new DescriptorSetLayoutBinding { DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit };
        _marchSetLayout = PipelineBuilder.CreateSetLayout(ctx, [binding with { Binding = 0 }, binding with { Binding = 1 }], "volumetric fog march");
        _temporalSetLayout = PipelineBuilder.CreateSetLayout(ctx,
            [binding with { Binding = 0 }, binding with { Binding = 1 }, binding with { Binding = 2 }], "volumetric fog temporal");
        _compositeSetLayout = PipelineBuilder.CreateSetLayout(ctx,
            [binding with { Binding = 0 }, binding with { Binding = 1 }, binding with { Binding = 2 }], "volumetric fog composite");
        _pool = PipelineBuilder.CreatePool(ctx, 6, [new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 17 }], "volumetric fog");
        _marchSet = PipelineBuilder.AllocateSet(ctx, _pool, _marchSetLayout, "volumetric fog march");
        for (var i = 0; i < _temporalSets.Length; i++)
            _temporalSets[i] = PipelineBuilder.AllocateSet(ctx, _pool, _temporalSetLayout, "volumetric fog temporal");
        for (var i = 0; i < _compositeSets.Length; i++)
            _compositeSets[i] = PipelineBuilder.AllocateSet(ctx, _pool, _compositeSetLayout, "volumetric fog composite");
        WriteSets(context.Scene);

        // The temporal pass binds the frame set (set 0: this and last frame's matrices); the march also the shadow set.
        _temporalLayout = ctx.Frame.CreatePipelineLayout(null, [_temporalSetLayout], "volumetric fog temporal");
        _temporalPipeline = PipelineBuilder.Create(ctx, new PipelineState(), _temporalLayout, _history.Current.RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/VolumetricFogTemporal.vk.frag.spv", [], [], "volumetric fog temporal");
        _compositeLayout = PipelineBuilder.CreateLayout(ctx, [_compositeSetLayout], (uint)sizeof(CompositePush), ShaderStageFlags.FragmentBit, "volumetric fog composite");
        _compositePipeline = PipelineBuilder.Create(ctx, new PipelineState(), _compositeLayout, composite.RenderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/VolumetricFogComposite.vk.frag.spv", [], [], "volumetric fog composite");
        EnsureMarchPipeline(ShadowsFor(context), march.RenderPass);
        CreateTimestamps();
    }

    protected override void OnBeginFrame(PostEffectContext context)
    {
        if (context.Settings.VolumetricFog.TemporalReprojection)
            _history.Advance(context.FrameNumber);
    }

    /// <summary>After a resize (device idle): the scene's images and the pooled targets are new.</summary>
    protected override void OnResize(PostEffectContext context) => WriteSets(context.Scene);

    protected override void OnRecord(PostEffectContext context)
    {
        if (!context.HasCamera)
            return;
        var settings = context.Settings.VolumetricFog;
        var ctx = _ctx;
        var vk = ctx.Vk;
        var cb = context.CommandBuffer;
        var march = context.Targets.Get(MarchDesc);
        EnsureMarchPipeline(ShadowsFor(context), march.RenderPass);
        var slot = ctx.FrameSlot;
        BeginGpuTiming(cb, slot);

        // 1. The march (half resolution).
        var temporal = settings.TemporalReprojection;
        var marchPush = MarchParams(settings, march.Extent, context.Scene.Extent, temporal ? context.FrameNumber : (ulong?)null);
        march.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _marchPipeline);
        var sets = stackalloc DescriptorSet[3];
        sets[0] = ctx.Frame.SetFor(context.View);
        sets[1] = _shadows!.GetMainSet();
        sets[2] = _marchSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _marchLayout, 0, 3, sets, 0, null);
        vk.CmdPushConstants(cb, _marchLayout, FrameContext.PushConstantStages, 0, (uint)sizeof(MarchPush), &marchPush);
        PipelineBuilder.SetViewport(vk, cb, march.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        march.End(cb);

        // 2. Temporal reprojection (half resolution) into the history's current target.
        var fogSet = _compositeSets[2];
        if (temporal)
        {
            var valid = _history.IsValid && context.Camera.HistoryValid;
            if (valid)
            {
                AccumulatedFrames++;
            }
            else
            {
                ResetFrames++;
                // Last frame's target may never have been written (undefined layout): clear it so the set can bind it.
                _history.Previous.Begin(cb, default);
                _history.Previous.End(cb);
            }

            var target = _history.Current;
            var previousIndex = ReferenceEquals(_history.Previous, _first) ? 0 : 1;
            target.Begin(cb, default);
            vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _temporalPipeline);
            sets[0] = ctx.Frame.SetFor(context.View);
            sets[1] = _temporalSets[previousIndex];
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _temporalLayout, 0, 2, sets, 0, null);
            var temporalPush = new TemporalPush
            {
                Params = new Vector4(valid ? settings.TemporalReprojectionAmount : 0f, ClipGamma, target.Extent.Width, target.Extent.Height),
                Scene = new Vector4(context.Scene.Extent.Width, context.Scene.Extent.Height, settings.Length, 0f),
            };
            vk.CmdPushConstants(cb, _temporalLayout, FrameContext.PushConstantStages, 0, (uint)sizeof(TemporalPush), &temporalPush);
            PipelineBuilder.SetViewport(vk, cb, target.Extent, flipY: false);
            vk.CmdDraw(cb, 3, 1, 0, 0);
            target.End(cb);
            _history.MarkWritten();
            fogSet = _compositeSets[ReferenceEquals(target, _first) ? 0 : 1];
        }

        // 3. The composite (full resolution), then back into the scene colour.
        var composite = context.Targets.Get(CompositeDesc);
        var projection = context.Camera.JitteredProjection;
        var compositePush = new CompositePush
        {
            Depth = new Vector4(projection.M33, projection.M43, projection.M34, projection.M44),
            Params = new Vector4(settings.SkyAffect, march.Extent.Width, march.Extent.Height, 0f),
        };
        composite.Begin(cb, default);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _compositePipeline);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _compositeLayout, 0, 1, &fogSet, 0, null);
        vk.CmdPushConstants(cb, _compositeLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(CompositePush), &compositePush);
        PipelineBuilder.SetViewport(vk, cb, composite.Extent, flipY: false);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        composite.End(cb);
        EndGpuTiming(cb, slot);
        context.CopyToSceneColor(composite.GetColor(0).View);
        DrawnFrame = context.FrameNumber;
    }

    /// <summary>
    /// The march's push block: scattering and extinction, emission and anisotropy, the ray (length, spread, steps, the
    /// noise frame: −1 = a fixed per-pixel offset), the height profile and ambient, the density noise and the target sizes.
    /// </summary>
    internal static MarchPush MarchParams(in VolumetricFogSettings settings, Extent2D march, Extent2D scene, ulong? frame) => new()
    {
        Scattering = new Vector4(settings.Albedo * settings.Density, settings.Density),
        Emission = new Vector4(settings.Emission * settings.Density, settings.Anisotropy),
        Ray = new Vector4(settings.Length, settings.DetailSpread, Steps, frame is { } f ? f % 64 : -1f),
        Height = new Vector4(settings.Height, settings.HeightDensity, settings.AmbientInject, 0f),
        Noise = new Vector4(1f / settings.NoiseScale, settings.NoiseStrength, 0f, 0f),
        Sizes = new Vector4(march.Width, march.Height, scene.Width, scene.Height),
    };

    /// <summary>The Henyey–Greenstein phase function (per steradian; integrates to 1 over the sphere) for <paramref name="cosTheta"/>
    /// between the view ray and the direction towards the light (what the march evaluates).</summary>
    public static float HenyeyGreenstein(float g, float cosTheta)
    {
        var g2 = g * g;
        var denominator = 1f + g2 - 2f * g * cosTheta;
        return (1f - g2) / (4f * MathF.PI * denominator * MathF.Sqrt(denominator));
    }

    /// <summary>Where step <paramref name="i"/> of <paramref name="steps"/> ends along a ray of <paramref name="length"/>
    /// metres with detail spread <paramref name="spread"/>: <c>length · ((i + 1) / steps)^spread</c>.</summary>
    public static float StepEnd(int i, int steps, float length, float spread) => length * MathF.Pow((i + 1f) / steps, spread);

    /// <summary>
    /// The 32³ density noise (RGBA8, the value in every channel): three octaves of tileable value noise with smoothstep
    /// interpolation (periods 4, 8 and 16 cells), stretched to [0, 1]. Deterministic; computed once per process.
    /// </summary>
    internal static byte[] Noise()
    {
        if (s_noise is { } cached)
            return cached;
        const int n = NoiseSize;
        var values = new float[n * n * n];
        float min = float.MaxValue, max = float.MinValue;
        for (var z = 0; z < n; z++)
            for (var y = 0; y < n; y++)
                for (var x = 0; x < n; x++)
                {
                    var v = 0.5f * ValueNoise(x, y, z, 4) + 0.3f * ValueNoise(x, y, z, 8) + 0.2f * ValueNoise(x, y, z, 16);
                    values[(z * n + y) * n + x] = v;
                    min = MathF.Min(min, v);
                    max = MathF.Max(max, v);
                }

        var bytes = new byte[n * n * n * 4];
        var scale = max > min ? 1f / (max - min) : 0f;
        for (var i = 0; i < values.Length; i++)
        {
            var b = (byte)Math.Clamp((int)MathF.Round((values[i] - min) * scale * 255f), 0, 255);
            bytes[i * 4] = bytes[i * 4 + 1] = bytes[i * 4 + 2] = bytes[i * 4 + 3] = b;
        }

        return s_noise = bytes;
    }

    // Value noise with `cells` lattice cells across the volume (tiling: the lattice wraps), smoothstep-interpolated.
    private static float ValueNoise(int x, int y, int z, int cells)
    {
        var size = (float)NoiseSize / cells;
        float fx = x / size, fy = y / size, fz = z / size;
        int x0 = (int)fx, y0 = (int)fy, z0 = (int)fz;
        float tx = Smooth(fx - x0), ty = Smooth(fy - y0), tz = Smooth(fz - z0);
        var result = 0f;
        for (var c = 0; c < 8; c++)
        {
            int dx = c & 1, dy = (c >> 1) & 1, dz = c >> 2;
            var w = (dx == 1 ? tx : 1f - tx) * (dy == 1 ? ty : 1f - ty) * (dz == 1 ? tz : 1f - tz);
            result += w * Lattice((x0 + dx) % cells, (y0 + dy) % cells, (z0 + dz) % cells, cells);
        }

        return result;

        static float Smooth(float t) => t * t * (3f - 2f * t);
    }

    private static float Lattice(int x, int y, int z, int cells)
    {
        var h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(z * 83492791) ^ (uint)(cells * 2654435761);
        h ^= h >> 13;
        h *= 0x5bd1e995;
        h ^= h >> 15;
        return (h & 0xFFFF) / 65535f;
    }

    // The shadow set the march binds: the render server's shadow system, or the renderer's "no shadows" fallback.
    private static IShadowDescriptors ShadowsFor(PostEffectContext context) =>
        context.Shadows ?? ShadowFallback.Resolve(null, context.Vulkan);

    // The march's layout includes the shadow set's (its immutable samplers differ between the shadow system and the
    // fallback): rebuilt when the shadow descriptors change (the old objects go to the deletion queue).
    private void EnsureMarchPipeline(IShadowDescriptors shadows, RenderPass renderPass)
    {
        if (ReferenceEquals(shadows, _shadows) && _marchPipeline.Handle != 0)
            return;
        if (_marchPipeline.Handle != 0)
        {
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_marchPipeline));
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_marchLayout));
        }

        _shadows = shadows;
        _marchLayout = _ctx.Frame.CreatePipelineLayout(shadows, [_marchSetLayout], "volumetric fog march");
        _marchPipeline = PipelineBuilder.Create(_ctx, new PipelineState(), _marchLayout, renderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Post/VolumetricFogMarch.vk.frag.spv", [], [], "volumetric fog march");
    }

    private void WriteSets(SceneTextures scene)
    {
        var depth = Image(_pointSampler, scene.Depth, ImageLayout.DepthStencilReadOnlyOptimal);
        PipelineBuilder.WriteImage(_ctx, _marchSet, 0, depth);
        PipelineBuilder.WriteImage(_ctx, _marchSet, 1, Image(_noise.Sampler, _noise.View, ImageLayout.ShaderReadOnlyOptimal));

        // The pool hands out the same target objects after a resize (their views are new).
        var march = Image(_pointSampler, _march.GetColor(0).View, ImageLayout.ShaderReadOnlyOptimal);
        var second = ReferenceEquals(_history.Current, _first) ? _history.Previous : _history.Current;
        for (var i = 0; i < 2; i++)
        {
            var previous = i == 0 ? _first : second;
            PipelineBuilder.WriteImage(_ctx, _temporalSets[i], 0, march);
            PipelineBuilder.WriteImage(_ctx, _temporalSets[i], 1, Image(_linearSampler, previous.GetColor(0).View, ImageLayout.ShaderReadOnlyOptimal));
            PipelineBuilder.WriteImage(_ctx, _temporalSets[i], 2, depth);
        }

        var color = Image(_pointSampler, scene.Color, ImageLayout.ShaderReadOnlyOptimal);
        for (var i = 0; i < _compositeSets.Length; i++)
        {
            var fog = i < 2 ? Image(_pointSampler, (i == 0 ? _first : second).GetColor(0).View, ImageLayout.ShaderReadOnlyOptimal) : march;
            PipelineBuilder.WriteImage(_ctx, _compositeSets[i], 0, color);
            PipelineBuilder.WriteImage(_ctx, _compositeSets[i], 1, fog);
            PipelineBuilder.WriteImage(_ctx, _compositeSets[i], 2, depth);
        }
    }

    private static DescriptorImageInfo Image(Sampler sampler, ImageView view, ImageLayout layout) =>
        new() { Sampler = sampler, ImageView = view, ImageLayout = layout };

    private static Sampler CreateSampler(IVulkanContext ctx, Filter filter)
    {
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        ctx.Vk.CreateSampler(ctx.Device, in info, null, out var sampler).Check("vkCreateSampler (volumetric fog)");
        return sampler;
    }

    // Two timestamps per frame slot around the passes (SsaoEffect's pattern), when the graphics queue has them.
    private void CreateTimestamps()
    {
        _ctx.Vk.GetPhysicalDeviceProperties(_ctx.PhysicalDevice, out var props);
        if (!props.Limits.TimestampComputeAndGraphics || props.Limits.TimestampPeriod <= 0f)
            return;
        var info = new QueryPoolCreateInfo
        {
            SType = StructureType.QueryPoolCreateInfo,
            QueryType = QueryType.Timestamp,
            QueryCount = 2 * IVulkanContext.MaxFramesInFlight,
        };
        _ctx.Vk.CreateQueryPool(_ctx.Device, in info, null, out _timestamps).Check("vkCreateQueryPool (volumetric fog timing)");
        _timestampPeriodNs = props.Limits.TimestampPeriod;
    }

    private void BeginGpuTiming(CommandBuffer cb, int slot)
    {
        if (_timestamps.Handle == 0)
            return;
        if (_timestampsPending[slot])
        {
            var data = stackalloc ulong[2];
            if (_ctx.Vk.GetQueryPoolResults(_ctx.Device, _timestamps, (uint)slot * 2, 2, 16, data, 8, QueryResultFlags.Result64Bit) == Result.Success)
                LastGpuMilliseconds = (data[1] - data[0]) * _timestampPeriodNs / 1e6;
            _timestampsPending[slot] = false;
        }

        _ctx.Vk.CmdResetQueryPool(cb, _timestamps, (uint)slot * 2, 2);
        _ctx.Vk.CmdWriteTimestamp(cb, PipelineStageFlags.TopOfPipeBit, _timestamps, (uint)slot * 2);
    }

    private void EndGpuTiming(CommandBuffer cb, int slot)
    {
        if (_timestamps.Handle == 0)
            return;
        _ctx.Vk.CmdWriteTimestamp(cb, PipelineStageFlags.BottomOfPipeBit, _timestamps, (uint)slot * 2 + 1);
        _timestampsPending[slot] = true;
    }

    /// <summary>Destroys everything (the device is idle); the pool owns the targets.</summary>
    protected override void OnDispose()
    {
        var vk = _ctx.Vk;
        var device = _ctx.Device;
        vk.DestroyPipeline(device, _marchPipeline, null);
        vk.DestroyPipeline(device, _temporalPipeline, null);
        vk.DestroyPipeline(device, _compositePipeline, null);
        vk.DestroyPipelineLayout(device, _marchLayout, null);
        vk.DestroyPipelineLayout(device, _temporalLayout, null);
        vk.DestroyPipelineLayout(device, _compositeLayout, null);
        vk.DestroyDescriptorPool(device, _pool, null);
        vk.DestroyDescriptorSetLayout(device, _marchSetLayout, null);
        vk.DestroyDescriptorSetLayout(device, _temporalSetLayout, null);
        vk.DestroyDescriptorSetLayout(device, _compositeSetLayout, null);
        vk.DestroySampler(device, _pointSampler, null);
        vk.DestroySampler(device, _linearSampler, null);
        if (_timestamps.Handle != 0)
            vk.DestroyQueryPool(device, _timestamps, null);
        _noise.Dispose();
    }

    /// <summary>VolumetricFogMarch.vk.frag's push block (96 bytes; within the frame layout's 128).</summary>
    internal struct MarchPush
    {
        /// <summary>rgb = albedo × density (scattering per metre), a = density (extinction per metre).</summary>
        public Vector4 Scattering;

        /// <summary>rgb = emission × density, a = Henyey–Greenstein g.</summary>
        public Vector4 Emission;

        /// <summary>x = length (m), y = detail spread, z = steps, w = noise frame (−1: fixed per pixel).</summary>
        public Vector4 Ray;

        /// <summary>x = fog height (m), y = height density (1/m), z = ambient inject, w = 0.</summary>
        public Vector4 Height;

        /// <summary>x = 1 / noise scale (1/m), y = noise strength.</summary>
        public Vector4 Noise;

        /// <summary>xy = the march's size, zw = the scene's (pixels).</summary>
        public Vector4 Sizes;
    }

    /// <summary>VolumetricFogTemporal.vk.frag's push block.</summary>
    internal struct TemporalPush
    {
        /// <summary>x = the history's weight (0: none), y = clip gamma, zw = the target's size.</summary>
        public Vector4 Params;

        /// <summary>xy = the scene's size, z = length (m).</summary>
        public Vector4 Scene;
    }

    /// <summary>VolumetricFogComposite.vk.frag's push block.</summary>
    internal struct CompositePush
    {
        /// <summary>M33, M43, M34, M44 of the jittered projection (view depth from a depth value).</summary>
        public Vector4 Depth;

        /// <summary>x = sky affect, yz = the fog image's size.</summary>
        public Vector4 Params;
    }
}
