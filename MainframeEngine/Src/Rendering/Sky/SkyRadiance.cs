using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>The image-based lighting maps one view binds in set 0 (bindings 2 and 3, <see cref="FrameContext"/>).</summary>
internal sealed class EnvironmentMaps(ImageView radiance, ImageView irradiance)
{
    private static long s_nextId;

    /// <summary>Unique per instance (never 0, the fallback's id): set 0 is rewritten when the bound id changes.</summary>
    public long Id { get; } = Interlocked.Increment(ref s_nextId);

    public ImageView Radiance { get; } = radiance;
    public ImageView Irradiance { get; } = irradiance;
}

/// <summary>
/// Image-based lighting from a <see cref="SkyEnvironment"/> (ADR 0150, G6.2-lite): the sky's own fragment shader
/// renders into a <see cref="Size"/>² <c>R16G16B16A16_SFLOAT</c> cube (one 90° camera per face through the frame data
/// and the sky push constants, so every sky type — procedural, panoramic, cubemap and later ones — lights the scene
/// the way it looks), whose mip chain is blitted; then fragment passes prefilter it with GGX importance sampling into
/// the <see cref="RadianceMips"/> mips of the radiance cube (perceptual roughness m / (mips − 1)) and convolve it with
/// a cosine lobe into a <see cref="IrradianceSize"/>² irradiance cube. No compute.
/// </summary>
/// <remarks>
/// <para>A bake is 6 + 6·<see cref="RadianceMips"/> + 6 small passes, recorded with the frame's offscreen work (no render
/// pass active) and only when the sky changes: another <see cref="SkyEnvironment"/>, or other push constants (sun
/// direction, colours). Parameter changes re-bake at most every <see cref="MinFramesBetweenBakes"/> frames, so a moving
/// sun costs a bake every few frames. The images are rewritten in place behind a barrier that waits for earlier frames'
/// sampling. Allocation-free once created.</para>
/// <para>The capture leaves the sun disk out (<c>SkyParams.Sun.Z</c> = 1): directional lights light with the sun, and
/// a sub-texel disk would flicker in the cube as it moves.</para>
/// </remarks>
internal sealed unsafe class SkyRadiance : IDisposable
{
    /// <summary>Pixels across a face of the captured sky and of the radiance cube's mip 0.</summary>
    public const uint Size = 128;

    /// <summary>Radiance mips (128 … 4): mip m is roughness m / 5. <c>kRadianceMaxMip</c> in <c>environment.slang</c> is this − 1.</summary>
    public const uint RadianceMips = 6;

    /// <summary>Pixels across a face of the irradiance cube.</summary>
    public const uint IrradianceSize = 32;

    /// <summary>The fewest frames between two bakes of the same sky (its parameters keep changing: a moving sun).</summary>
    public const int MinFramesBetweenBakes = 4;

    private const Format CubeFormat = Format.R16G16B16A16Sfloat;
    private const uint PrefilterSamples = 128;
    private const uint IrradianceSamples = 256;

    private readonly IVulkanContext _ctx;
    private readonly GpuImage _source;
    private readonly GpuImage _radiance;
    private readonly GpuImage _irradiance;
    private readonly uint _sourceMips;
    private readonly RenderPass _renderPass;
    private readonly Framebuffer[] _sourceFaces = new Framebuffer[6];
    private readonly Framebuffer[] _radianceFaces = new Framebuffer[6 * RadianceMips];
    private readonly Framebuffer[] _irradianceFaces = new Framebuffer[6];
    private readonly GpuBuffer _cameras;
    private readonly DescriptorPool _pool;
    private readonly DescriptorSet[] _faceSets = new DescriptorSet[6];
    private readonly DescriptorSetLayout _sourceLayout;
    private readonly DescriptorSet _sourceSet;
    private readonly Sampler _sourceSampler;
    private readonly PipelineLayout _filterLayout;
    private readonly Pipeline _prefilter;
    private readonly Pipeline _irradianceFilter;
    private SkyEnvironment? _bakedSky;
    private SkyEnvironment.SkyParams _bakedParams;
    private ulong _bakedFrame;
    private ulong _updatedFrame;
    private bool _disposed;

    public SkyRadiance(IVulkanContext ctx)
    {
        _ctx = ctx;
        _sourceMips = SupportsLinearBlit(ctx) ? GpuImageDesc.FullMipChain(Size, Size) : 1u;
        const ImageUsageFlags target = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit;
        _source = CreateCube(ctx, Size, _sourceMips, target | (_sourceMips > 1 ? ImageUsageFlags.TransferSrcBit | ImageUsageFlags.TransferDstBit : 0));
        _radiance = CreateCube(ctx, Size, RadianceMips, target);
        _irradiance = CreateCube(ctx, IrradianceSize, 1, target);
        Maps = new EnvironmentMaps(_radiance.View, _irradiance.View);

        _renderPass = CreateRenderPass(ctx);
        for (uint face = 0; face < 6; face++)
        {
            _sourceFaces[face] = CreateFramebuffer(_source, face, 0, Size);
            _irradianceFaces[face] = CreateFramebuffer(_irradiance, face, 0, IrradianceSize);
            for (uint mip = 0; mip < RadianceMips; mip++)
                _radianceFaces[mip * 6 + face] = CreateFramebuffer(_radiance, face, mip, Size >> (int)mip);
        }

        // Six 90° cameras at the origin (no fog: the capture is the sky alone) and an empty lights block.
        ctx.Vk.GetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        var stride = FreeListBlock.AlignUp(FrameData.Size, Math.Max(256ul, props.Limits.MinUniformBufferOffsetAlignment));
        var lightsOffset = stride * 6;
        _cameras = GpuBuffer.Create(ctx, lightsOffset + LightEnvironment.UboSize, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
        _cameras.MappedSpan.Clear();
        var projection = FaceProjection;
        for (var face = 0; face < 6; face++)
            _cameras.Write(FrameData.From(FaceView(face), projection, Vector3.Zero, new Extent2D(Size, Size), 0f, 1f), (ulong)face * stride);

        _sourceLayout = PipelineBuilder.CreateSetLayout(ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "sky radiance source");
        _pool = PipelineBuilder.CreatePool(ctx, 7,
        [
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 12 },
            new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 6 * FrameContext.ImageBindings + 1 },
        ], "sky radiance");
        var frame = ctx.Frame;
        for (var face = 0; face < 6; face++)
        {
            var set = PipelineBuilder.AllocateSet(ctx, _pool, frame.SetLayout, "sky capture frame");
            PipelineBuilder.WriteUniformBuffer(ctx, set, 0, _cameras.Descriptor((ulong)face * stride, FrameData.Size));
            PipelineBuilder.WriteUniformBuffer(ctx, set, 1, _cameras.Descriptor(lightsOffset, LightEnvironment.UboSize));
            PipelineBuilder.WriteImage(ctx, set, 2, frame.FallbackCubeDescriptor);
            PipelineBuilder.WriteImage(ctx, set, 3, frame.FallbackCubeDescriptor);
            PipelineBuilder.WriteImage(ctx, set, 4, frame.BrdfLutDescriptor);
            PipelineBuilder.WriteImage(ctx, set, FrameContext.AmbientOcclusionBinding, frame.NoOcclusionDescriptor);
            _faceSets[face] = set;
        }

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Linear,
            MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MaxLod = _sourceMips,
        };
        ctx.Vk.CreateSampler(ctx.Device, in samplerInfo, null, out _sourceSampler).Check("vkCreateSampler (sky radiance)");
        _sourceSet = PipelineBuilder.AllocateSet(ctx, _pool, _sourceLayout, "sky radiance source");
        PipelineBuilder.WriteImage(ctx, _sourceSet, 0, new DescriptorImageInfo
        {
            Sampler = _sourceSampler,
            ImageView = _source.View,
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
        });

        _filterLayout = PipelineBuilder.CreateLayout(ctx, [_sourceLayout], (uint)sizeof(BakePush), ShaderStageFlags.FragmentBit, "sky radiance filter");
        _prefilter = PipelineBuilder.Create(ctx, new PipelineState(), _filterLayout, _renderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Sky/SkyPrefilter.vk.frag.spv", [], [], "sky radiance prefilter");
        _irradianceFilter = PipelineBuilder.Create(ctx, new PipelineState(), _filterLayout, _renderPass,
            "Shaders/Post/Fullscreen.vk.vert.spv", "Shaders/Sky/SkyIrradiance.vk.frag.spv", [], [], "sky irradiance");
    }

    /// <summary>The cubes to bind, once <see cref="IsBaked"/>.</summary>
    public EnvironmentMaps Maps { get; }

    /// <summary>True once a bake has been recorded (the maps hold a sky).</summary>
    public bool IsBaked => _bakedSky is not null;

    /// <summary>Bakes recorded since creation (diagnostics, tests).</summary>
    public int BakeCount { get; private set; }

    /// <summary>The 90° projection of a capture face (aspect 1).</summary>
    public static Matrix4x4 FaceProjection => Matrix4x4.CreatePerspectiveFieldOfView(MathF.PI / 2f, 1f, 0.1f, 10f);

    /// <summary>
    /// The view matrix of cube face <paramref name="face"/> (Vulkan order +X, −X, +Y, −Y, +Z, −Z) for the sky shaders'
    /// ray (<c>skyRay</c>, flipped viewport): pixel (u, v) of the face looks along the cube direction of texel (u, v).
    /// The view's right, up and back axes are the face's s axis, −t axis and −forward (a mirror: cube faces are
    /// left-handed).
    /// </summary>
    public static Matrix4x4 FaceView(int face)
    {
        var (forward, right, down) = face switch
        {
            0 => (Vector3.UnitX, -Vector3.UnitZ, -Vector3.UnitY),
            1 => (-Vector3.UnitX, Vector3.UnitZ, -Vector3.UnitY),
            2 => (Vector3.UnitY, Vector3.UnitX, Vector3.UnitZ),
            3 => (-Vector3.UnitY, Vector3.UnitX, -Vector3.UnitZ),
            4 => (Vector3.UnitZ, Vector3.UnitX, -Vector3.UnitY),
            5 => (-Vector3.UnitZ, -Vector3.UnitX, -Vector3.UnitY),
            _ => throw new ArgumentOutOfRangeException(nameof(face)),
        };
        var up = -down;
        var back = -forward;
        // Inverse view (view → world) has rows right, up, back; the view is its transpose.
        var inverse = new Matrix4x4(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            back.X, back.Y, back.Z, 0f,
            0f, 0f, 0f, 1f);
        return Matrix4x4.Transpose(inverse);
    }

    /// <summary>
    /// Bakes <paramref name="sky"/> when it changed since the last bake (at most once per frame, and parameter changes
    /// at most every <see cref="MinFramesBetweenBakes"/> frames). Call with the frame's command buffer and no render
    /// pass active.
    /// </summary>
    public void Update(CommandBuffer cb, SkyEnvironment sky)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var frame = _ctx.FrameNumber;
        if (_updatedFrame == frame)
            return;
        _updatedFrame = frame;

        var parameters = sky.CaptureParams;
        var sameSky = ReferenceEquals(sky, _bakedSky);
        if (sameSky && MemoryMarshal.AsBytes(new ReadOnlySpan<SkyEnvironment.SkyParams>(in parameters))
                .SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<SkyEnvironment.SkyParams>(in _bakedParams))))
            return;
        if (sameSky && frame - _bakedFrame < MinFramesBetweenBakes)
            return;

        Bake(cb, sky, parameters);
        _bakedSky = sky;
        _bakedParams = parameters;
        _bakedFrame = frame;
        BakeCount++;
    }

    private void Bake(CommandBuffer cb, SkyEnvironment sky, in SkyEnvironment.SkyParams parameters)
    {
        var vk = _ctx.Vk;

        // Everything becomes writable once earlier frames have finished sampling it (the old contents are dropped).
        var barriers = stackalloc ImageMemoryBarrier[4];
        barriers[0] = Barrier(_source, 0, 1, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, 0, AccessFlags.ColorAttachmentWriteBit);
        barriers[1] = Barrier(_radiance, 0, RadianceMips, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, 0, AccessFlags.ColorAttachmentWriteBit);
        barriers[2] = Barrier(_irradiance, 0, 1, ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal, 0, AccessFlags.ColorAttachmentWriteBit);
        var count = 3u;
        if (_sourceMips > 1)
            barriers[count++] = Barrier(_source, 1, _sourceMips - 1, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 0, AccessFlags.TransferWriteBit);
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.FragmentShaderBit,
            PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.TransferBit, 0, 0, null, 0, null, count, barriers);

        // 1. The sky, one face at a time.
        var size = new Extent2D(Size, Size);
        for (var face = 0; face < 6; face++)
        {
            BeginPass(cb, _sourceFaces[face], size);
            sky.DrawCapture(cb, _renderPass, _faceSets[face], size, parameters);
            vk.CmdEndRenderPass(cb);
        }

        // 2. Its mip chain (filtered importance sampling reads coarser mips for wider lobes).
        BuildSourceMips(cb);

        // 3. GGX-prefiltered radiance, mip by mip.
        for (uint mip = 0; mip < RadianceMips; mip++)
        {
            var extent = new Extent2D(Size >> (int)mip, Size >> (int)mip);
            for (uint face = 0; face < 6; face++)
            {
                var push = new BakePush
                {
                    Face = face,
                    Roughness = mip / (float)(RadianceMips - 1),
                    TargetSize = extent.Width,
                    SourceSize = Size,
                    SampleCount = PrefilterSamples,
                    SourceMaxMip = _sourceMips - 1,
                };
                FilterPass(cb, _prefilter, _radianceFaces[mip * 6 + face], extent, push);
            }
        }

        // 4. Irradiance.
        var irradianceExtent = new Extent2D(IrradianceSize, IrradianceSize);
        for (uint face = 0; face < 6; face++)
        {
            var push = new BakePush
            {
                Face = face,
                TargetSize = IrradianceSize,
                SourceSize = Size,
                SampleCount = IrradianceSamples,
                SourceMaxMip = _sourceMips - 1,
            };
            FilterPass(cb, _irradianceFilter, _irradianceFaces[face], irradianceExtent, push);
        }

        // Ready for the scene's fragment shaders.
        barriers[0] = Barrier(_radiance, 0, RadianceMips, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit);
        barriers[1] = Barrier(_irradiance, 0, 1, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit);
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 2, barriers);
    }

    private void BuildSourceMips(CommandBuffer cb)
    {
        var vk = _ctx.Vk;
        var barrier = stackalloc ImageMemoryBarrier[1];
        if (_sourceMips == 1)
        {
            barrier[0] = Barrier(_source, 0, 1, ImageLayout.ColorAttachmentOptimal, ImageLayout.ShaderReadOnlyOptimal,
                AccessFlags.ColorAttachmentWriteBit, AccessFlags.ShaderReadBit);
            vk.CmdPipelineBarrier(cb, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.FragmentShaderBit,
                0, 0, null, 0, null, 1, barrier);
            return;
        }

        barrier[0] = Barrier(_source, 0, 1, ImageLayout.ColorAttachmentOptimal, ImageLayout.TransferSrcOptimal,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.TransferReadBit);
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, barrier);
        for (uint mip = 1; mip < _sourceMips; mip++)
        {
            int from = (int)Math.Max(1u, Size >> (int)(mip - 1)), to = (int)Math.Max(1u, Size >> (int)mip);
            var blit = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip - 1, 0, 6),
                DstSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, mip, 0, 6),
            };
            blit.SrcOffsets[1] = new Offset3D(from, from, 1);
            blit.DstOffsets[1] = new Offset3D(to, to, 1);
            vk.CmdBlitImage(cb, _source.Handle, ImageLayout.TransferSrcOptimal, _source.Handle, ImageLayout.TransferDstOptimal,
                1, &blit, Filter.Linear);
            barrier[0] = Barrier(_source, mip, 1, ImageLayout.TransferDstOptimal, ImageLayout.TransferSrcOptimal,
                AccessFlags.TransferWriteBit, AccessFlags.TransferReadBit);
            vk.CmdPipelineBarrier(cb, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit, 0, 0, null, 0, null, 1, barrier);
        }

        barrier[0] = Barrier(_source, 0, _sourceMips, ImageLayout.TransferSrcOptimal, ImageLayout.ShaderReadOnlyOptimal,
            AccessFlags.TransferWriteBit, AccessFlags.ShaderReadBit);
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit, 0, 0, null, 0, null, 1, barrier);
    }

    private void FilterPass(CommandBuffer cb, Pipeline pipeline, Framebuffer target, Extent2D extent, in BakePush push)
    {
        var vk = _ctx.Vk;
        BeginPass(cb, target, extent);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
        var set = _sourceSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _filterLayout, 0, 1, &set, 0, null);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);
        fixed (BakePush* p = &push)
            vk.CmdPushConstants(cb, _filterLayout, ShaderStageFlags.FragmentBit, 0, (uint)sizeof(BakePush), p);
        vk.CmdDraw(cb, 3, 1, 0, 0);
        vk.CmdEndRenderPass(cb);
    }

    private void BeginPass(CommandBuffer cb, Framebuffer framebuffer, Extent2D extent)
    {
        var info = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _renderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Extent = extent },
        };
        _ctx.Vk.CmdBeginRenderPass(cb, &info, SubpassContents.Inline);
    }

    private static ImageMemoryBarrier Barrier(GpuImage image, uint baseMip, uint mips, ImageLayout from, ImageLayout to,
        AccessFlags srcAccess, AccessFlags dstAccess) => new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image.Handle,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, baseMip, mips, 0, 6),
        };

    private static GpuImage CreateCube(IVulkanContext ctx, uint size, uint mips, ImageUsageFlags usage) =>
        GpuImage.Create(ctx, new GpuImageDesc(size, size, CubeFormat, usage)
        {
            ArrayLayers = 6,
            MipLevels = mips,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ViewType = ImageViewType.TypeCube,
        });

    private Framebuffer CreateFramebuffer(GpuImage image, uint face, uint mip, uint size)
    {
        var view = image.CreateView(ImageViewType.Type2D, face, 1, mip, 1);
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _renderPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = Math.Max(1u, size),
            Height = Math.Max(1u, size),
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out var framebuffer).Check("vkCreateFramebuffer (sky radiance)");
        return framebuffer;
    }

    // One RGBA16F colour attachment kept in COLOR_ATTACHMENT_OPTIMAL: the bake's barriers do every transition, and every
    // pass covers its whole attachment (nothing to load).
    private static RenderPass CreateRenderPass(IVulkanContext ctx)
    {
        var attachment = new AttachmentDescription
        {
            Format = CubeFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.DontCare,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.ColorAttachmentOptimal,
            FinalLayout = ImageLayout.ColorAttachmentOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };
        ctx.Vk.CreateRenderPass(ctx.Device, in info, null, out var renderPass).Check("vkCreateRenderPass (sky radiance)");
        return renderPass;
    }

    private static bool SupportsLinearBlit(IVulkanContext ctx)
    {
        ctx.Vk.GetPhysicalDeviceFormatProperties(ctx.PhysicalDevice, CubeFormat, out var props);
        const FormatFeatureFlags needed = FormatFeatureFlags.BlitSrcBit | FormatFeatureFlags.BlitDstBit |
                                          FormatFeatureFlags.SampledImageFilterLinearBit;
        return (props.OptimalTilingFeatures & needed) == needed;
    }

    /// <summary>Releases everything through the deletion queue (frames in flight may still sample the cubes).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        var deletions = _ctx.Deletions;
        deletions.Enqueue(GpuDeletion.Of(_prefilter));
        deletions.Enqueue(GpuDeletion.Of(_irradianceFilter));
        deletions.Enqueue(GpuDeletion.Of(_filterLayout));
        foreach (var fb in _sourceFaces)
            deletions.Enqueue(GpuDeletion.Of(fb));
        foreach (var fb in _radianceFaces)
            deletions.Enqueue(GpuDeletion.Of(fb));
        foreach (var fb in _irradianceFaces)
            deletions.Enqueue(GpuDeletion.Of(fb));
        deletions.Enqueue(GpuDeletion.Of(_renderPass));
        deletions.Enqueue(GpuDeletion.Of(_pool));
        deletions.Enqueue(GpuDeletion.Of(_sourceLayout));
        deletions.Enqueue(GpuDeletion.Of(_sourceSampler));
        _cameras.Dispose();
        _source.Dispose();
        _radiance.Dispose();
        _irradiance.Dispose();
    }

    // ibl_bake.slang's BakePush (std430).
    [StructLayout(LayoutKind.Sequential)]
    private struct BakePush
    {
        public uint Face;
        public float Roughness;
        public float TargetSize;
        public float SourceSize;
        public uint SampleCount;
        public float SourceMaxMip;
    }
}
