using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer  = Silk.NET.Vulkan.Buffer;
using VkSampler = Silk.NET.Vulkan.Sampler;

namespace MainframeEngine;

/// <summary>
/// Manages shadow maps for all three light types (directional, spot, point).
/// Call RenderShadows() from IGame.OnShadowPass(), then pass this object to shapes'
/// Draw() methods so they can bind the shadow descriptor set.
/// </summary>
public sealed unsafe class ShadowSystem : IDisposable
{
    // ── Limits ────────────────────────────────────────────────────────────────

    public const int MaxShadowDir   = LightEnvironment.MaxDirectional; // 4
    public const int MaxShadowSpot  = LightEnvironment.MaxSpot;        // 8
    public const int MaxShadowPoint = 4;   // cube maps are expensive

    private const int DirSize   = 2048;
    private const int SpotSize  = 1024;
    private const int PointSize = 512;

    // ── Shadow matrices UBO layout (must match Shapes.vk.frag) ───────────────
    // mat4[4] dir + mat4[8] spot = (4+8)*64 = 768 bytes
    private const int ShadowMatricesUboSize = (MaxShadowDir + MaxShadowSpot) * 64;

    // ── Per-map structs ───────────────────────────────────────────────────────

    private struct Map2D
    {
        public Image       Image;
        public DeviceMemory Memory;
        public ImageView   View;        // full 2D view (for sampling)
        public Framebuffer Framebuffer;
    }

    private struct MapCube
    {
        public Image        Image;
        public DeviceMemory Memory;
        public ImageView    CubeView;                          // full cube view (for sampling)
        public ImageView[]  FaceViews = new ImageView[6];     // one per face (for rendering)
        public Framebuffer[] FaceFramebuffers = new Framebuffer[6];
        public MapCube() { }
    }

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly IVulkanContext _ctx;
    private readonly Format         _depthFormat;

    // Depth-only render pass (shared for all shadow types)
    private RenderPass _shadowRenderPass;

    // Shadow pipelines — one per vertex stride per shadow type
    // Shapes with stride 32 (Box3d) and stride 12 (Quad) are supported.
    private Pipeline       _pipe2D_S32,   _pipe2D_S12;
    private Pipeline       _pipePoint_S32, _pipePoint_S12;
    private PipelineLayout _layout2D;      // push: mat4 model; set0: light VP UBO
    private PipelineLayout _layoutPoint;   // push: mat4 model + vec4 lightPosRange; set0: light VP UBO

    // Light-VP UBO (one buffer, updated before each shadow sub-pass)
    private VkBuffer       _vpBuffer;
    private DeviceMemory   _vpMemory;
    private nint           _vpMapped;
    private DescriptorPool       _vpPool;
    private DescriptorSetLayout  _vpSetLayout;
    private DescriptorSet        _vpSet;

    // Shadow maps
    private readonly Map2D[]   _dirMaps  = new Map2D[MaxShadowDir];
    private readonly Map2D[]   _spotMaps = new Map2D[MaxShadowSpot];
    private readonly MapCube[] _ptMaps   = new MapCube[MaxShadowPoint];

    // Samplers
    private VkSampler _sampler2DShadow; // comparison sampler for dir/spot
    private VkSampler _samplerCube;     // plain sampler for point

    // Main-pass descriptor set (set=2 in Shapes.vk.frag)
    private DescriptorPool      _mainPool;
    public  DescriptorSetLayout MainDescSetLayout { get; private set; }
    private DescriptorSet[]     _mainSets = null!; // one per swapchain image

    // Shadow matrices UBO (per swapchain image, like Box3d VP UBO)
    private VkBuffer[]      _matBuffers = null!;
    private DeviceMemory[]  _matMemory  = null!;
    private nint[]          _matMapped  = null!;

    // CPU-side matrices updated by RenderShadows each frame
    private readonly Matrix4x4[] _dirMats  = new Matrix4x4[MaxShadowDir];
    private readonly Matrix4x4[] _spotMats = new Matrix4x4[MaxShadowSpot];

    // ── Constructor ───────────────────────────────────────────────────────────

    public ShadowSystem(IVulkanContext ctx)
    {
        _ctx = ctx;
        _depthFormat = FindDepthFormat();

        CreateShadowRenderPass();
        CreateVpUboResources();
        CreateShadowPipelines();
        CreateShadowMaps();
        CreateSamplers();
        CreateMainDescriptorResources();
        InitializeShadowMapLayouts();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns the shadow descriptor set for the current swapchain image.</summary>
    public DescriptorSet GetMainSet() => _mainSets[_ctx.CurrentImageIndex];

    /// <summary>
    /// Renders shadow maps for all active lights, then updates the shadow matrices UBO.
    /// Call from IGame.OnShadowPass (before the main render pass).
    /// draw2D: bind VB, push model matrix via layout2D, call CmdDraw.
    /// drawPoint: bind VB, push model+lightPosRange via layoutPoint, call CmdDraw.
    /// </summary>
    public void RenderShadows(
        LightEnvironment lights,
        Action<CommandBuffer, Pipeline, Pipeline, PipelineLayout> draw2D,
        Action<CommandBuffer, Pipeline, Pipeline, PipelineLayout, Vector3, float> drawPoint)
    {
        var vk = _ctx.Vk;
        var cb = _ctx.CurrentCommandBuffer;

        int numDir   = Math.Min(lights.DirectionalLights.Count, MaxShadowDir);
        int numSpot  = Math.Min(lights.SpotLights.Count, MaxShadowSpot);
        int numPoint = Math.Min(lights.PointLights.Count, MaxShadowPoint);

        // Transition all shadow maps to DepthStencilAttachmentOptimal for writing
        TransitionAll(cb, numDir, numSpot, numPoint,
            ImageLayout.DepthStencilReadOnlyOptimal, ImageLayout.DepthStencilAttachmentOptimal);

        // ── Directional shadow maps ──────────────────────────────────────────
        for (int i = 0; i < numDir; i++)
        {
            var light = lights.DirectionalLights[i];
            _dirMats[i] = CalcDirLightMatrix(light);
            *(Matrix4x4*)(void*)_vpMapped = _dirMats[i];

            RenderShadowPass2D(cb, _dirMaps[i].Framebuffer, DirSize, DirSize, _layout2D,
                (c, p32, p12, lay) => draw2D(c, p32, p12, lay));
        }

        // ── Spot shadow maps ─────────────────────────────────────────────────
        for (int i = 0; i < numSpot; i++)
        {
            var light = lights.SpotLights[i];
            _spotMats[i] = CalcSpotLightMatrix(light);
            *(Matrix4x4*)(void*)_vpMapped = _spotMats[i];

            RenderShadowPass2D(cb, _spotMaps[i].Framebuffer, SpotSize, SpotSize, _layout2D,
                (c, p32, p12, lay) => draw2D(c, p32, p12, lay));
        }

        // ── Point shadow cube maps ───────────────────────────────────────────
        var cubeFaces = new[] {
            (Vector3.UnitX,     -Vector3.UnitY),  // +X
            (-Vector3.UnitX,    -Vector3.UnitY),  // -X
            (Vector3.UnitY,      Vector3.UnitZ),   // +Y
            (-Vector3.UnitY,    -Vector3.UnitZ),   // -Y
            (Vector3.UnitZ,     -Vector3.UnitY),   // +Z
            (-Vector3.UnitZ,    -Vector3.UnitY),   // -Z
        };

        for (int i = 0; i < numPoint; i++)
        {
            var light = lights.PointLights[i];
            for (int face = 0; face < 6; face++)
            {
                var (dir, up) = cubeFaces[face];
                var faceVP = CalcPointFaceMatrix(light.Position, dir, up, light.Range);
                *(Matrix4x4*)(void*)_vpMapped = faceVP;

                RenderShadowPass2D(cb, _ptMaps[i].FaceFramebuffers[face], PointSize, PointSize, _layoutPoint,
                    (c, p32, p12, lay) => drawPoint(c, p32, p12, lay, light.Position, light.Range));
            }
        }

        // Transition shadow maps to DepthStencilReadOnlyOptimal for sampling in main pass
        TransitionAll(cb, numDir, numSpot, numPoint,
            ImageLayout.DepthStencilAttachmentOptimal, ImageLayout.DepthStencilReadOnlyOptimal);

        // Upload light-space matrices to per-image UBO
        var imgIdx = _ctx.CurrentImageIndex;
        var dst    = (float*)(void*)_matMapped[imgIdx];
        for (int i = 0; i < MaxShadowDir;  i++) Unsafe.Copy(dst + i * 16, ref _dirMats[i]);
        dst += MaxShadowDir * 16;
        for (int i = 0; i < MaxShadowSpot; i++) Unsafe.Copy(dst + i * 16, ref _spotMats[i]);
    }

    // ── Shadow pass helpers ───────────────────────────────────────────────────

    private void RenderShadowPass2D(
        CommandBuffer cb, Framebuffer fb, uint width, uint height,
        PipelineLayout bindLayout,
        Action<CommandBuffer, Pipeline, Pipeline, PipelineLayout> draw)
    {
        var vk = _ctx.Vk;

        var clearVal = new ClearValue { DepthStencil = new() { Depth = 1.0f, Stencil = 0 } };
        var rpInfo   = new RenderPassBeginInfo
        {
            SType           = StructureType.RenderPassBeginInfo,
            RenderPass      = _shadowRenderPass,
            Framebuffer     = fb,
            RenderArea      = new Rect2D { Extent = new Extent2D(width, height) },
            ClearValueCount = 1,
            PClearValues    = &clearVal,
        };
        vk.CmdBeginRenderPass(cb, &rpInfo, SubpassContents.Inline);

        // Standard viewport (no Y-flip) — shadow maps are depth-only; the UV lookup
        // in the fragment shader uses standard NDC→[0,1] mapping so the viewport must match.
        var vp = new Viewport
        {
            X = 0, Y = 0, Width = width, Height = height,
            MinDepth = 0f, MaxDepth = 1f,
        };
        vk.CmdSetViewport(cb, 0, 1, &vp);
        var sc = new Rect2D { Extent = new Extent2D(width, height) };
        vk.CmdSetScissor(cb, 0, 1, &sc);

        var vpSet = _vpSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, bindLayout, 0, 1, &vpSet, 0, null);

        draw(cb, _pipe2D_S32, _pipe2D_S12, bindLayout);

        vk.CmdEndRenderPass(cb);
    }

    // ── Light matrix computation ──────────────────────────────────────────────

    private static Matrix4x4 CalcDirLightMatrix(DirectionalLight light)
    {
        var lightDir = Vector3.Normalize(light.Direction);
        var lightPos = -lightDir * 20f; // pull back 20 units from scene centre
        var view     = Matrix4x4.CreateLookAt(lightPos, lightPos + lightDir, Vector3.UnitY);
        var proj     = Matrix4x4.CreateOrthographicOffCenter(-20, 20, -20, 20, 0.1f, 50f);
        return view * proj;
    }

    private static Matrix4x4 CalcSpotLightMatrix(SpotLight light)
    {
        var fovY = float.DegreesToRadians(light.OuterConeAngle * 2f);
        var view = Matrix4x4.CreateLookAt(light.Position, light.Position + light.Direction, ChooseUp(light.Direction));
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fovY, 1f, 0.1f, light.Range);
        return view * proj;
    }

    private static Matrix4x4 CalcPointFaceMatrix(Vector3 pos, Vector3 dir, Vector3 up, float range)
    {
        var view = Matrix4x4.CreateLookAt(pos, pos + dir, up);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(90f), 1f, 0.05f, range);
        return view * proj;
    }

    private static Vector3 ChooseUp(Vector3 dir)
    {
        // Avoid gimbal lock when direction is nearly parallel to world-up
        return MathF.Abs(Vector3.Dot(dir, Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;
    }

    // ── Pipeline barrier helpers ──────────────────────────────────────────────

    private void TransitionAll(CommandBuffer cb, int numDir, int numSpot, int numPoint,
        ImageLayout oldLayout, ImageLayout newLayout)
    {
        var vk = _ctx.Vk;

        bool toWrite = newLayout == ImageLayout.DepthStencilAttachmentOptimal;
        var srcAccess = toWrite
            ? AccessFlags.ShaderReadBit
            : AccessFlags.DepthStencilAttachmentWriteBit;
        var dstAccess = toWrite
            ? AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit
            : AccessFlags.ShaderReadBit;
        var srcStage = toWrite
            ? PipelineStageFlags.FragmentShaderBit
            : PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        var dstStage = toWrite
            ? PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit
            : PipelineStageFlags.FragmentShaderBit;

        // Shadow maps are always cleared at render pass start, so use Undefined as old layout
        // when transitioning to write — this is valid and avoids first-frame layout tracking issues.
        var barrierOldLayout = toWrite ? ImageLayout.Undefined : oldLayout;

        void Barrier(Image img, uint layers = 1)
        {
            var b = new ImageMemoryBarrier
            {
                SType               = StructureType.ImageMemoryBarrier,
                SrcAccessMask       = srcAccess,
                DstAccessMask       = dstAccess,
                OldLayout           = barrierOldLayout,
                NewLayout           = newLayout,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image               = img,
                SubresourceRange    = new ImageSubresourceRange
                {
                    AspectMask     = ImageAspectFlags.DepthBit,
                    BaseMipLevel   = 0,
                    LevelCount     = 1,
                    BaseArrayLayer = 0,
                    LayerCount     = layers,
                },
            };
            vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, 1, &b);
        }

        for (int i = 0; i < numDir;   i++) Barrier(_dirMaps[i].Image);
        for (int i = 0; i < numSpot;  i++) Barrier(_spotMaps[i].Image);
        for (int i = 0; i < numPoint; i++) Barrier(_ptMaps[i].Image, 6);
    }

    // ── Resource creation ─────────────────────────────────────────────────────

    private void CreateShadowRenderPass()
    {
        var vk = _ctx.Vk;
        var depth = new AttachmentDescription
        {
            Format         = _depthFormat,
            Samples        = SampleCountFlags.Count1Bit,
            LoadOp         = AttachmentLoadOp.Clear,
            StoreOp        = AttachmentStoreOp.Store,
            StencilLoadOp  = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout  = ImageLayout.Undefined,
            FinalLayout    = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var depthRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var subpass  = new SubpassDescription
        {
            PipelineBindPoint       = PipelineBindPoint.Graphics,
            PDepthStencilAttachment = &depthRef,
        };
        var dep = new SubpassDependency
        {
            SrcSubpass    = Vk.SubpassExternal,
            DstSubpass    = 0,
            SrcStageMask  = PipelineStageFlags.FragmentShaderBit,
            DstStageMask  = PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
        };
        var info = new RenderPassCreateInfo
        {
            SType           = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments    = &depth,
            SubpassCount    = 1,
            PSubpasses      = &subpass,
            DependencyCount = 1,
            PDependencies   = &dep,
        };
        if (vk.CreateRenderPass(_ctx.Device, info, null, out _shadowRenderPass) != Result.Success)
            throw new Exception("[Shadow] Failed to create shadow render pass!");
    }

    private void CreateVpUboResources()
    {
        var vk     = _ctx.Vk;
        var device = _ctx.Device;

        CreateBuffer((ulong)sizeof(Matrix4x4),
            BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _vpBuffer, out _vpMemory);

        void* ptr;
        vk.MapMemory(device, _vpMemory, 0, (ulong)sizeof(Matrix4x4), 0, &ptr);
        _vpMapped = (nint)ptr;

        // Descriptor set layout: binding 0 = UBO (vertex stage)
        var bind = new DescriptorSetLayoutBinding
        {
            Binding         = 0,
            DescriptorType  = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags      = ShaderStageFlags.VertexBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType        = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings    = &bind,
        };
        vk.CreateDescriptorSetLayout(device, layoutInfo, null, out _vpSetLayout);

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 1 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, MaxSets = 1,
            PoolSizeCount = 1, PPoolSizes = &poolSize,
        };
        vk.CreateDescriptorPool(device, poolInfo, null, out _vpPool);

        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool     = _vpPool,
            DescriptorSetCount = 1,
            PSetLayouts        = (DescriptorSetLayout*)Unsafe.AsPointer(ref _vpSetLayout),
        };
        vk.AllocateDescriptorSets(device, allocInfo, out _vpSet);

        var bufInfo = new DescriptorBufferInfo { Buffer = _vpBuffer, Offset = 0, Range = (ulong)sizeof(Matrix4x4) };
        var write   = new WriteDescriptorSet
        {
            SType           = StructureType.WriteDescriptorSet,
            DstSet          = _vpSet,
            DstBinding      = 0,
            DescriptorType  = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            PBufferInfo     = &bufInfo,
        };
        vk.UpdateDescriptorSets(device, 1, &write, 0, null);
    }

    private void CreateShadowPipelines()
    {
        var vk     = _ctx.Vk;
        var device = _ctx.Device;

        // ── Pipeline layouts ──────────────────────────────────────────────────
        var vpLayout = _vpSetLayout;

        var push2D = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = 64 };
        var l2DInfo = new PipelineLayoutCreateInfo
        {
            SType                  = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount         = 1, PSetLayouts = &vpLayout,
            PushConstantRangeCount = 1, PPushConstantRanges = &push2D,
        };
        vk.CreatePipelineLayout(device, l2DInfo, null, out _layout2D);

        var pushPt = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Offset = 0, Size = 80 };
        var lPtInfo = new PipelineLayoutCreateInfo
        {
            SType                  = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount         = 1, PSetLayouts = &vpLayout,
            PushConstantRangeCount = 1, PPushConstantRanges = &pushPt,
        };
        vk.CreatePipelineLayout(device, lPtInfo, null, out _layoutPoint);

        // ── Shared pipeline state ─────────────────────────────────────────────
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        var vert2DCode   = File.ReadAllBytes("Content/Shaders/Shadows/Shadow2D.vk.vert.spv");
        var frag2DCode   = File.ReadAllBytes("Content/Shaders/Shadows/Shadow2D.vk.frag.spv");
        var vertPtCode   = File.ReadAllBytes("Content/Shaders/Shadows/ShadowPoint.vk.vert.spv");
        var fragPtCode   = File.ReadAllBytes("Content/Shaders/Shadows/ShadowPoint.vk.frag.spv");

        var vert2D = CreateShaderModule(vert2DCode);
        var frag2D = CreateShaderModule(frag2DCode);
        var vertPt = CreateShaderModule(vertPtCode);
        var fragPt = CreateShaderModule(fragPtCode);

        var stages2D = stackalloc PipelineShaderStageCreateInfo[]
        {
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit,   Module = vert2D, PName = entryPoint },
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = frag2D, PName = entryPoint },
        };
        var stagesPt = stackalloc PipelineShaderStageCreateInfo[]
        {
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit,   Module = vertPt, PName = entryPoint },
            new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fragPt, PName = entryPoint },
        };

        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType    = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = PrimitiveTopology.TriangleList,
        };
        var viewportState = new PipelineViewportStateCreateInfo
        {
            SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
        };
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType       = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            LineWidth   = 1f,
            CullMode    = CullModeFlags.FrontBit,  // cull front faces (Peter Pan trick)
            FrontFace   = FrontFace.CounterClockwise,
            DepthBiasEnable         = true,
            DepthBiasConstantFactor = 1.25f,
            DepthBiasSlopeFactor    = 1.75f,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType                = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var depthStencil = new PipelineDepthStencilStateCreateInfo
        {
            SType            = StructureType.PipelineDepthStencilStateCreateInfo,
            DepthTestEnable  = true,
            DepthWriteEnable = true,
            DepthCompareOp   = CompareOp.Less,
        };
        var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState  = new PipelineDynamicStateCreateInfo
        {
            SType             = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
            PDynamicStates    = dynamicStates,
        };

        // ── Build pipeline for each stride ────────────────────────────────────
        _pipe2D_S32   = BuildPipeline(stages2D, 2, 32, _layout2D,   _shadowRenderPass, ref inputAssembly, ref viewportState, ref rasterizer, ref multisampling, ref depthStencil, ref dynamicState);
        _pipe2D_S12   = BuildPipeline(stages2D, 2, 12, _layout2D,   _shadowRenderPass, ref inputAssembly, ref viewportState, ref rasterizer, ref multisampling, ref depthStencil, ref dynamicState);
        _pipePoint_S32 = BuildPipeline(stagesPt, 2, 32, _layoutPoint, _shadowRenderPass, ref inputAssembly, ref viewportState, ref rasterizer, ref multisampling, ref depthStencil, ref dynamicState);
        _pipePoint_S12 = BuildPipeline(stagesPt, 2, 12, _layoutPoint, _shadowRenderPass, ref inputAssembly, ref viewportState, ref rasterizer, ref multisampling, ref depthStencil, ref dynamicState);

        SilkMarshal.Free((nint)entryPoint);
        foreach (var m in new[] { vert2D, frag2D, vertPt, fragPt })
            vk.DestroyShaderModule(device, m, null);
    }

    private Pipeline BuildPipeline(
        PipelineShaderStageCreateInfo* stages, uint stageCount,
        uint stride, PipelineLayout layout, RenderPass rp,
        ref PipelineInputAssemblyStateCreateInfo ia,
        ref PipelineViewportStateCreateInfo vps,
        ref PipelineRasterizationStateCreateInfo rast,
        ref PipelineMultisampleStateCreateInfo ms,
        ref PipelineDepthStencilStateCreateInfo ds,
        ref PipelineDynamicStateCreateInfo dyn)
    {
        var bindingDesc = new VertexInputBindingDescription
        {
            Binding = 0, Stride = stride, InputRate = VertexInputRate.Vertex,
        };
        var attrib = new VertexInputAttributeDescription
        {
            Location = 0, Binding = 0,
            Format   = Silk.NET.Vulkan.Format.R32G32B32Sfloat,
            Offset   = 0,
        };
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType                           = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount   = 1,
            PVertexBindingDescriptions      = &bindingDesc,
            VertexAttributeDescriptionCount = 1,
            PVertexAttributeDescriptions    = &attrib,
        };

        fixed (PipelineInputAssemblyStateCreateInfo* pIA  = &ia)
        fixed (PipelineViewportStateCreateInfo*       pVPS = &vps)
        fixed (PipelineRasterizationStateCreateInfo*  pR   = &rast)
        fixed (PipelineMultisampleStateCreateInfo*    pMS  = &ms)
        fixed (PipelineDepthStencilStateCreateInfo*   pDS  = &ds)
        fixed (PipelineDynamicStateCreateInfo*         pDyn = &dyn)
        {
            var pipeInfo = new GraphicsPipelineCreateInfo
            {
                SType               = StructureType.GraphicsPipelineCreateInfo,
                StageCount          = stageCount,
                PStages             = stages,
                PVertexInputState   = &vertexInput,
                PInputAssemblyState = pIA,
                PViewportState      = pVPS,
                PRasterizationState = pR,
                PMultisampleState   = pMS,
                PDepthStencilState  = pDS,
                PDynamicState       = pDyn,
                Layout              = layout,
                RenderPass          = rp,
                Subpass             = 0,
            };
            _ctx.Vk.CreateGraphicsPipelines(_ctx.Device, default, 1, pipeInfo, null, out var pipe);
            return pipe;
        }
    }

    private void CreateShadowMaps()
    {
        for (int i = 0; i < MaxShadowDir;   i++) _dirMaps[i]  = CreateMap2D(DirSize);
        for (int i = 0; i < MaxShadowSpot;  i++) _spotMaps[i] = CreateMap2D(SpotSize);
        for (int i = 0; i < MaxShadowPoint; i++) _ptMaps[i]   = CreateMapCube(PointSize);
    }

    private Map2D CreateMap2D(uint size)
    {
        var vk     = _ctx.Vk;
        var device = _ctx.Device;

        CreateImage(size, size, 1, _depthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            ImageCreateFlags.None,
            out var image, out var memory);

        var viewInfo = new ImageViewCreateInfo
        {
            SType            = StructureType.ImageViewCreateInfo,
            Image            = image,
            ViewType         = ImageViewType.Type2D,
            Format           = _depthFormat,
            SubresourceRange = { AspectMask = ImageAspectFlags.DepthBit, LevelCount = 1, LayerCount = 1 },
        };
        vk.CreateImageView(device, viewInfo, null, out var view);

        var fbAtt    = view;
        var fbInfo   = new FramebufferCreateInfo
        {
            SType           = StructureType.FramebufferCreateInfo,
            RenderPass      = _shadowRenderPass,
            AttachmentCount = 1,
            PAttachments    = &fbAtt,
            Width = size, Height = size, Layers = 1,
        };
        vk.CreateFramebuffer(device, fbInfo, null, out var fb);

        return new Map2D { Image = image, Memory = memory, View = view, Framebuffer = fb };
    }

    private MapCube CreateMapCube(uint size)
    {
        var vk     = _ctx.Vk;
        var device = _ctx.Device;
        var cube   = new MapCube();

        CreateImage(size, size, 6, _depthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
            ImageCreateFlags.CreateCubeCompatibleBit,
            out cube.Image, out cube.Memory);

        // Full cube view for sampling
        var cubeViewInfo = new ImageViewCreateInfo
        {
            SType            = StructureType.ImageViewCreateInfo,
            Image            = cube.Image,
            ViewType         = ImageViewType.Cube,
            Format           = _depthFormat,
            SubresourceRange = { AspectMask = ImageAspectFlags.DepthBit, LevelCount = 1, LayerCount = 6 },
        };
        vk.CreateImageView(device, cubeViewInfo, null, out cube.CubeView);

        // Per-face views and framebuffers
        for (uint face = 0; face < 6; face++)
        {
            var faceViewInfo = new ImageViewCreateInfo
            {
                SType            = StructureType.ImageViewCreateInfo,
                Image            = cube.Image,
                ViewType         = ImageViewType.Type2D,
                Format           = _depthFormat,
                SubresourceRange = new ImageSubresourceRange
                {
                    AspectMask     = ImageAspectFlags.DepthBit,
                    LevelCount     = 1,
                    BaseArrayLayer = face,
                    LayerCount     = 1,
                },
            };
            vk.CreateImageView(device, faceViewInfo, null, out cube.FaceViews[face]);

            var att  = cube.FaceViews[face];
            var fbInfo = new FramebufferCreateInfo
            {
                SType           = StructureType.FramebufferCreateInfo,
                RenderPass      = _shadowRenderPass,
                AttachmentCount = 1,
                PAttachments    = &att,
                Width = size, Height = size, Layers = 1,
            };
            vk.CreateFramebuffer(device, fbInfo, null, out cube.FaceFramebuffers[face]);
        }

        return cube;
    }

    private void CreateSamplers()
    {
        var vk     = _ctx.Vk;
        var device = _ctx.Device;

        // Comparison sampler for 2D shadow maps (sampler2DShadow)
        var info2D = new SamplerCreateInfo
        {
            SType            = StructureType.SamplerCreateInfo,
            MagFilter        = Filter.Linear,
            MinFilter        = Filter.Linear,
            AddressModeU     = SamplerAddressMode.ClampToBorder,
            AddressModeV     = SamplerAddressMode.ClampToBorder,
            AddressModeW     = SamplerAddressMode.ClampToBorder,
            BorderColor      = BorderColor.FloatOpaqueWhite,
            CompareEnable    = true,
            CompareOp        = CompareOp.Less,
            MipmapMode       = SamplerMipmapMode.Nearest,
        };
        vk.CreateSampler(device, info2D, null, out _sampler2DShadow);

        // Plain sampler for cube shadow maps (samplerCube, manual comparison in shader)
        var infoCube = new SamplerCreateInfo
        {
            SType        = StructureType.SamplerCreateInfo,
            MagFilter    = Filter.Nearest,
            MinFilter    = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MipmapMode   = SamplerMipmapMode.Nearest,
        };
        vk.CreateSampler(device, infoCube, null, out _samplerCube);
    }

    private void CreateMainDescriptorResources()
    {
        var vk        = _ctx.Vk;
        var device    = _ctx.Device;
        var imgCount  = (int)_ctx.SwapchainImageCount;

        // ── Shadow matrices UBO (per swapchain image) ─────────────────────────
        _matBuffers = new VkBuffer[imgCount];
        _matMemory  = new DeviceMemory[imgCount];
        _matMapped  = new nint[imgCount];
        for (int i = 0; i < imgCount; i++)
        {
            CreateBuffer((ulong)ShadowMatricesUboSize,
                BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _matBuffers[i], out _matMemory[i]);
            void* ptr;
            vk.MapMemory(device, _matMemory[i], 0, (ulong)ShadowMatricesUboSize, 0, &ptr);
            _matMapped[i] = (nint)ptr;
            // Zero-initialise so unused matrices are identity-ish
            Unsafe.InitBlock((void*)_matMapped[i], 0, (uint)ShadowMatricesUboSize);
        }

        // ── Descriptor set layout (set=2 in Shapes.vk.frag) ──────────────────
        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            // b0: shadow matrices UBO
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer,
                    DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            // b1: dir shadow maps (sampler2DShadow)
            new() { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = MaxShadowDir, StageFlags = ShaderStageFlags.FragmentBit },
            // b2: spot shadow maps (sampler2DShadow)
            new() { Binding = 2, DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = MaxShadowSpot, StageFlags = ShaderStageFlags.FragmentBit },
            // b3: point shadow cube maps (samplerCube)
            new() { Binding = 3, DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = MaxShadowPoint, StageFlags = ShaderStageFlags.FragmentBit },
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 4, PBindings = bindings,
        };
        DescriptorSetLayout layout;
        vk.CreateDescriptorSetLayout(device, layoutInfo, null, out layout);
        MainDescSetLayout = layout;

        // ── Descriptor pool ───────────────────────────────────────────────────
        int samplerCount = (MaxShadowDir + MaxShadowSpot + MaxShadowPoint) * imgCount;
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.UniformBuffer,        DescriptorCount = (uint)imgCount },
            new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)samplerCount },
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, MaxSets = (uint)imgCount,
            PoolSizeCount = 2, PPoolSizes = poolSizes,
        };
        vk.CreateDescriptorPool(device, poolInfo, null, out _mainPool);

        // ── Allocate descriptor sets ──────────────────────────────────────────
        _mainSets = new DescriptorSet[imgCount];
        var layouts = stackalloc DescriptorSetLayout[imgCount];
        for (int i = 0; i < imgCount; i++) layouts[i] = MainDescSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _mainPool, DescriptorSetCount = (uint)imgCount, PSetLayouts = layouts,
        };
        fixed (DescriptorSet* p = _mainSets)
            vk.AllocateDescriptorSets(device, allocInfo, p);

        // ── Write descriptor sets ─────────────────────────────────────────────
        for (int img = 0; img < imgCount; img++)
        {
            var matBufInfo = new DescriptorBufferInfo
                { Buffer = _matBuffers[img], Offset = 0, Range = (ulong)ShadowMatricesUboSize };

            // Dir shadow map image infos
            var dirInfos = stackalloc DescriptorImageInfo[MaxShadowDir];
            for (int i = 0; i < MaxShadowDir; i++)
                dirInfos[i] = new DescriptorImageInfo
                    { Sampler = _sampler2DShadow, ImageView = _dirMaps[i].View, ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };

            // Spot shadow map image infos
            var spotInfos = stackalloc DescriptorImageInfo[MaxShadowSpot];
            for (int i = 0; i < MaxShadowSpot; i++)
                spotInfos[i] = new DescriptorImageInfo
                    { Sampler = _sampler2DShadow, ImageView = _spotMaps[i].View, ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };

            // Point shadow cube map image infos
            var ptInfos = stackalloc DescriptorImageInfo[MaxShadowPoint];
            for (int i = 0; i < MaxShadowPoint; i++)
                ptInfos[i] = new DescriptorImageInfo
                    { Sampler = _samplerCube, ImageView = _ptMaps[i].CubeView, ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };

            var writes = stackalloc WriteDescriptorSet[]
            {
                new() { SType = StructureType.WriteDescriptorSet, DstSet = _mainSets[img],
                        DstBinding = 0, DescriptorType = DescriptorType.UniformBuffer,
                        DescriptorCount = 1, PBufferInfo = &matBufInfo },
                new() { SType = StructureType.WriteDescriptorSet, DstSet = _mainSets[img],
                        DstBinding = 1, DescriptorType = DescriptorType.CombinedImageSampler,
                        DescriptorCount = MaxShadowDir, PImageInfo = dirInfos },
                new() { SType = StructureType.WriteDescriptorSet, DstSet = _mainSets[img],
                        DstBinding = 2, DescriptorType = DescriptorType.CombinedImageSampler,
                        DescriptorCount = MaxShadowSpot, PImageInfo = spotInfos },
                new() { SType = StructureType.WriteDescriptorSet, DstSet = _mainSets[img],
                        DstBinding = 3, DescriptorType = DescriptorType.CombinedImageSampler,
                        DescriptorCount = MaxShadowPoint, PImageInfo = ptInfos },
            };
            vk.UpdateDescriptorSets(device, 4, writes, 0, null);
        }
    }

    /// <summary>
    /// Transitions every shadow map slot (including unused ones) to DepthStencilReadOnlyOptimal
    /// so that descriptors referencing all slots are valid from the very first frame.
    /// </summary>
    private unsafe void InitializeShadowMapLayouts()
    {
        var vk     = _ctx.Vk;
        var device = _ctx.Device;

        var allocInfo = new CommandBufferAllocateInfo
        {
            SType              = StructureType.CommandBufferAllocateInfo,
            CommandPool        = _ctx.CommandPool,
            Level              = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        vk.AllocateCommandBuffers(device, allocInfo, out var cb);

        var beginInfo = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        vk.BeginCommandBuffer(cb, beginInfo);

        void Transition(Image img, uint layers)
        {
            var b = new ImageMemoryBarrier
            {
                SType               = StructureType.ImageMemoryBarrier,
                SrcAccessMask       = AccessFlags.None,
                DstAccessMask       = AccessFlags.ShaderReadBit,
                OldLayout           = ImageLayout.Undefined,
                NewLayout           = ImageLayout.DepthStencilReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image               = img,
                SubresourceRange    = new ImageSubresourceRange
                {
                    AspectMask     = ImageAspectFlags.DepthBit,
                    BaseMipLevel   = 0,
                    LevelCount     = 1,
                    BaseArrayLayer = 0,
                    LayerCount     = layers,
                },
            };
            vk.CmdPipelineBarrier(cb,
                PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit,
                0, 0, null, 0, null, 1, &b);
        }

        for (int i = 0; i < MaxShadowDir;   i++) Transition(_dirMaps[i].Image,  1);
        for (int i = 0; i < MaxShadowSpot;  i++) Transition(_spotMaps[i].Image, 1);
        for (int i = 0; i < MaxShadowPoint; i++) Transition(_ptMaps[i].Image,   6);

        vk.EndCommandBuffer(cb);

        var cbHandle = cb;
        var submit = new SubmitInfo
        {
            SType              = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers    = &cbHandle,
        };
        vk.QueueSubmit(_ctx.GraphicsQueue, 1, submit, default);
        vk.QueueWaitIdle(_ctx.GraphicsQueue);

        vk.FreeCommandBuffers(device, _ctx.CommandPool, 1, &cbHandle);
    }

    // ── Public shadow-pipeline accessors (for shapes' DrawShadow methods) ─────

    /// <summary>Returns the 2D depth pipeline matching the shape's vertex stride (12 or 32 bytes).</summary>
    public Pipeline GetShadow2DPipeline(uint strideBytes) =>
        strideBytes == 12 ? _pipe2D_S12 : _pipe2D_S32;

    /// <summary>Returns the point-light depth pipeline matching the shape's vertex stride.</summary>
    public Pipeline GetShadowPointPipeline(uint strideBytes) =>
        strideBytes == 12 ? _pipePoint_S12 : _pipePoint_S32;

    public PipelineLayout Shadow2DLayout   => _layout2D;
    public PipelineLayout ShadowPointLayout => _layoutPoint;

    // ── Low-level helpers ─────────────────────────────────────────────────────

    private void CreateImage(uint width, uint height, uint layers, Format format,
        ImageUsageFlags usage, ImageCreateFlags flags,
        out Image image, out DeviceMemory memory)
    {
        var vk   = _ctx.Vk;
        var dev  = _ctx.Device;
        var info = new ImageCreateInfo
        {
            SType         = StructureType.ImageCreateInfo,
            Flags         = flags,
            ImageType     = ImageType.Type2D,
            Format        = format,
            Extent        = new Extent3D(width, height, 1),
            MipLevels     = 1,
            ArrayLayers   = layers,
            Samples       = SampleCountFlags.Count1Bit,
            Tiling        = ImageTiling.Optimal,
            Usage         = usage,
            SharingMode   = SharingMode.Exclusive,
            InitialLayout = ImageLayout.Undefined,
        };
        vk.CreateImage(dev, info, null, out image);
        vk.GetImageMemoryRequirements(dev, image, out var req);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        vk.AllocateMemory(dev, alloc, null, out memory);
        vk.BindImageMemory(dev, image, memory, 0);
    }

    private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags props,
        out VkBuffer buffer, out DeviceMemory memory)
    {
        var vk  = _ctx.Vk;
        var dev = _ctx.Device;
        var info = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo, Size = size,
            Usage = usage, SharingMode = SharingMode.Exclusive,
        };
        vk.CreateBuffer(dev, info, null, out buffer);
        vk.GetBufferMemoryRequirements(dev, buffer, out var req);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = req.Size,
            MemoryTypeIndex = FindMemoryType(req.MemoryTypeBits, props),
        };
        vk.AllocateMemory(dev, alloc, null, out memory);
        vk.BindBufferMemory(dev, buffer, memory, 0);
    }

    private ShaderModule CreateShaderModule(byte[] code)
    {
        fixed (byte* ptr = code)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType    = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)code.Length,
                PCode    = (uint*)ptr,
            };
            _ctx.Vk.CreateShaderModule(_ctx.Device, info, null, out var module);
            return module;
        }
    }

    private uint FindMemoryType(uint typeBits, MemoryPropertyFlags props)
    {
        _ctx.Vk.GetPhysicalDeviceMemoryProperties(_ctx.PhysicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
            if ((typeBits & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
                return i;
        throw new Exception("[Shadow] No suitable memory type found.");
    }

    private Format FindDepthFormat()
    {
        var candidates = new[] { Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint };
        foreach (var fmt in candidates)
        {
            _ctx.Vk.GetPhysicalDeviceFormatProperties(_ctx.PhysicalDevice, fmt, out var props);
            if ((props.OptimalTilingFeatures & FormatFeatureFlags.DepthStencilAttachmentBit) != 0)
                return fmt;
        }
        throw new Exception("[Shadow] No supported depth format.");
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        var vk  = _ctx.Vk;
        var dev = _ctx.Device;
        vk.DeviceWaitIdle(dev);

        // Shadow maps
        for (int i = 0; i < MaxShadowDir;  i++) DestroyMap2D(ref _dirMaps[i]);
        for (int i = 0; i < MaxShadowSpot; i++) DestroyMap2D(ref _spotMaps[i]);
        for (int i = 0; i < MaxShadowPoint; i++) DestroyMapCube(ref _ptMaps[i]);

        // Samplers
        vk.DestroySampler(dev, _sampler2DShadow, null);
        vk.DestroySampler(dev, _samplerCube, null);

        // Main descriptor resources
        vk.DestroyDescriptorPool(dev, _mainPool, null);
        vk.DestroyDescriptorSetLayout(dev, MainDescSetLayout, null);
        for (int i = 0; i < _matBuffers.Length; i++)
        {
            vk.UnmapMemory(dev, _matMemory[i]);
            vk.DestroyBuffer(dev, _matBuffers[i], null);
            vk.FreeMemory(dev, _matMemory[i], null);
        }

        // Pipelines
        foreach (var p in new[] { _pipe2D_S32, _pipe2D_S12, _pipePoint_S32, _pipePoint_S12 })
            vk.DestroyPipeline(dev, p, null);
        vk.DestroyPipelineLayout(dev, _layout2D, null);
        vk.DestroyPipelineLayout(dev, _layoutPoint, null);

        // VP UBO
        vk.UnmapMemory(dev, _vpMemory);
        vk.DestroyBuffer(dev, _vpBuffer, null);
        vk.FreeMemory(dev, _vpMemory, null);
        vk.DestroyDescriptorPool(dev, _vpPool, null);
        vk.DestroyDescriptorSetLayout(dev, _vpSetLayout, null);

        vk.DestroyRenderPass(dev, _shadowRenderPass, null);
    }

    private void DestroyMap2D(ref Map2D m)
    {
        var vk  = _ctx.Vk;
        var dev = _ctx.Device;
        vk.DestroyFramebuffer(dev, m.Framebuffer, null);
        vk.DestroyImageView(dev, m.View, null);
        vk.FreeMemory(dev, m.Memory, null);
        vk.DestroyImage(dev, m.Image, null);
    }

    private void DestroyMapCube(ref MapCube m)
    {
        var vk  = _ctx.Vk;
        var dev = _ctx.Device;
        for (int f = 0; f < 6; f++)
        {
            vk.DestroyFramebuffer(dev, m.FaceFramebuffers[f], null);
            vk.DestroyImageView(dev, m.FaceViews[f], null);
        }
        vk.DestroyImageView(dev, m.CubeView, null);
        vk.FreeMemory(dev, m.Memory, null);
        vk.DestroyImage(dev, m.Image, null);
    }
}
