using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using StbImageSharp;
using ShaderModule = Silk.NET.Vulkan.ShaderModule;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkImage = Silk.NET.Vulkan.Image;

namespace MainframeEngine;

/// <summary>
/// Full-screen sky backdrop rendered before all other geometry.
/// Supports procedural gradient, equirectangular panoramic, and cubemap sky types.
/// </summary>
public class SkyEnvironment : IDisposable
{
    // ─── Procedural parameters ────────────────────────────────────────────────

    /// <summary>Zenith / upper sky color (procedural only).</summary>
    public Vector3 SkyColor { get; set; } = new(0.18f, 0.48f, 0.87f);
    /// <summary>Color at the horizon band (procedural only).</summary>
    public Vector3 HorizonColor { get; set; } = new(0.70f, 0.85f, 1.00f);
    /// <summary>Color of the ground / nadir hemisphere (procedural only).</summary>
    public Vector3 GroundColor { get; set; } = new(0.15f, 0.14f, 0.13f);
    /// <summary>World-space direction toward the sun (procedural only).</summary>
    public Vector3 SunDirection { get; set; } = Vector3.Normalize(new(0.3f, 1f, 0.5f));
    /// <summary>Sun disk color (procedural only).</summary>
    public Vector3 SunColor { get; set; } = new(1.00f, 0.95f, 0.85f);
    /// <summary>Sun brightness multiplier (procedural only).</summary>
    public float SunIntensity { get; set; } = 20f;
    /// <summary>Angular radius of the sun disk in degrees. Default ≈ real sun (0.53°).</summary>
    public float SunAngularRadius { get; set; } = 0.53f;
    /// <summary>How sharply sky blends into horizon / ground. Higher = tighter band.</summary>
    public float HorizonSharpness { get; set; } = 6f;

    public SkyEnvironmentType Type { get; }

    // ─── UBO (must match std140 layout in all sky fragment shaders) ───────────

    [StructLayout(LayoutKind.Sequential)]
    private struct SkyUbo
    {
        public Matrix4x4 InvProj;            // 64 bytes
        public Matrix4x4 InvViewRot;         // 64 bytes
        public Vector4   SkyColor;           // 16 bytes
        public Vector4   HorizonColor;       // 16 bytes
        public Vector4   GroundColor;        // 16 bytes
        public Vector4   SunDirection;       // 16 bytes  (xyz = dir, w = unused)
        public Vector4   SunColorIntensity;  // 16 bytes  (rgb = color, a = intensity)
        public float     SunSize;            // 4 bytes   (cos of angular radius)
        public float     HorizonSharpness;   // 4 bytes
        public float     Pad0, Pad1;         // 8 bytes
    }                                        // Total: 240 bytes

    // ─── Vulkan state ─────────────────────────────────────────────────────────

    private IVulkanContext?     _ctx;
    private Pipeline            _pipeline;
    private PipelineLayout      _pipelineLayout;
    private DescriptorSetLayout _uboSetLayout;
    private DescriptorSetLayout _texSetLayout;   // only for Panoramic / Cubemap
    private DescriptorPool      _descPool;
    private DescriptorSet[]     _uboSets = null!;
    private DescriptorSet       _texSet;         // shared across frames (static texture)
    private VkBuffer[]          _uboBuffers = null!;
    private DeviceMemory[]      _uboMemory  = null!;
    private nint[]              _uboMapped  = null!;

    // Texture resources (Panoramic / Cubemap only)
    private VkImage                 _texImage;
    private DeviceMemory            _texMemory;
    private ImageView               _texView;
    private Sampler _texSampler;

    private bool HasTexture => Type is SkyEnvironmentType.Panoramic or SkyEnvironmentType.Cubemap;

    // ─── Constructor ──────────────────────────────────────────────────────────

    protected SkyEnvironment(
        IRenderer renderer,
        SkyEnvironmentType type,
        string? panoramicPath = null,
        string[]? cubeFacePaths = null)
    {
        Type = type;

        if (renderer is not IVulkanContext ctx) return;
        _ctx = ctx;

        switch (type)
        {
            case SkyEnvironmentType.Panoramic when panoramicPath is not null:
                LoadPanoramic(ctx, panoramicPath);
                break;
            case SkyEnvironmentType.Cubemap when cubeFacePaths is not null:
                LoadCubemap(ctx, cubeFacePaths);
                break;
        }

        CreatePipeline(ctx);
    }

    // ─── Public draw method ───────────────────────────────────────────────────

    /// <summary>
    /// Records sky draw commands into the current command buffer.
    /// Call this <em>first</em> in OnRender, before any other geometry.
    /// </summary>
    public unsafe void Draw(ICamera camera)
    {
        if (_ctx is null || !_ctx.FrameStarted) return;

        var vk       = _ctx.Vk;
        var cb       = _ctx.CurrentCommandBuffer;
        var extent   = _ctx.SwapchainExtent;
        var imageIdx = _ctx.CurrentImageIndex;

        // Build the UBO: inverse projection + rotation-only inverse view.
        Matrix4x4.Invert(camera.ProjectionMatrix, out var invProj);
        var viewRot = camera.ViewMatrix;
        viewRot.M41 = viewRot.M42 = viewRot.M43 = 0f; // strip translation
        Matrix4x4.Invert(viewRot, out var invViewRot);

        *(SkyUbo*)(void*)_uboMapped[imageIdx] = new SkyUbo
        {
            InvProj           = invProj,
            InvViewRot        = invViewRot,
            SkyColor          = new Vector4(SkyColor, 1f),
            HorizonColor      = new Vector4(HorizonColor, 1f),
            GroundColor       = new Vector4(GroundColor, 1f),
            SunDirection      = new Vector4(Vector3.Normalize(SunDirection), 0f),
            SunColorIntensity = new Vector4(SunColor, SunIntensity),
            SunSize           = MathF.Cos(float.DegreesToRadians(SunAngularRadius)),
            HorizonSharpness  = HorizonSharpness,
        };

        // Use the same Y-flip viewport as the rest of the engine.
        var viewport = new Viewport
        {
            X = 0,
            Y = extent.Height,
            Width = extent.Width,
            Height = -(float)extent.Height,
            MinDepth = 0f,
            MaxDepth = 1f,
        };
        vk.CmdSetViewport(cb, 0, 1, &viewport);

        var scissor = new Rect2D { Offset = default, Extent = extent };
        vk.CmdSetScissor(cb, 0, 1, &scissor);

        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _pipeline);

        if (HasTexture)
        {
            var sets = stackalloc[] { _uboSets[imageIdx], _texSet };
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 0, 2, sets, 0, null);
        }
        else
        {
            var set = _uboSets[imageIdx];
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, &set, 0, null);
        }

        // Three vertices expand into the fullscreen triangle in the vertex shader.
        vk.CmdDraw(cb, 3, 1, 0, 0);
    }

    // ─── Dispose ──────────────────────────────────────────────────────────────

    public unsafe void Dispose()
    {
        if (_ctx is null) return;
        var vk     = _ctx.Vk;
        var device = _ctx.Device;
        vk.DeviceWaitIdle(device);

        for (int i = 0; i < _uboBuffers.Length; i++)
        {
            vk.UnmapMemory(device, _uboMemory[i]);
            vk.DestroyBuffer(device, _uboBuffers[i], null);
            vk.FreeMemory(device, _uboMemory[i], null);
        }
        vk.DestroyDescriptorPool(device, _descPool, null);
        vk.DestroyDescriptorSetLayout(device, _uboSetLayout, null);

        if (HasTexture)
        {
            vk.DestroyDescriptorSetLayout(device, _texSetLayout, null);
            vk.DestroyImageView(device, _texView, null);
            vk.DestroyImage(device, _texImage, null);
            vk.FreeMemory(device, _texMemory, null);
            vk.DestroySampler(device, _texSampler, null);
        }

        vk.DestroyPipeline(device, _pipeline, null);
        vk.DestroyPipelineLayout(device, _pipelineLayout, null);
    }

    // ─── Image loading ────────────────────────────────────────────────────────

    private unsafe void LoadPanoramic(IVulkanContext ctx, string path)
    {
        var img = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
        CreateTexture2d(ctx, (uint)img.Width, (uint)img.Height, img.Data);
    }

    private unsafe void LoadCubemap(IVulkanContext ctx, string[] facePaths)
    {
        if (facePaths.Length != 6)
            throw new ArgumentException("Cubemap requires exactly 6 face paths (+X, -X, +Y, -Y, +Z, -Z).", nameof(facePaths));

        var faces = facePaths
            .Select(p => ImageResult.FromMemory(File.ReadAllBytes(p), ColorComponents.RedGreenBlueAlpha))
            .ToArray();
        CreateTextureCube(ctx, (uint)faces[0].Width, (uint)faces[0].Height,
            faces.Select(f => f.Data).ToArray());
    }

    private unsafe void CreateTexture2d(IVulkanContext ctx, uint w, uint h, byte[] pixels)
    {
        var vk     = ctx.Vk;
        var device = ctx.Device;
        ulong size = w * h * 4;

        CreateBuffer(ctx, size,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var staging, out var stagingMem);

        void* mapped;
        vk.MapMemory(device, stagingMem, 0, size, 0, &mapped);
        fixed (byte* p = pixels) Unsafe.CopyBlock(mapped, p, (uint)size);
        vk.UnmapMemory(device, stagingMem);

        CreateImage(ctx, w, h, 1,
            ImageCreateFlags.None,
            Format.R8G8B8A8Srgb,
            out _texImage, out _texMemory);

        TransitionLayout(ctx, _texImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 1);
        CopyBufferToImage(ctx, staging, _texImage, w, h, 1);
        TransitionLayout(ctx, _texImage, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, 1);

        vk.DestroyBuffer(device, staging, null);
        vk.FreeMemory(device, stagingMem, null);

        var viewInfo = new ImageViewCreateInfo
        {
            SType    = StructureType.ImageViewCreateInfo,
            Image    = _texImage,
            ViewType = ImageViewType.Type2D,
            Format   = Format.R8G8B8A8Srgb,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };
        if (vk.CreateImageView(device, in viewInfo, null, out _texView) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to create panoramic image view!");

        CreateSampler(ctx);
    }

    private unsafe void CreateTextureCube(IVulkanContext ctx, uint faceW, uint faceH, byte[][] faceData)
    {
        var vk     = ctx.Vk;
        var device = ctx.Device;
        ulong faceSize  = faceW * faceH * 4;
        ulong totalSize = faceSize * 6;

        CreateBuffer(ctx, totalSize,
            BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out var staging, out var stagingMem);

        void* mapped;
        vk.MapMemory(device, stagingMem, 0, totalSize, 0, &mapped);
        for (int i = 0; i < 6; i++)
            fixed (byte* p = faceData[i])
                Unsafe.CopyBlock((byte*)mapped + i * (long)faceSize, p, (uint)faceSize);
        vk.UnmapMemory(device, stagingMem);

        CreateImage(ctx, faceW, faceH, 6,
            ImageCreateFlags.CreateCubeCompatibleBit,
            Format.R8G8B8A8Srgb,
            out _texImage, out _texMemory);

        TransitionLayout(ctx, _texImage, ImageLayout.Undefined, ImageLayout.TransferDstOptimal, 6);
        CopyBufferToImage(ctx, staging, _texImage, faceW, faceH, 6);
        TransitionLayout(ctx, _texImage, ImageLayout.TransferDstOptimal, ImageLayout.ShaderReadOnlyOptimal, 6);

        vk.DestroyBuffer(device, staging, null);
        vk.FreeMemory(device, stagingMem, null);

        var viewInfo = new ImageViewCreateInfo
        {
            SType    = StructureType.ImageViewCreateInfo,
            Image    = _texImage,
            ViewType = ImageViewType.TypeCube,
            Format   = Format.R8G8B8A8Srgb,
            SubresourceRange = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 6,
            },
        };
        if (vk.CreateImageView(device, in viewInfo, null, out _texView) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to create cubemap image view!");

        CreateSampler(ctx);
    }

    private static unsafe void CreateImage(IVulkanContext ctx, uint w, uint h, uint arrayLayers,
        ImageCreateFlags flags, Format format,
        out VkImage image, out DeviceMemory memory)
    {
        var vk     = ctx.Vk;
        var device = ctx.Device;

        var info = new ImageCreateInfo
        {
            SType         = StructureType.ImageCreateInfo,
            Flags         = flags,
            ImageType     = ImageType.Type2D,
            Format        = format,
            Extent        = new Extent3D(w, h, 1),
            MipLevels     = 1,
            ArrayLayers   = arrayLayers,
            Samples       = SampleCountFlags.Count1Bit,
            Tiling        = ImageTiling.Optimal,
            Usage         = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            SharingMode   = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        if (vk.CreateImage(device, in info, null, out image) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to create image!");

        vk.GetImageMemoryRequirements(device, image, out var memReq);
        var ai = new MemoryAllocateInfo
        {
            SType           = StructureType.MemoryAllocateInfo,
            AllocationSize  = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        if (vk.AllocateMemory(device, in ai, null, out memory) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to allocate image memory!");

        vk.BindImageMemory(device, image, memory, 0);
    }

    private unsafe void CreateSampler(IVulkanContext ctx)
    {
        var info = new SamplerCreateInfo
        {
            SType        = StructureType.SamplerCreateInfo,
            MagFilter    = Filter.Linear,
            MinFilter    = Filter.Linear,
            MipmapMode   = SamplerMipmapMode.Linear,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
        };
        if (ctx.Vk.CreateSampler(ctx.Device, in info, null, out _texSampler) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to create sampler!");
    }

    // ─── Pipeline creation ────────────────────────────────────────────────────

    private unsafe void CreatePipeline(IVulkanContext ctx)
    {
        var vk         = ctx.Vk;
        var device     = ctx.Device;
        var imageCount = ctx.SwapchainImageCount;

        // UBO descriptor set layout (set = 0)
        var uboBinding = new DescriptorSetLayoutBinding
        {
            Binding         = 0,
            DescriptorType  = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags      = ShaderStageFlags.FragmentBit,
        };
        var uboLayoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType        = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings    = &uboBinding,
        };
        if (vk.CreateDescriptorSetLayout(device, in uboLayoutInfo, null, out _uboSetLayout) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to create UBO descriptor set layout!");

        // Texture descriptor set layout (set = 1, Panoramic / Cubemap only)
        if (HasTexture)
        {
            var texBinding = new DescriptorSetLayoutBinding
            {
                Binding         = 0,
                DescriptorType  = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                StageFlags      = ShaderStageFlags.FragmentBit,
            };
            var texLayoutInfo = new DescriptorSetLayoutCreateInfo
            {
                SType        = StructureType.DescriptorSetLayoutCreateInfo,
                BindingCount = 1,
                PBindings    = &texBinding,
            };
            if (vk.CreateDescriptorSetLayout(device, in texLayoutInfo, null, out _texSetLayout) != Result.Success)
                throw new VulkanException("[SkyEnvironment] Failed to create texture descriptor set layout!");
        }

        // UBO buffers (one per swapchain image)
        _uboBuffers = new VkBuffer[imageCount];
        _uboMemory  = new DeviceMemory[imageCount];
        _uboMapped  = new nint[imageCount];
        for (int i = 0; i < imageCount; i++)
        {
            CreateBuffer(ctx, (ulong)sizeof(SkyUbo),
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _uboBuffers[i], out _uboMemory[i]);
            void* ptr;
            vk.MapMemory(device, _uboMemory[i], 0, (ulong)sizeof(SkyUbo), 0, &ptr);
            _uboMapped[i] = (nint)ptr;
        }

        // Descriptor pool
        if (HasTexture)
        {
            var ps = stackalloc[]
            {
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer,       DescriptorCount = imageCount },
                new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 },
            };
            var pi = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 2,
                PPoolSizes = ps,
                MaxSets = imageCount + 1,
            };
            if (vk.CreateDescriptorPool(device, in pi, null, out _descPool) != Result.Success)
                throw new VulkanException("[SkyEnvironment] Failed to create descriptor pool!");
        }
        else
        {
            var ps = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = imageCount };
            var pi = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                PoolSizeCount = 1,
                PPoolSizes = &ps,
                MaxSets = imageCount,
            };
            if (vk.CreateDescriptorPool(device, in pi, null, out _descPool) != Result.Success)
                throw new VulkanException("[SkyEnvironment] Failed to create descriptor pool!");
        }

        // Allocate and write UBO descriptor sets
        var uboLayouts = stackalloc DescriptorSetLayout[(int)imageCount];
        for (int i = 0; i < imageCount; i++) uboLayouts[i] = _uboSetLayout;
        var uboAlloc = new DescriptorSetAllocateInfo
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool     = _descPool,
            DescriptorSetCount = imageCount,
            PSetLayouts        = uboLayouts,
        };
        _uboSets = new DescriptorSet[imageCount];
        fixed (DescriptorSet* p = _uboSets)
            if (vk.AllocateDescriptorSets(device, in uboAlloc, p) != Result.Success)
                throw new VulkanException("[SkyEnvironment] Failed to allocate UBO descriptor sets!");

        for (int i = 0; i < imageCount; i++)
        {
            var bufInfo = new DescriptorBufferInfo
            { Buffer = _uboBuffers[i], Offset = 0, Range = (ulong)sizeof(SkyUbo) };
            var write = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _uboSets[i],
                DstBinding      = 0,
                DescriptorType  = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo     = &bufInfo,
            };
            vk.UpdateDescriptorSets(device, 1, &write, 0, null);
        }

        // Allocate and write texture descriptor set (shared)
        if (HasTexture)
        {
            var texLayout = _texSetLayout;
            var texAlloc = new DescriptorSetAllocateInfo
            {
                SType              = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool     = _descPool,
                DescriptorSetCount = 1,
                PSetLayouts        = &texLayout,
            };
            if (vk.AllocateDescriptorSets(device, in texAlloc, out _texSet) != Result.Success)
                throw new VulkanException("[SkyEnvironment] Failed to allocate texture descriptor set!");

            var imgInfo = new DescriptorImageInfo
            {
                Sampler     = _texSampler,
                ImageView   = _texView,
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var texWrite = new WriteDescriptorSet
            {
                SType           = StructureType.WriteDescriptorSet,
                DstSet          = _texSet,
                DstBinding      = 0,
                DescriptorType  = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1,
                PImageInfo      = &imgInfo,
            };
            vk.UpdateDescriptorSets(device, 1, &texWrite, 0, null);
        }

        // Shaders
        var vertCode = File.ReadAllBytes("Content/Shaders/Sky/Sky.vk.vert.spv");
        var fragCode = Type switch
        {
            SkyEnvironmentType.Procedural => File.ReadAllBytes("Content/Shaders/Sky/Sky.Procedural.vk.frag.spv"),
            SkyEnvironmentType.Panoramic  => File.ReadAllBytes("Content/Shaders/Sky/Sky.Panoramic.vk.frag.spv"),
            SkyEnvironmentType.Cubemap    => File.ReadAllBytes("Content/Shaders/Sky/Sky.Cubemap.vk.frag.spv"),
            _ => throw new InvalidOperationException(),
        };

        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        var vertModule = CreateShaderModule(ctx, vertCode);
        var fragModule = CreateShaderModule(ctx, fragCode);

        var stages = stackalloc[]
        {
            new PipelineShaderStageCreateInfo
            {
                SType  = StructureType.PipelineShaderStageCreateInfo,
                Stage  = ShaderStageFlags.VertexBit,
                Module = vertModule,
                PName  = entryPoint,
            },
            new PipelineShaderStageCreateInfo
            {
                SType  = StructureType.PipelineShaderStageCreateInfo,
                Stage  = ShaderStageFlags.FragmentBit,
                Module = fragModule,
                PName  = entryPoint,
            },
        };

        // No vertex input — positions are baked into the vertex shader.
        var vertexInput = new PipelineVertexInputStateCreateInfo
        { SType = StructureType.PipelineVertexInputStateCreateInfo };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType    = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType       = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            LineWidth   = 1f,
            CullMode    = CullModeFlags.None,
            FrontFace   = FrontFace.CounterClockwise,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType                = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var blendAttachment = new PipelineColorBlendAttachmentState
        {
            ColorWriteMask =
                ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                ColorComponentFlags.BBit | ColorComponentFlags.ABit,
            BlendEnable = false,
        };
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType           = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments    = &blendAttachment,
        };
        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState  = new PipelineDynamicStateCreateInfo
        {
            SType             = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates    = dynamicStates,
        };

        // Pipeline layout
        DescriptorSetLayout* setLayouts;
        uint setLayoutCount;
        if (HasTexture)
        {
            var ls = stackalloc[] { _uboSetLayout, _texSetLayout };
            setLayouts    = ls;
            setLayoutCount = 2;
        }
        else
        {
            var ls = stackalloc[] { _uboSetLayout };
            setLayouts    = ls;
            setLayoutCount = 1;
        }

        var pipelineLayoutInfo = new PipelineLayoutCreateInfo
        {
            SType          = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = setLayoutCount,
            PSetLayouts    = setLayouts,
        };
        if (vk.CreatePipelineLayout(device, in pipelineLayoutInfo, null, out _pipelineLayout) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to create pipeline layout!");

        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType            = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable  = false,
            DepthWriteEnable = false,
        };

        var pipelineInfo = new GraphicsPipelineCreateInfo
        {
            SType               = StructureType.GraphicsPipelineCreateInfo,
            StageCount          = 2,
            PStages             = stages,
            PVertexInputState   = &vertexInput,
            PInputAssemblyState = &inputAssembly,
            PViewportState      = &viewportState,
            PRasterizationState = &rasterizer,
            PMultisampleState   = &multisampling,
            PDepthStencilState  = &depthStencil,
            PColorBlendState    = &colorBlend,
            PDynamicState       = &dynamicState,
            Layout              = _pipelineLayout,
            RenderPass          = ctx.RenderPass,
            Subpass             = 0,
        };
        if (vk.CreateGraphicsPipelines(device, default, 1, in pipelineInfo, null, out _pipeline) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to create sky pipeline!");

        SilkMarshal.Free((nint)entryPoint);
        vk.DestroyShaderModule(device, vertModule, null);
        vk.DestroyShaderModule(device, fragModule, null);
    }

    // ─── Vulkan helpers ───────────────────────────────────────────────────────

    private static unsafe void TransitionLayout(IVulkanContext ctx, VkImage image,
        ImageLayout oldLayout, ImageLayout newLayout, uint layerCount)
    {
        var cb = BeginOneTimeCmd(ctx);

        var barrier = new ImageMemoryBarrier
        {
            SType               = StructureType.ImageMemoryBarrier,
            OldLayout           = oldLayout,
            NewLayout           = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image               = image,
            SubresourceRange    = new ImageSubresourceRange
            {
                AspectMask = ImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = layerCount,
            },
        };

        PipelineStageFlags srcStage, dstStage;
        if (oldLayout == ImageLayout.Undefined && newLayout == ImageLayout.TransferDstOptimal)
        {
            barrier.SrcAccessMask = 0;
            barrier.DstAccessMask = AccessFlags.TransferWriteBit;
            srcStage = PipelineStageFlags.TopOfPipeBit;
            dstStage = PipelineStageFlags.TransferBit;
        }
        else
        {
            barrier.SrcAccessMask = AccessFlags.TransferWriteBit;
            barrier.DstAccessMask = AccessFlags.ShaderReadBit;
            srcStage = PipelineStageFlags.TransferBit;
            dstStage = PipelineStageFlags.FragmentShaderBit;
        }

        ctx.Vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);
        EndOneTimeCmd(ctx, cb);
    }

    private unsafe void CopyBufferToImage(IVulkanContext ctx,
        VkBuffer buffer, VkImage image, uint w, uint h, uint layerCount)
    {
        var cb = BeginOneTimeCmd(ctx);
        ulong faceSize = w * h * 4;

        var regions = new BufferImageCopy[layerCount];
        for (uint i = 0; i < layerCount; i++)
        {
            regions[i] = new BufferImageCopy
            {
                BufferOffset     = i * faceSize,
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask     = ImageAspectFlags.ColorBit,
                    MipLevel       = 0,
                    BaseArrayLayer = i,
                    LayerCount     = 1,
                },
                ImageExtent = new Extent3D(w, h, 1),
            };
        }

        fixed (BufferImageCopy* p = regions)
            ctx.Vk.CmdCopyBufferToImage(cb, buffer, image, ImageLayout.TransferDstOptimal, layerCount, p);

        EndOneTimeCmd(ctx, cb);
    }

    private static unsafe CommandBuffer BeginOneTimeCmd(IVulkanContext ctx)
    {
        var vk = ctx.Vk;
        var alloc = new CommandBufferAllocateInfo
        {
            SType              = StructureType.CommandBufferAllocateInfo,
            Level              = CommandBufferLevel.Primary,
            CommandPool        = ctx.CommandPool,
            CommandBufferCount = 1,
        };
        CommandBuffer cb;
        vk.AllocateCommandBuffers(ctx.Device, in alloc, &cb);
        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        vk.BeginCommandBuffer(cb, in beginInfo);
        return cb;
    }

    private static unsafe void EndOneTimeCmd(IVulkanContext ctx, CommandBuffer cb)
    {
        var vk = ctx.Vk;
        vk.EndCommandBuffer(cb);
        var submit = new SubmitInfo
        {
            SType              = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers    = &cb,
        };
        vk.QueueSubmit(ctx.GraphicsQueue, 1, in submit, default);
        vk.QueueWaitIdle(ctx.GraphicsQueue);
        vk.FreeCommandBuffers(ctx.Device, ctx.CommandPool, 1, &cb);
    }

    private static unsafe void CreateBuffer(IVulkanContext ctx, ulong size,
        BufferUsageFlags usage, MemoryPropertyFlags props,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        var vk = ctx.Vk;
        var bi = new BufferCreateInfo
        {
            SType       = StructureType.BufferCreateInfo,
            Size        = size,
            Usage       = usage,
            SharingMode = SharingMode.Exclusive,
        };
        if (vk.CreateBuffer(ctx.Device, in bi, null, out buffer) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to create buffer!");

        vk.GetBufferMemoryRequirements(ctx.Device, buffer, out var memReq);
        var ai = new MemoryAllocateInfo
        {
            SType           = StructureType.MemoryAllocateInfo,
            AllocationSize  = memReq.Size,
            MemoryTypeIndex = FindMemoryType(ctx, memReq.MemoryTypeBits, props),
        };
        if (vk.AllocateMemory(ctx.Device, in ai, null, out memory) != Result.Success)
            throw new VulkanException("[SkyEnvironment] Failed to allocate memory!");

        vk.BindBufferMemory(ctx.Device, buffer, memory, 0);
    }

    private static uint FindMemoryType(IVulkanContext ctx, uint typeBits, MemoryPropertyFlags props)
    {
        ctx.Vk.GetPhysicalDeviceMemoryProperties(ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
                return i;
        throw new VulkanException("[SkyEnvironment] No suitable memory type found!");
    }

    private unsafe ShaderModule CreateShaderModule(IVulkanContext ctx, byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var ci = new ShaderModuleCreateInfo
            {
                SType    = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode    = (uint*)ptr,
            };
            if (ctx.Vk.CreateShaderModule(ctx.Device, in ci, null, out var m) != Result.Success)
                throw new VulkanException("[SkyEnvironment] Failed to create shader module!");
            return m;
        }
    }
}
