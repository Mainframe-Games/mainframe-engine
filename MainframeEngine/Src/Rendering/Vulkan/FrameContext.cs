using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>std140 camera block at set 0, binding 0 (<c>include/frame.slang</c>, 576 bytes).</summary>
/// <remarks>
/// With a projection jitter (TAA, ADR 0163) <see cref="Projection"/>, <see cref="ViewProjection"/> and
/// <see cref="InverseProjection"/> are the jittered matrices the view rasterises with; <see cref="PreviousViewProjection"/>
/// is last frame's <b>unjittered</b> one, and <see cref="Jitter"/> holds both frames' offsets, so a shader recovers the
/// unjittered clip position as <c>clip.xy − jitter.xy · clip.w</c> (<c>unjitterClip</c> in <c>frame.slang</c>).
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public struct FrameData
{
    public Matrix4x4 View;
    public Matrix4x4 Projection;
    public Matrix4x4 ViewProjection;
    public Matrix4x4 InverseProjection;

    /// <summary>Inverse of the view matrix without its translation (camera-relative rays: the sky).</summary>
    public Matrix4x4 InverseViewRotation;

    /// <summary>xyz = camera world position, w = unused.</summary>
    public Vector4 CameraPosition;

    /// <summary>Render-target width, height, 1/width, 1/height in pixels.</summary>
    public Vector4 Viewport;

    /// <summary>x = near plane, y = far plane (recovered from the projection), z = time in seconds, w = exposure.</summary>
    public Vector4 Clip;

    /// <summary>xyz = world direction the wind blows towards (normalized), w = strength (see <see cref="FrameEnvironment"/>).</summary>
    public Vector4 Wind;

    /// <summary>x = frequency (Hz), y = turbulence, z = noise scale (m).</summary>
    public Vector4 WindParams;

    /// <summary>rgb = linear fog light colour, a = 1 when fog is enabled.</summary>
    public Vector4 FogColor;

    /// <summary>x = density, y = fog base height, z = height density, w = sun scatter.</summary>
    public Vector4 FogParams;

    /// <summary>Last frame's unjittered view-projection (ADR 0163: motion vectors); this frame's when there is no history.</summary>
    public Matrix4x4 PreviousViewProjection;

    /// <summary>xy = this frame's projection jitter, zw = last frame's, in NDC (+Y up); zero without TAA.</summary>
    public Vector4 Jitter;

    /// <summary>
    /// x = last frame's time in seconds (<see cref="Clip"/>.z a frame ago), y = 1 when the previous-frame fields hold last
    /// frame's values (0: first frame of the view, they repeat this frame's), z = the jitter sample index, w = unused.
    /// </summary>
    public Vector4 Temporal;

    /// <summary>Last frame's <see cref="Wind"/> (foliage motion vectors evaluate the wind at both times).</summary>
    public Vector4 PreviousWind;

    /// <summary>Last frame's <see cref="WindParams"/>.</summary>
    public Vector4 PreviousWindParams;

    /// <summary>
    /// How the lit shaders apply the screen-space ambient occlusion at set 0 binding 5 (ADR 0165): x =
    /// <see cref="PostProcessSettings.SsaoLightAffect"/>, y = <see cref="PostProcessSettings.SsaoAoChannelAffect"/>, z = 1
    /// while an SSAO image is bound, w unused. Zero without SSAO (and in offscreen views), which with the white image makes
    /// <c>ambient_occlusion.slang</c>'s terms exactly 1. Set by <see cref="FrameContext.SetAmbientOcclusion"/>.
    /// </summary>
    public Vector4 AmbientOcclusion;

    /// <summary>Bytes in the std140 block.</summary>
    public const int Size = 6 * 64 + 12 * 16;

    /// <summary>Fills the block from a camera's matrices.</summary>
    public static FrameData From(in Matrix4x4 view, in Matrix4x4 projection, Vector3 cameraPosition, Extent2D extent,
        float time, float exposure) => From(view, projection, cameraPosition, extent, time, exposure, default);

    /// <summary>Fills the block from a camera's matrices and the world's wind and fog (no motion: the previous frame is this one).</summary>
    public static FrameData From(in Matrix4x4 view, in Matrix4x4 projection, Vector3 cameraPosition, Extent2D extent,
        float time, float exposure, in FrameEnvironment environment) =>
        From(view, projection, cameraPosition, extent, time, exposure, environment,
            FrameTemporal.Still(view * projection, time, environment));

    /// <summary>
    /// Fills the block from a camera's matrices, the world's wind and fog and the view's temporal data (ADR 0163). The
    /// projection is jittered by <paramref name="temporal"/>'s <see cref="FrameTemporal.Jitter"/>
    /// (<see cref="TemporalJitter.Apply"/>); everything else about it is unchanged.
    /// </summary>
    public static FrameData From(in Matrix4x4 view, in Matrix4x4 projection, Vector3 cameraPosition, Extent2D extent,
        float time, float exposure, in FrameEnvironment environment, in FrameTemporal temporal)
    {
        var jittered = TemporalJitter.Apply(projection, temporal.Jitter);
        Matrix4x4.Invert(jittered, out var invProj);
        var viewRotation = view;
        viewRotation.M41 = viewRotation.M42 = viewRotation.M43 = 0f;
        Matrix4x4.Invert(viewRotation, out var invViewRotation);
        var (near, far) = ClipPlanes(projection);
        float w = Math.Max(1u, extent.Width), h = Math.Max(1u, extent.Height);
        return new FrameData
        {
            View = view,
            Projection = jittered,
            ViewProjection = view * jittered,
            InverseProjection = invProj,
            InverseViewRotation = invViewRotation,
            CameraPosition = new Vector4(cameraPosition, 0f),
            Viewport = new Vector4(w, h, 1f / w, 1f / h),
            Clip = new Vector4(near, far, time, exposure),
            Wind = environment.Wind,
            WindParams = environment.WindParams,
            FogColor = environment.FogColor,
            FogParams = environment.FogParams,
            PreviousViewProjection = temporal.PreviousViewProjection,
            Jitter = new Vector4(temporal.Jitter, temporal.PreviousJitter.X, temporal.PreviousJitter.Y),
            Temporal = new Vector4(temporal.PreviousTime, temporal.HistoryValid ? 1f : 0f, temporal.JitterIndex, 0f),
            PreviousWind = temporal.PreviousEnvironment.Wind,
            PreviousWindParams = temporal.PreviousEnvironment.WindParams,
        };
    }

    /// <summary>
    /// Near and far planes of a System.Numerics projection with Vulkan's [0, 1] depth: perspective
    /// (<c>M34 = −1</c>: <c>M33 = f/(n−f)</c>, <c>M43 = n·f/(n−f)</c>) or orthographic (<c>M33 = 1/(n−f)</c>, <c>M43 = n/(n−f)</c>).
    /// </summary>
    public static (float Near, float Far) ClipPlanes(in Matrix4x4 projection)
    {
        if (projection.M34 != 0f)
        {
            var near = projection.M43 / projection.M33;
            var far = projection.M43 / (projection.M33 + 1f);
            return (near, far);
        }

        if (projection.M33 == 0f)
            return (0f, 1f);
        var n = projection.M43 / projection.M33;
        return (n, n - 1f / projection.M33);
    }
}

/// <summary>
/// The wind and fog parts of <see cref="FrameData"/>, packed for the shaders (see <c>include/frame.slang</c>). Default:
/// no wind, no fog.
/// </summary>
public readonly record struct FrameEnvironment(Vector4 Wind, Vector4 WindParams, Vector4 FogColor, Vector4 FogParams);

/// <summary>
/// The temporal part of <see cref="FrameData"/> (ADR 0163): last frame's unjittered view-projection, time and wind, and
/// both frames' projection jitter (NDC). <see cref="FrameContext"/> keeps it per view (<see cref="ViewHistory"/>).
/// </summary>
public readonly record struct FrameTemporal(
    Matrix4x4 PreviousViewProjection,
    Vector2 Jitter,
    Vector2 PreviousJitter,
    float PreviousTime,
    bool HistoryValid,
    int JitterIndex,
    FrameEnvironment PreviousEnvironment)
{
    /// <summary>No history: the previous frame is this one, no jitter.</summary>
    public static FrameTemporal Still(in Matrix4x4 viewProjection, float time, in FrameEnvironment environment) =>
        new(viewProjection, Vector2.Zero, Vector2.Zero, time, false, 0, environment);
}

/// <summary>
/// One view's camera history across frames (ADR 0163): what <see cref="FrameContext"/> wrote for it this frame and last
/// frame. <see cref="Record"/> is called on every camera write; the first write of a frame moves "current" to "previous"
/// when it came from the frame before, so writing a view twice in a frame (the picking pass, then the main pass) keeps
/// last frame's values. A view not written the frame before has no history: "previous" follows "current" (no motion).
/// </summary>
internal struct ViewHistory
{
    /// <summary>The frame the current values were written in (0: never).</summary>
    public ulong Frame;

    public Matrix4x4 ViewProjection;
    public Vector2 Jitter;
    public float Time;
    public FrameEnvironment Environment;

    public Matrix4x4 PreviousViewProjection;
    public Vector2 PreviousJitter;
    public float PreviousTime;
    public FrameEnvironment PreviousEnvironment;

    /// <summary>True when the previous values are last frame's (false on the view's first frame or after a gap).</summary>
    public bool HistoryValid;

    /// <summary>Records this frame's unjittered <paramref name="viewProjection"/>, jitter, time and wind for frame <paramref name="frame"/>.</summary>
    public void Record(ulong frame, in Matrix4x4 viewProjection, Vector2 jitter, float time, in FrameEnvironment environment)
    {
        if (Frame != frame)
        {
            HistoryValid = Frame != 0 && Frame + 1 == frame;
            if (HistoryValid)
            {
                PreviousViewProjection = ViewProjection;
                PreviousJitter = Jitter;
                PreviousTime = Time;
                PreviousEnvironment = Environment;
            }

            Frame = frame;
        }

        ViewProjection = viewProjection;
        Jitter = jitter;
        Time = time;
        Environment = environment;
        if (HistoryValid)
            return;
        PreviousViewProjection = viewProjection;
        PreviousJitter = jitter;
        PreviousTime = time;
        PreviousEnvironment = environment;
    }

    /// <summary>Forgets the history: the next frame has no motion (camera cuts, resizes).</summary>
    public void Reset() => this = default;

    /// <summary>The temporal block for this frame (jitter sample <paramref name="jitterIndex"/>).</summary>
    public readonly FrameTemporal ToTemporal(int jitterIndex) =>
        new(PreviousViewProjection, Jitter, PreviousJitter, PreviousTime, HistoryValid, jitterIndex, PreviousEnvironment);
}

/// <summary>
/// The per-frame shared descriptor set 0: camera (<see cref="FrameData"/>, binding 0) and lights (the
/// <see cref="LightEnvironment"/> UBO, binding 1), written once per frame and view into the frame slot's buffer and
/// bound by every scene pipeline — instead of each object writing and binding its own copies — and the sky's
/// image-based lighting (ADR 0150, <c>include/environment.slang</c>): binding 2 the prefiltered radiance cube, binding 3
/// the irradiance cube (the view's world's <see cref="SkyRadiance"/>, or a black 1×1 cube) and binding 4 the
/// <see cref="BrdfLut"/>; binding 5 the screen-space ambient occlusion of the main view (ADR 0163,
/// <c>include/ambient_occlusion.slang</c>: <see cref="SetAmbientOcclusion"/>, else a white 1×1 image); binding 6 the main
/// view's screen-space contact shadows (ADR 0167, <c>include/contact_shadows.slang</c>: <see cref="SetContactShadows"/>, else
/// the same white image). Set 1 is the shadow
/// set (<see cref="ShadowSystem"/> or the renderer's fallback); per-material data is set 2 and per-instance data
/// comes from the instance buffer (or push constants).
/// </summary>
/// <remarks>
/// <para><b>Views.</b> A frame can render several views (the main viewport, offscreen <see cref="SubViewport"/>s,
/// the object-ID pass): each has its own camera, lights and target extent. <see cref="CurrentView"/> (0 = the main
/// view, sized to the swapchain) selects which copy <see cref="Begin"/>, the <c>Ensure*</c> methods,
/// <see cref="Bind"/> and <see cref="Extent"/> refer to; the render server switches it around offscreen views
/// with <see cref="SetView"/>.</para>
/// <para>Call <see cref="Begin"/> once per frame (and view) before drawing. Renderer-owned drawers (sky, grid,
/// Spine) also call the <c>Ensure*</c> methods with the camera they were given, which write the data only if
/// nothing has this frame, so games that never call <see cref="Begin"/> keep working.</para>
/// <para>Pipelines built with <see cref="CreatePipelineLayout"/> share set 0 (and set 1 when they take the shadow
/// set) and the <see cref="PushConstantSize"/>-byte push range, so their layouts are compatible: set 0 bound once
/// stays bound across pipeline switches.</para>
/// </remarks>
public sealed unsafe class FrameContext : IDisposable
{
    public const uint FrameSetIndex = 0;
    public const uint ShadowSetIndex = 1;

    /// <summary>The push-constant range every scene pipeline declares (vertex + fragment), the spec minimum.</summary>
    public const uint PushConstantSize = 128;
    public const ShaderStageFlags PushConstantStages = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit;

    /// <summary>Views a frame can render (main view + offscreen views).</summary>
    public const int MaxViews = 8;

    private const int Slots = IVulkanContext.MaxFramesInFlight;

    private readonly IVulkanContext _ctx;
    private readonly ulong _lightsOffset;
    private readonly ulong _viewStride;
    private readonly GpuBuffer[] _buffers = new GpuBuffer[Slots];
    private readonly DescriptorSet[] _sets = new DescriptorSet[Slots * MaxViews];
    private readonly DescriptorPool _pool;
    private readonly ulong[] _cameraFrame = new ulong[Slots * MaxViews];
    private readonly ulong[] _lightsFrame = new ulong[Slots * MaxViews];
    private readonly long[] _environmentIds = new long[Slots * MaxViews]; // EnvironmentMaps.Id bound (0 = the fallback)
    private readonly long[] _occlusionIds = new long[Slots * MaxViews];   // ambient occlusion id bound at binding 5 (0 = white)
    private readonly long[] _contactIds = new long[Slots * MaxViews];     // contact shadows id bound at binding 6 (0 = white)
    private readonly ulong[] _occlusionImages = new ulong[Slots * MaxViews]; // the image views bound there (ids are per effect)
    private readonly ulong[] _contactImages = new ulong[Slots * MaxViews];
    private readonly ViewHistory[] _history = new ViewHistory[MaxViews];
    private readonly GpuImage _blackCube;
    private readonly GpuImage _brdfLut;
    private readonly GpuImage _white;
    private readonly Sampler _iblSampler;
    // Per view (ADR 0169: a post-processed sub-viewport has its own SSAO, contact shadows and jitter; view 0 is the main view).
    private readonly DescriptorImageInfo[] _occlusion = new DescriptorImageInfo[MaxViews];
    private readonly long[] _occlusionId = new long[MaxViews];
    private readonly Vector4[] _occlusionParams = new Vector4[MaxViews]; // FrameData.AmbientOcclusion
    private readonly DescriptorImageInfo[] _frameOcclusion = new DescriptorImageInfo[MaxViews]; // latched at the view's first bind of the frame
    private readonly long[] _frameOcclusionId = new long[MaxViews];
    private readonly ulong[] _occlusionFrame = new ulong[MaxViews];
    private readonly DescriptorImageInfo[] _contact = new DescriptorImageInfo[MaxViews];
    private readonly long[] _contactId = new long[MaxViews];
    private readonly DescriptorImageInfo[] _frameContact = new DescriptorImageInfo[MaxViews]; // latched with the ambient occlusion
    private readonly long[] _frameContactId = new long[MaxViews];
    private readonly Vector2[] _jitter = new Vector2[MaxViews];
    private readonly int[] _jitterIndex = new int[MaxViews];
    private Extent2D _viewExtent;
    private bool _disposed;

    internal FrameContext(IVulkanContext ctx)
    {
        _ctx = ctx;
        ctx.Vk.GetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        var alignment = Math.Max(256ul, props.Limits.MinUniformBufferOffsetAlignment);
        _lightsOffset = FreeListBlock.AlignUp(FrameData.Size, alignment);
        _viewStride = FreeListBlock.AlignUp(_lightsOffset + LightEnvironment.UboSize, alignment);

        ReadOnlySpan<DescriptorSetLayoutBinding> bindings =
        [
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = PushConstantStages },
            new() { Binding = 1, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = PushConstantStages },
            new() { Binding = 2, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 3, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 4, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = AmbientOcclusionBinding, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = ContactShadowBinding, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
        ];
        SetLayout = PipelineBuilder.CreateSetLayout(ctx, bindings, "frame set 0");
        _pool = PipelineBuilder.CreatePool(ctx, Slots * MaxViews,
        [
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 2 * Slots * MaxViews },
            new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = ImageBindings * Slots * MaxViews },
        ], "frame set 0");

        // Image-based lighting: one linear, mipmapped, clamped sampler for the cubes and the LUT.
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = 16f,
        };
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _iblSampler).Check("vkCreateSampler (image-based lighting)");
        _blackCube = GpuImage.Create(ctx, new GpuImageDesc(1, 1, Format.R16G16B16A16Sfloat, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit)
        {
            ArrayLayers = 6,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ViewType = ImageViewType.TypeCube,
        });
        ctx.Uploads.UploadImage(_blackCube, new byte[6 * 8]);
        _brdfLut = GpuImage.Create(ctx, new GpuImageDesc(BrdfLut.Size, BrdfLut.Size, Format.R16G16Sfloat,
            ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit));
        ctx.Uploads.UploadImage(_brdfLut, BrdfLut.ToHalfPixels());
        // Binding 5 without screen-space AO: no occlusion (every channel 1, so a shader may read any of them).
        _white = GpuImage.Create(ctx, new GpuImageDesc(1, 1, Format.R8G8B8A8Unorm, ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit));
        ctx.Uploads.UploadImage(_white, [255, 255, 255, 255]);
        ctx.Uploads.FlushIfRecording();

        for (var slot = 0; slot < Slots; slot++)
        {
            _buffers[slot] = GpuBuffer.Create(ctx, _viewStride * MaxViews, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
            _buffers[slot].MappedSpan.Clear();
            for (var view = 0; view < MaxViews; view++)
            {
                var set = PipelineBuilder.AllocateSet(ctx, _pool, SetLayout, "frame set 0");
                var baseOffset = (ulong)view * _viewStride;
                PipelineBuilder.WriteUniformBuffer(ctx, set, 0, _buffers[slot].Descriptor(baseOffset, FrameData.Size));
                PipelineBuilder.WriteUniformBuffer(ctx, set, 1, _buffers[slot].Descriptor(baseOffset + _lightsOffset, LightEnvironment.UboSize));
                PipelineBuilder.WriteImage(ctx, set, 2, FallbackCubeDescriptor);
                PipelineBuilder.WriteImage(ctx, set, 3, FallbackCubeDescriptor);
                PipelineBuilder.WriteImage(ctx, set, 4, BrdfLutDescriptor);
                PipelineBuilder.WriteImage(ctx, set, AmbientOcclusionBinding, NoOcclusionDescriptor);
                PipelineBuilder.WriteImage(ctx, set, ContactShadowBinding, NoOcclusionDescriptor);
                _sets[slot * MaxViews + view] = set;
            }
        }
    }

    /// <summary>
    /// Layout of set 0 (binding 0 camera, binding 1 lights, bindings 2–4 image-based lighting, binding 5 ambient occlusion,
    /// binding 6 contact shadows).
    /// </summary>
    public DescriptorSetLayout SetLayout { get; }

    /// <summary>Image-based-lighting bindings of set 0 (radiance cube, irradiance cube, BRDF LUT).</summary>
    internal const int EnvironmentBindings = 3;

    /// <summary>Image bindings of set 0: the image-based lighting, the ambient occlusion and the contact shadows.</summary>
    internal const int ImageBindings = EnvironmentBindings + 2;

    /// <summary>Set 0's screen-space ambient occlusion binding (<c>ssaoTexture</c> in <c>include/ambient_occlusion.slang</c>).</summary>
    public const uint AmbientOcclusionBinding = 5;

    /// <summary>
    /// Set 0's screen-space contact shadows binding (<c>contactShadowTexture</c> in <c>include/contact_shadows.slang</c>,
    /// ADR 0167): r = the primary light's contact shadow, g = the view depth it was computed at.
    /// </summary>
    public const uint ContactShadowBinding = 6;

    /// <summary>The white 1×1 image bound at <see cref="AmbientOcclusionBinding"/> without screen-space AO (and at <see cref="ContactShadowBinding"/>).</summary>
    internal DescriptorImageInfo NoOcclusionDescriptor => new()
    {
        Sampler = _iblSampler,
        ImageView = _white.View,
        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
    };

    /// <summary>
    /// The view the post effects' per-frame bindings apply to (<see cref="SetAmbientOcclusion(in DescriptorImageInfo, long)"/>,
    /// <see cref="SetContactShadows"/>, the clears): 0, the main view, except while a post-processed sub-viewport starts its
    /// effects (ADR 0169).
    /// </summary>
    internal int PostView
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value, MaxViews);
            field = value;
        }
    }

    /// <summary>
    /// <see cref="PostView"/>'s (the main view's) screen-space ambient occlusion from the next
    /// <see cref="Begin(ICamera, LightEnvironment?, bool)"/> on: <paramref name="image"/> (in <c>SHADER_READ_ONLY_OPTIMAL</c>
    /// whenever a lit pass reads it) identified by <paramref name="id"/> (any non-zero value that changes when the image
    /// does, e.g. after a resize). The renderer clears it at the start of every frame; an SSAO effect sets it again in its
    /// <c>OnBeginFrame</c>, before the frame's first <c>Begin</c> of the view (a later change applies next frame: a set
    /// already bound is never rewritten). Offscreen views bind the white image unless they are post-processed.
    /// </summary>
    internal void SetAmbientOcclusion(in DescriptorImageInfo image, long id) => SetAmbientOcclusion(image, id, 0f, 0f);

    /// <summary>
    /// <see cref="SetAmbientOcclusion(in DescriptorImageInfo, long)"/>, with how the lit shaders apply it (ADR 0165,
    /// <see cref="FrameData.AmbientOcclusion"/>, written by the main view's camera writes from now on):
    /// <paramref name="lightAffect"/> darkens direct light too, <paramref name="aoChannelAffect"/> multiplies with a
    /// material's own AO instead of taking the darker of the two.
    /// </summary>
    internal void SetAmbientOcclusion(in DescriptorImageInfo image, long id, float lightAffect, float aoChannelAffect)
    {
        ArgumentOutOfRangeException.ThrowIfZero(id);
        var view = PostView;
        _occlusion[view] = image;
        _occlusionId[view] = id;
        _occlusionParams[view] = new Vector4(Math.Clamp(lightAffect, 0f, 1f), Math.Clamp(aoChannelAffect, 0f, 1f), 1f, 0f);
    }

    /// <summary>Binds the white image at <see cref="AmbientOcclusionBinding"/> again (screen-space AO off).</summary>
    internal void ClearAmbientOcclusion()
    {
        var view = PostView;
        _occlusion[view] = default;
        _occlusionId[view] = 0;
        _occlusionParams[view] = default;
    }

    /// <summary>
    /// The main view's contact shadows (ADR 0167) from the next <see cref="Begin(ICamera, LightEnvironment?, bool)"/> on, like
    /// <see cref="SetAmbientOcclusion"/>: <paramref name="image"/> (RG: shadow, view depth; <c>SHADER_READ_ONLY_OPTIMAL</c>
    /// whenever a lit pass reads it) identified by <paramref name="id"/>. Cleared at the start of every frame; the contact
    /// shadow effect sets it again in its <c>OnBeginFrame</c>. Offscreen views always bind the white image.
    /// </summary>
    internal void SetContactShadows(in DescriptorImageInfo image, long id)
    {
        ArgumentOutOfRangeException.ThrowIfZero(id);
        _contact[PostView] = image;
        _contactId[PostView] = id;
    }

    /// <summary>Binds the white image at <see cref="ContactShadowBinding"/> again (contact shadows off).</summary>
    internal void ClearContactShadows()
    {
        _contact[PostView] = default;
        _contactId[PostView] = 0;
    }

    /// <summary>
    /// An offscreen view starts a frame (ADR 0169): no ambient occlusion, contact shadows or jitter until its post effects
    /// set them (<see cref="PostView"/>, <see cref="SetViewJitter"/>). The render server calls it for every view it renders.
    /// </summary>
    internal void ResetView(int view)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(view);
        _occlusion[view] = default;
        _occlusionId[view] = 0;
        _occlusionParams[view] = default;
        _contact[view] = default;
        _contactId[view] = 0;
        _jitter[view] = default;
        _jitterIndex[view] = 0;
    }

    /// <summary>
    /// The projection jitter of <paramref name="view"/> (view 0: <see cref="ProjectionJitter"/> and <see cref="JitterIndex"/>):
    /// a post-processed sub-viewport with TAA jitters its own view (ADR 0169).
    /// </summary>
    internal void SetViewJitter(int view, Vector2 jitter, int index)
    {
        _jitter[view] = jitter;
        _jitterIndex[view] = index;
    }

    /// <summary>The jitter sample index of <paramref name="view"/> this frame.</summary>
    internal int ViewJitterIndex(int view) => _jitterIndex[view];

    /// <summary>
    /// Sub-pixel offset (NDC, +Y up) added to the main view's projection on every camera write (TAA, ADR 0163):
    /// <see cref="FrameData.Projection"/> and the matrices derived from it are jittered, the camera's own matrices (culling,
    /// shadows, UI, light shafts) are not. Zero unless a post effect asks for jitter; the render server sets it before
    /// each frame. Offscreen views are jittered only when post-processed with TAA (<see cref="SetViewJitter"/>, ADR 0169).
    /// </summary>
    public Vector2 ProjectionJitter
    {
        get => _jitter[0];
        set => _jitter[0] = value;
    }

    /// <summary>The jitter sample index written to <see cref="FrameData.Temporal"/>.z with <see cref="ProjectionJitter"/>.</summary>
    public int JitterIndex
    {
        get => _jitterIndex[0];
        set => _jitterIndex[0] = value;
    }

    /// <summary>The camera history of <paramref name="view"/> (written by every camera write; see <see cref="ViewHistory"/>).</summary>
    internal ref readonly ViewHistory History(int view) => ref _history[view];

    /// <summary>Forgets every view's history: the next frame has no camera motion (resize, camera cut).</summary>
    public void ResetHistory() => Array.Clear(_history);

    /// <summary>
    /// The sky lighting the next <see cref="Begin(ICamera, LightEnvironment?, bool)"/> binds for the current view (null:
    /// the black fallback cube). The render server sets it from the view's world before each <c>Begin</c>.
    /// </summary>
    internal EnvironmentMaps? EnvironmentMaps { get; set; }

    /// <summary>The black 1×1 cube bound when a view has no sky lighting.</summary>
    internal DescriptorImageInfo FallbackCubeDescriptor => new()
    {
        Sampler = _iblSampler,
        ImageView = _blackCube.View,
        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
    };

    /// <summary>The split-sum BRDF table (<see cref="BrdfLut"/>, binding 4).</summary>
    internal DescriptorImageInfo BrdfLutDescriptor => new()
    {
        Sampler = _iblSampler,
        ImageView = _brdfLut.View,
        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
    };

    /// <summary>The view the frame is currently drawing (0 = main view); see <see cref="SetView"/>.</summary>
    public int CurrentView { get; private set; }

    /// <summary>Pixel size of the current view's target (the swapchain extent for the main view).</summary>
    public Extent2D Extent => CurrentView == 0 ? _ctx.SwapchainExtent : _viewExtent;

    /// <summary>Set 0 for the frame and view being recorded.</summary>
    public DescriptorSet CurrentSet => _sets[Index];

    private int Index => _ctx.FrameSlot * MaxViews + CurrentView;

    /// <summary>Time in seconds written to <see cref="FrameData.Clip"/>.z by <see cref="Begin"/>.</summary>
    public float Time { get; set; }

    /// <summary>Wind and fog written into <see cref="FrameData"/> by later camera writes (the render server sets the world's).</summary>
    public FrameEnvironment Environment { get; set; }

    /// <summary>
    /// Switches the view later calls refer to: 0 is the main view; 1 .. <see cref="MaxViews"/> - 1 are offscreen
    /// views of <paramref name="extent"/> pixels (ignored for view 0).
    /// </summary>
    public void SetView(int view, Extent2D extent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(view);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(view, MaxViews);
        CurrentView = view;
        _viewExtent = extent;
    }

    /// <summary>Writes this frame's camera and lights for the current view. Call once per frame before the main-pass draws.</summary>
    public void Begin(ICamera camera, LightEnvironment? lights) => Begin(camera, lights, shadows: true);

    /// <summary>
    /// <see cref="Begin(ICamera, LightEnvironment?)"/>; <paramref name="shadows"/> false tells the lit shaders not to
    /// sample shadow maps (offscreen views of another world).
    /// </summary>
    public void Begin(ICamera camera, LightEnvironment? lights, bool shadows)
    {
        ArgumentNullException.ThrowIfNull(camera);
        WriteCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);
        if (lights is not null)
            WriteLights(lights, camera.Position, shadows);
        BindEnvironment();
    }

    // Points bindings 2 and 3 of this frame slot's and view's set at EnvironmentMaps when they changed, and binding 5 at
    // the ambient occlusion. The slot's earlier frame has finished (FrameStarted), and a view shows one world per frame,
    // so the set is not bound yet.
    private void BindEnvironment()
    {
        if (!_ctx.FrameStarted)
            return;
        var index = Index;
        var view = CurrentView;
        if (_occlusionFrame[view] != _ctx.FrameNumber)
        {
            // Latched once per frame: a set bound earlier this frame is never rewritten (a change applies next frame).
            _occlusionFrame[view] = _ctx.FrameNumber;
            _frameOcclusion[view] = _occlusion[view];
            _frameOcclusionId[view] = _occlusionId[view];
            _frameContact[view] = _contact[view];
            _frameContactId[view] = _contactId[view];
        }

        var occlusionId = _frameOcclusionId[view];
        var occlusionImage = occlusionId == 0 ? 0 : _frameOcclusion[view].ImageView.Handle;
        if (_occlusionIds[index] != occlusionId || _occlusionImages[index] != occlusionImage)
        {
            _occlusionIds[index] = occlusionId;
            _occlusionImages[index] = occlusionImage;
            PipelineBuilder.WriteImage(_ctx, _sets[index], AmbientOcclusionBinding, occlusionId == 0 ? NoOcclusionDescriptor : _frameOcclusion[view]);
        }

        var contactId = _frameContactId[view];
        var contactImage = contactId == 0 ? 0 : _frameContact[view].ImageView.Handle;
        if (_contactIds[index] != contactId || _contactImages[index] != contactImage)
        {
            _contactIds[index] = contactId;
            _contactImages[index] = contactImage;
            PipelineBuilder.WriteImage(_ctx, _sets[index], ContactShadowBinding, contactId == 0 ? NoOcclusionDescriptor : _frameContact[view]);
        }

        var maps = EnvironmentMaps;
        var id = maps?.Id ?? 0;
        if (_environmentIds[index] == id)
            return;
        _environmentIds[index] = id;

        var images = stackalloc DescriptorImageInfo[2];
        if (maps is null)
        {
            images[0] = images[1] = FallbackCubeDescriptor;
        }
        else
        {
            images[0] = new DescriptorImageInfo { Sampler = _iblSampler, ImageView = maps.Radiance, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
            images[1] = new DescriptorImageInfo { Sampler = _iblSampler, ImageView = maps.Irradiance, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        }

        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _sets[index],
            DstBinding = 2,
            DescriptorCount = 2,
            DescriptorType = DescriptorType.CombinedImageSampler,
            PImageInfo = images,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
    }

    /// <summary>Writes the camera block for this frame and view (overwrites anything written earlier this frame).</summary>
    public void WriteCamera(in Matrix4x4 view, in Matrix4x4 projection, Vector3 position)
    {
        if (!_ctx.FrameStarted) return;
        var index = Index;
        var current = CurrentView;
        ref var history = ref _history[current];
        history.Record(_ctx.FrameNumber, view * projection, _jitter[current], Time, Environment);
        var temporal = history.ToTemporal(_jitterIndex[current]);
        var data = FrameData.From(view, projection, position, Extent, Time, _ctx.Exposure, Environment, temporal);
        data.AmbientOcclusion = _occlusionParams[current];
        _buffers[_ctx.FrameSlot].Write(data, (ulong)CurrentView * _viewStride);
        _cameraFrame[index] = _ctx.FrameNumber;
    }

    /// <summary>Writes the lights block for this frame and view.</summary>
    public void WriteLights(LightEnvironment lights, Vector3 cameraPosition) => WriteLights(lights, cameraPosition, shadows: true);

    /// <summary>Writes the lights block; <paramref name="shadows"/> false disables shadow-map sampling for this view.</summary>
    public void WriteLights(LightEnvironment lights, Vector3 cameraPosition, bool shadows)
    {
        ArgumentNullException.ThrowIfNull(lights);
        if (!_ctx.FrameStarted) return;
        var offset = (int)((ulong)CurrentView * _viewStride + _lightsOffset);
        lights.WriteUbo(_buffers[_ctx.FrameSlot].MappedSpan.Slice(offset, LightEnvironment.UboSize), cameraPosition, shadows);
        _lightsFrame[Index] = _ctx.FrameNumber;
    }

    /// <summary>Writes the camera block unless something already did this frame (for the current view).</summary>
    public void EnsureCamera(in Matrix4x4 view, in Matrix4x4 projection, Vector3 position)
    {
        if (_ctx.FrameStarted && _cameraFrame[Index] != _ctx.FrameNumber)
            WriteCamera(view, projection, position);
    }

    /// <summary>Writes the lights block unless something already did this frame (for the current view).</summary>
    public void EnsureLights(LightEnvironment lights, Vector3 cameraPosition)
    {
        if (_ctx.FrameStarted && _lightsFrame[Index] != _ctx.FrameNumber)
            WriteLights(lights, cameraPosition);
    }

    /// <summary>True once this frame's camera block has been written for the current view.</summary>
    public bool HasCameraThisFrame => _ctx.FrameStarted && _cameraFrame[Index] == _ctx.FrameNumber;

    /// <summary>Binds set 0 (and, when given, the shadow set as set 1) for pipelines made with <see cref="CreatePipelineLayout"/>.</summary>
    public void Bind(CommandBuffer cb, PipelineLayout layout, IShadowDescriptors? shadows = null)
    {
        var sets = stackalloc DescriptorSet[2];
        sets[0] = CurrentSet;
        var count = 1u;
        if (shadows is not null)
            sets[count++] = shadows.GetMainSet();
        _ctx.Vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, layout, FrameSetIndex, count, sets, 0, null);
    }

    /// <summary>
    /// A scene pipeline layout: set 0 = frame, set 1 = <paramref name="shadows"/>' layout (when given), then
    /// <paramref name="extraSets"/>; one <see cref="PushConstantSize"/>-byte vertex+fragment push range.
    /// </summary>
    internal PipelineLayout CreatePipelineLayout(IShadowDescriptors? shadows, ReadOnlySpan<DescriptorSetLayout> extraSets, string what)
    {
        var layouts = stackalloc DescriptorSetLayout[2 + extraSets.Length];
        var count = 0;
        layouts[count++] = SetLayout;
        if (shadows is not null)
            layouts[count++] = shadows.MainDescSetLayout;
        foreach (var extra in extraSets)
            layouts[count++] = extra;
        return PipelineBuilder.CreateLayout(_ctx, new ReadOnlySpan<DescriptorSetLayout>(layouts, count),
            PushConstantSize, PushConstantStages, what);
    }

    /// <summary>Destroys the set and buffers through the deletion queue.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var buffer in _buffers)
            buffer.Dispose();
        _blackCube.Dispose();
        _brdfLut.Dispose();
        _white.Dispose();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_iblSampler));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_pool));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(SetLayout));
    }
}
