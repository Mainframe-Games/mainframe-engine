using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSampler = Silk.NET.Vulkan.Sampler;

namespace MainframeEngine;

/// <summary>The set-2 shadow descriptors a lit pipeline binds: a real <see cref="ShadowSystem"/> or the "no shadows" fallback.</summary>
internal interface IShadowDescriptors
{
    DescriptorSetLayout MainDescSetLayout { get; }

    /// <summary>The set to bind for the frame being recorded.</summary>
    DescriptorSet GetMainSet();
}

/// <summary>
/// Manages shadow maps for all three light types (directional, spot, point).
/// Call <see cref="RenderShadows{TState}"/> once per frame from <c>Engine.OnShadowPass</c>; lit nodes
/// then bind <see cref="GetMainSet"/> as descriptor set 2 in the main pass.
/// </summary>
/// <remarks>
/// Every shadow sub-pass (one per directional/spot light, six per point light) gets its own light
/// view-projection matrix in a per-frame-slot dynamic-offset uniform ring, so each recorded pass keeps
/// its matrix until the GPU executes it.
/// </remarks>
public sealed unsafe class ShadowSystem : IDisposable, IShadowDescriptors
{
    // ── Limits ────────────────────────────────────────────────────────────────

    public const int MaxShadowDir   = LightEnvironment.MaxDirectional; // 4
    // One less than LightEnvironment.MaxSpot: the fragment stage's sampler budget is 16 on
    // MoltenVK (maxPerStageDescriptorSamplers) — 4 dir + 7 spot + 4 point + 1 material texture.
    // The 8th spot light still lights, it just casts no shadow.
    public const int MaxShadowSpot  = 7;
    public const int MaxShadowPoint = 4;   // cube maps are expensive

    /// <summary>Shadow sub-passes per frame: one per dir/spot map, six per point cube map (35).</summary>
    public const int MaxShadowPasses = MaxShadowDir + MaxShadowSpot + MaxShadowPoint * 6;

    private const int DirSize   = 2048;
    private const int SpotSize  = 1024;
    private const int PointSize = 512;

    // ── Shadow matrices UBO layout (must match Shapes.vk.frag) ───────────────
    // mat4[MaxShadowDir] dir + mat4[MaxShadowSpot] spot, 64 bytes per matrix
    internal const int ShadowMatricesUboSize = (MaxShadowDir + MaxShadowSpot) * 64;

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
    private readonly bool           _linearDepthFiltering;
    private bool                    _disposed;

    // Depth-only render pass (shared for all shadow types)
    private RenderPass _shadowRenderPass;

    // Shadow pipelines — one per vertex stride per shadow type
    // Shapes with stride 32 (Box3d) and stride 12 (Quad, Spine) are supported.
    private Pipeline       _pipe2D_S32,   _pipe2D_S12;
    private Pipeline       _pipePoint_S32, _pipePoint_S12;
    private PipelineLayout _layout2D;      // push: mat4 model; set0: light VP (dynamic UBO)
    private PipelineLayout _layoutPoint;   // push: mat4 model + vec4 lightPosRange; set0: light VP (dynamic UBO)

    // Light-VP ring: MaxFramesInFlight × MaxShadowPasses matrices, bound with a dynamic offset per pass.
    private readonly UniformRing  _vpRing;
    private VkBuffer              _vpBuffer;
    private DeviceMemory          _vpMemory;
    private nint                  _vpMapped;
    private DescriptorPool        _vpPool;
    private DescriptorSetLayout   _vpSetLayout;
    private DescriptorSet         _vpSet;

    // Shadow maps (shared by both frame slots: the layout barriers in RenderShadows order a frame's
    // writes after the previous frame's sampling on the same queue)
    private readonly Map2D[]   _dirMaps  = new Map2D[MaxShadowDir];
    private readonly Map2D[]   _spotMaps = new Map2D[MaxShadowSpot];
    private readonly MapCube[] _ptMaps   = new MapCube[MaxShadowPoint];

    // Samplers
    private VkSampler _sampler2DShadow; // comparison sampler for dir/spot
    private VkSampler _samplerCube;     // plain sampler for point

    // Main-pass descriptor set (set=2 in Shapes.vk.frag), one per frame slot
    private DescriptorPool      _mainPool;
    public DescriptorSetLayout MainDescSetLayout { get; private set; }
    private readonly DescriptorSet[] _mainSets = new DescriptorSet[IVulkanContext.MaxFramesInFlight];

    // Shadow matrices UBO, one per frame slot
    private readonly VkBuffer[]     _matBuffers = new VkBuffer[IVulkanContext.MaxFramesInFlight];
    private readonly DeviceMemory[] _matMemory  = new DeviceMemory[IVulkanContext.MaxFramesInFlight];
    private readonly nint[]         _matMapped  = new nint[IVulkanContext.MaxFramesInFlight];

    // CPU-side matrices updated by RenderShadows each frame
    private readonly Matrix4x4[] _dirMats  = new Matrix4x4[MaxShadowDir];
    private readonly Matrix4x4[] _spotMats = new Matrix4x4[MaxShadowSpot];

    // ── Constructor ───────────────────────────────────────────────────────────

    public ShadowSystem(IVulkanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        (_depthFormat, _linearDepthFiltering) = ChooseDepthFormat(ctx);

        ctx.Vk.GetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        _vpRing = new UniformRing((ulong)sizeof(Matrix4x4), MaxShadowPasses, IVulkanContext.MaxFramesInFlight,
            props.Limits.MinUniformBufferOffsetAlignment);

        CreateShadowRenderPass();
        CreateVpRingResources();
        CreateShadowPipelines();
        CreateShadowMaps();
        _sampler2DShadow = CreateComparisonSampler(ctx, _linearDepthFiltering);
        _samplerCube = CreateCubeSampler(ctx);
        CreateMainDescriptorResources();
        InitializeShadowMapLayouts();
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>Returns the shadow descriptor set for the frame being recorded.</summary>
    public DescriptorSet GetMainSet() => _mainSets[_ctx.FrameSlot];

    /// <summary>
    /// Renders shadow maps for all active lights, then updates the shadow matrices UBO.
    /// Call from IGame.OnShadowPass (before the main render pass).
    /// draw2D: bind VB, push model matrix via layout2D, call CmdDraw.
    /// drawPoint: bind VB, push model+lightPosRange via layoutPoint, call CmdDraw.
    /// </summary>
    /// <remarks>
    /// Allocates if the callbacks capture state; prefer the
    /// <see cref="RenderShadows{TState}(LightEnvironment, TState, ShadowDraw2D{TState}, ShadowDrawPoint{TState})"/>
    /// overload with static lambdas in per-frame code.
    /// </remarks>
    public void RenderShadows(
        LightEnvironment lights,
        Action<CommandBuffer, Pipeline, Pipeline, PipelineLayout> draw2D,
        Action<CommandBuffer, Pipeline, Pipeline, PipelineLayout, Vector3, float> drawPoint)
    {
        RenderShadows(lights, (draw2D, drawPoint),
            static (s, cb, p32, p12, layout) => s.draw2D(cb, p32, p12, layout),
            static (s, cb, p32, p12, layout, lightPos, lightRange) => s.drawPoint(cb, p32, p12, layout, lightPos, lightRange));
    }

    /// <summary>
    /// Allocation-free form of <see cref="RenderShadows(LightEnvironment, Action{CommandBuffer, Pipeline, Pipeline, PipelineLayout}, Action{CommandBuffer, Pipeline, Pipeline, PipelineLayout, Vector3, float})"/>:
    /// <paramref name="state"/> is handed to the callbacks so they can be static lambdas.
    /// Call at most once per frame: each frame slot owns one ring region.
    /// </summary>
    public void RenderShadows<TState>(
        LightEnvironment lights,
        TState state,
        ShadowDraw2D<TState> draw2D,
        ShadowDrawPoint<TState> drawPoint)
    {
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(draw2D);
        ArgumentNullException.ThrowIfNull(drawPoint);
        if (!_ctx.FrameStarted)
            return;

        var cb        = _ctx.CurrentCommandBuffer;
        var frameSlot = _ctx.FrameSlot;

        int numDir   = Math.Min(lights.DirectionalLights.Count, MaxShadowDir);
        int numSpot  = Math.Min(lights.SpotLights.Count, MaxShadowSpot);
        int numPoint = Math.Min(lights.PointLights.Count, MaxShadowPoint);

        // Transition all shadow maps to DepthStencilAttachmentOptimal for writing
        TransitionAll(cb, numDir, numSpot, numPoint, toWrite: true);

        // ── Directional shadow maps ──────────────────────────────────────────
        for (int i = 0; i < numDir; i++)
        {
            _dirMats[i] = CalcDirLightMatrix(lights.DirectionalLights[i]);
            BeginShadowPass(cb, _dirMaps[i].Framebuffer, DirSize, _layout2D, WritePassMatrix(frameSlot, PassIndexDir(i), _dirMats[i]));
            draw2D(state, cb, _pipe2D_S32, _pipe2D_S12, _layout2D);
            _ctx.Vk.CmdEndRenderPass(cb);
        }

        // ── Spot shadow maps ─────────────────────────────────────────────────
        for (int i = 0; i < numSpot; i++)
        {
            _spotMats[i] = CalcSpotLightMatrix(lights.SpotLights[i]);
            BeginShadowPass(cb, _spotMaps[i].Framebuffer, SpotSize, _layout2D, WritePassMatrix(frameSlot, PassIndexSpot(i), _spotMats[i]));
            draw2D(state, cb, _pipe2D_S32, _pipe2D_S12, _layout2D);
            _ctx.Vk.CmdEndRenderPass(cb);
        }

        // ── Point shadow cube maps ───────────────────────────────────────────
        for (int i = 0; i < numPoint; i++)
        {
            var light = lights.PointLights[i];
            for (int face = 0; face < 6; face++)
            {
                var (dir, up) = CubeFaces[face];
                var faceVP = CalcPointFaceMatrix(light.Position, dir, up, light.Range);
                BeginShadowPass(cb, _ptMaps[i].FaceFramebuffers[face], PointSize, _layoutPoint,
                    WritePassMatrix(frameSlot, PassIndexPoint(i, face), faceVP));
                drawPoint(state, cb, _pipePoint_S32, _pipePoint_S12, _layoutPoint, light.Position, light.Range);
                _ctx.Vk.CmdEndRenderPass(cb);
            }
        }

        // Transition shadow maps to DepthStencilReadOnlyOptimal for sampling in main pass
        TransitionAll(cb, numDir, numSpot, numPoint, toWrite: false);

        // Upload light-space matrices to this frame slot's UBO
        var dst = (float*)(void*)_matMapped[frameSlot];
        for (int i = 0; i < MaxShadowDir; i++) Unsafe.Copy(dst + i * 16, ref _dirMats[i]);
        dst += MaxShadowDir * 16;
        for (int i = 0; i < MaxShadowSpot; i++) Unsafe.Copy(dst + i * 16, ref _spotMats[i]);
    }

    // Ring slot of each sub-pass: [dir 0..3][spot 0..6][point 0 faces 0..5]...[point 3 faces 0..5]
    internal static int PassIndexDir(int light) => light;
    internal static int PassIndexSpot(int light) => MaxShadowDir + light;
    internal static int PassIndexPoint(int light, int face) => MaxShadowDir + MaxShadowSpot + light * 6 + face;

    /// <summary>Ring offset of one sub-pass's matrix in one frame slot (exposed for tests).</summary>
    internal uint PassOffset(int frameSlot, int pass) => _vpRing.Offset(frameSlot, pass);

    /// <summary>The light view-projection stored for one sub-pass (test hook: reads the mapped ring).</summary>
    internal Matrix4x4 ReadPassMatrix(int frameSlot, int pass) => *(Matrix4x4*)(_vpMapped + (nint)_vpRing.Offset(frameSlot, pass));

    private uint WritePassMatrix(int frameSlot, int pass, in Matrix4x4 matrix)
    {
        var offset = _vpRing.Offset(frameSlot, pass);
        *(Matrix4x4*)(_vpMapped + (nint)offset) = matrix;
        return offset;
    }

    // Look direction and up vector per cube face, in Vulkan face order (+X, -X, +Y, -Y, +Z, -Z).
    private static readonly (Vector3 Dir, Vector3 Up)[] CubeFaces =
    [
        (Vector3.UnitX,  -Vector3.UnitY),
        (-Vector3.UnitX, -Vector3.UnitY),
        (Vector3.UnitY,   Vector3.UnitZ),
        (-Vector3.UnitY, -Vector3.UnitZ),
        (Vector3.UnitZ,  -Vector3.UnitY),
        (-Vector3.UnitZ, -Vector3.UnitY),
    ];

    // ── Shadow pass helpers ───────────────────────────────────────────────────

    // Begins a depth-only shadow render pass and binds this pass's light VP (dynamic offset); the caller draws, then ends it.
    private void BeginShadowPass(CommandBuffer cb, Framebuffer fb, uint size, PipelineLayout bindLayout, uint vpOffset)
    {
        var vk = _ctx.Vk;

        var clearVal = new ClearValue { DepthStencil = new ClearDepthStencilValue { Depth = 1.0f, Stencil = 0 } };
        var rpInfo   = new RenderPassBeginInfo
        {
            SType           = StructureType.RenderPassBeginInfo,
            RenderPass      = _shadowRenderPass,
            Framebuffer     = fb,
            RenderArea      = new Rect2D { Extent = new Extent2D(size, size) },
            ClearValueCount = 1,
            PClearValues    = &clearVal,
        };
        vk.CmdBeginRenderPass(cb, &rpInfo, SubpassContents.Inline);

        // Standard viewport (no Y-flip) — shadow maps are depth-only; the UV lookup
        // in the fragment shader uses standard NDC→[0,1] mapping so the viewport must match.
        var vp = new Viewport { Width = size, Height = size, MinDepth = 0f, MaxDepth = 1f };
        vk.CmdSetViewport(cb, 0, 1, &vp);
        var sc = new Rect2D { Extent = new Extent2D(size, size) };
        vk.CmdSetScissor(cb, 0, 1, &sc);

        var vpSet = _vpSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, bindLayout, 0, 1, &vpSet, 1, &vpOffset);
    }

    // ── Light matrix computation ──────────────────────────────────────────────

    internal static Matrix4x4 CalcDirLightMatrix(DirectionalLight light)
    {
        var lightDir = Vector3.Normalize(light.Direction);
        var lightPos = -lightDir * 20f; // pull back 20 units from scene centre
        var view     = Matrix4x4.CreateLookAt(lightPos, lightPos + lightDir, ChooseUp(lightDir));
        var proj     = Matrix4x4.CreateOrthographicOffCenter(-20, 20, -20, 20, 0.1f, 50f);
        return view * proj;
    }

    internal static Matrix4x4 CalcSpotLightMatrix(SpotLight light)
    {
        var fovY = float.DegreesToRadians(light.OuterConeAngle * 2f);
        var dir  = Vector3.Normalize(light.Direction);
        var view = Matrix4x4.CreateLookAt(light.Position, light.Position + dir, ChooseUp(dir));
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(fovY, 1f, 0.1f, light.Range);
        return view * proj;
    }

    private static Matrix4x4 CalcPointFaceMatrix(Vector3 pos, Vector3 dir, Vector3 up, float range)
    {
        var view = Matrix4x4.CreateLookAt(pos, pos + dir, up);
        var proj = Matrix4x4.CreatePerspectiveFieldOfView(float.DegreesToRadians(90f), 1f, 0.05f, range);
        return view * proj;
    }

    /// <summary>World up, or +Z when the light points (nearly) straight up/down, where up would be degenerate.</summary>
    internal static Vector3 ChooseUp(Vector3 dir)
        => MathF.Abs(Vector3.Dot(Vector3.Normalize(dir), Vector3.UnitY)) > 0.99f ? Vector3.UnitZ : Vector3.UnitY;

    // ── Pipeline barrier helpers ──────────────────────────────────────────────

    private void TransitionAll(CommandBuffer cb, int numDir, int numSpot, int numPoint, bool toWrite)
    {
        var vk = _ctx.Vk;

        // To write: the previous frame's sampling (fragment shader) must finish; the maps are
        // cleared by the render pass, so the old contents are discarded (Undefined).
        var oldLayout = toWrite ? ImageLayout.Undefined : ImageLayout.DepthStencilAttachmentOptimal;
        var newLayout = toWrite ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.DepthStencilReadOnlyOptimal;
        var srcAccess = toWrite ? AccessFlags.ShaderReadBit : AccessFlags.DepthStencilAttachmentWriteBit;
        var dstAccess = toWrite
            ? AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit
            : AccessFlags.ShaderReadBit;
        var srcStage = toWrite
            ? PipelineStageFlags.FragmentShaderBit
            : PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        var dstStage = toWrite
            ? PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit
            : PipelineStageFlags.FragmentShaderBit;

        for (int i = 0; i < numDir; i++)
            VkHelpers.DepthBarrier(vk, cb, _dirMaps[i].Image, 1, oldLayout, newLayout, srcAccess, dstAccess, srcStage, dstStage);
        for (int i = 0; i < numSpot; i++)
            VkHelpers.DepthBarrier(vk, cb, _spotMaps[i].Image, 1, oldLayout, newLayout, srcAccess, dstAccess, srcStage, dstStage);
        for (int i = 0; i < numPoint; i++)
            VkHelpers.DepthBarrier(vk, cb, _ptMaps[i].Image, 6, oldLayout, newLayout, srcAccess, dstAccess, srcStage, dstStage);
    }

    // ── Shared with ShadowFallback ────────────────────────────────────────────

    /// <summary>
    /// Depth format for shadow maps: must support depth attachment + sampling; linear filtering
    /// (hardware 2×2 PCF through the comparison sampler) is preferred but optional.
    /// </summary>
    internal static (Format Format, bool LinearFilter) ChooseDepthFormat(IVulkanContext ctx)
    {
        ReadOnlySpan<Format> candidates = [Format.D32Sfloat, Format.D32SfloatS8Uint, Format.D24UnormS8Uint, Format.D16Unorm];
        const FormatFeatureFlags required = FormatFeatureFlags.DepthStencilAttachmentBit | FormatFeatureFlags.SampledImageBit;

        Format? fallback = null;
        foreach (var format in candidates)
        {
            ctx.Vk.GetPhysicalDeviceFormatProperties(ctx.PhysicalDevice, format, out var props);
            var features = props.OptimalTilingFeatures;
            if ((features & required) != required)
                continue;
            if ((features & FormatFeatureFlags.SampledImageFilterLinearBit) != 0)
                return (format, true);
            fallback ??= format;
        }

        if (fallback is { } nearestOnly)
        {
            Log.Warning($"[Shadow] No depth format supports linear filtering; using {nearestOnly} with nearest comparison.");
            return (nearestOnly, false);
        }

        throw new VulkanException("[Shadow] No depth format supports both depth attachment and sampling.");
    }

    internal static VkSampler CreateComparisonSampler(IVulkanContext ctx, bool linear)
    {
        var filter = linear ? Filter.Linear : Filter.Nearest;
        var info = new SamplerCreateInfo
        {
            SType         = StructureType.SamplerCreateInfo,
            MagFilter     = filter,
            MinFilter     = filter,
            AddressModeU  = SamplerAddressMode.ClampToBorder,
            AddressModeV  = SamplerAddressMode.ClampToBorder,
            AddressModeW  = SamplerAddressMode.ClampToBorder,
            BorderColor   = BorderColor.FloatOpaqueWhite,
            CompareEnable = true,
            CompareOp     = CompareOp.Less,
            MipmapMode    = SamplerMipmapMode.Nearest,
        };
        ctx.Vk.CreateSampler(ctx.Device, in info, null, out var sampler).Check("vkCreateSampler (shadow comparison)");
        return sampler;
    }

    internal static VkSampler CreateCubeSampler(IVulkanContext ctx)
    {
        // Plain sampler for cube shadow maps (samplerCube, manual comparison in shader)
        var info = new SamplerCreateInfo
        {
            SType        = StructureType.SamplerCreateInfo,
            MagFilter    = Filter.Nearest,
            MinFilter    = Filter.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MipmapMode   = SamplerMipmapMode.Nearest,
        };
        ctx.Vk.CreateSampler(ctx.Device, in info, null, out var sampler).Check("vkCreateSampler (shadow cube)");
        return sampler;
    }

    /// <summary>
    /// Set-2 layout (Shapes.vk.frag / SpineLit.vk.frag): b0 matrices UBO, b1 dir maps, b2 spot maps,
    /// b3 point cube maps. The comparison samplers must be immutable (baked into the layout): Metal via
    /// MoltenVK reports mutableComparisonSamplers=false, which forbids writing compareEnable samplers
    /// through vkUpdateDescriptorSets.
    /// </summary>
    internal static DescriptorSetLayout CreateMainSetLayout(IVulkanContext ctx, VkSampler comparisonSampler)
    {
        var dirSamplers  = stackalloc VkSampler[MaxShadowDir];
        var spotSamplers = stackalloc VkSampler[MaxShadowSpot];
        for (int i = 0; i < MaxShadowDir; i++) dirSamplers[i]  = comparisonSampler;
        for (int i = 0; i < MaxShadowSpot; i++) spotSamplers[i] = comparisonSampler;

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer,
                    DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = MaxShadowDir, StageFlags = ShaderStageFlags.FragmentBit,
                    PImmutableSamplers = dirSamplers },
            new() { Binding = 2, DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = MaxShadowSpot, StageFlags = ShaderStageFlags.FragmentBit,
                    PImmutableSamplers = spotSamplers },
            new() { Binding = 3, DescriptorType = DescriptorType.CombinedImageSampler,
                    DescriptorCount = MaxShadowPoint, StageFlags = ShaderStageFlags.FragmentBit },
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4,
            PBindings = bindings,
        };
        ctx.Vk.CreateDescriptorSetLayout(ctx.Device, in layoutInfo, null, out var layout).Check("vkCreateDescriptorSetLayout (shadow set 2)");
        return layout;
    }

    /// <summary>Writes the four set-2 bindings: one UBO, then the dir/spot 2D views and the point cube views.</summary>
    internal static void WriteMainSet(IVulkanContext ctx, DescriptorSet set, VkBuffer matrices,
        ReadOnlySpan<ImageView> dirViews, ReadOnlySpan<ImageView> spotViews, ReadOnlySpan<ImageView> cubeViews, VkSampler cubeSampler)
    {
        var matBufInfo = new DescriptorBufferInfo { Buffer = matrices, Offset = 0, Range = ShadowMatricesUboSize };

        // Dir/spot: Sampler stays null — b1/b2 use immutable samplers from the layout.
        var dirInfos = stackalloc DescriptorImageInfo[MaxShadowDir];
        for (int i = 0; i < MaxShadowDir; i++)
            dirInfos[i] = new DescriptorImageInfo { ImageView = dirViews[i], ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };
        var spotInfos = stackalloc DescriptorImageInfo[MaxShadowSpot];
        for (int i = 0; i < MaxShadowSpot; i++)
            spotInfos[i] = new DescriptorImageInfo { ImageView = spotViews[i], ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };
        var ptInfos = stackalloc DescriptorImageInfo[MaxShadowPoint];
        for (int i = 0; i < MaxShadowPoint; i++)
            ptInfos[i] = new DescriptorImageInfo { Sampler = cubeSampler, ImageView = cubeViews[i], ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };

        var writes = stackalloc WriteDescriptorSet[4];
        writes[0] = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            PBufferInfo = &matBufInfo
        };
        writes[1] = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = MaxShadowDir,
            PImageInfo = dirInfos
        };
        writes[2] = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 2,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = MaxShadowSpot,
            PImageInfo = spotInfos
        };
        writes[3] = new()
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 3,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = MaxShadowPoint,
            PImageInfo = ptInfos
        };
        ctx.Vk.UpdateDescriptorSets(ctx.Device, 4, writes, 0, null);
    }

    /// <summary>Descriptor pool for <paramref name="setCount"/> set-2 sets.</summary>
    internal static DescriptorPool CreateMainPool(IVulkanContext ctx, uint setCount)
    {
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.UniformBuffer,        DescriptorCount = setCount },
            new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (MaxShadowDir + MaxShadowSpot + MaxShadowPoint) * setCount },
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = setCount,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        ctx.Vk.CreateDescriptorPool(ctx.Device, in poolInfo, null, out var pool).Check("vkCreateDescriptorPool (shadow set 2)");
        return pool;
    }

    // ── Resource creation ─────────────────────────────────────────────────────

    private void CreateShadowRenderPass()
    {
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
        _ctx.Vk.CreateRenderPass(_ctx.Device, in info, null, out _shadowRenderPass).Check("vkCreateRenderPass (shadow)");
    }

    private void CreateVpRingResources()
    {
        var vk     = _ctx.Vk;
        var device = _ctx.Device;

        _vpMapped = VkHelpers.CreateMappedBuffer(_ctx, _vpRing.Size, BufferUsageFlags.UniformBufferBit, out _vpBuffer, out _vpMemory);
        Unsafe.InitBlock((void*)_vpMapped, 0, (uint)_vpRing.Size);

        // Binding 0 = dynamic UBO (vertex stage): one matrix per sub-pass, selected by the dynamic offset.
        var bind = new DescriptorSetLayoutBinding
        {
            Binding         = 0,
            DescriptorType  = DescriptorType.UniformBufferDynamic,
            DescriptorCount = 1,
            StageFlags      = ShaderStageFlags.VertexBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType        = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings    = &bind,
        };
        vk.CreateDescriptorSetLayout(device, in layoutInfo, null, out _vpSetLayout).Check("vkCreateDescriptorSetLayout (light VP)");

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.UniformBufferDynamic, DescriptorCount = 1 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = 1,
            PoolSizeCount = 1,
            PPoolSizes = &poolSize,
        };
        vk.CreateDescriptorPool(device, in poolInfo, null, out _vpPool).Check("vkCreateDescriptorPool (light VP)");

        var setLayout = _vpSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType              = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool     = _vpPool,
            DescriptorSetCount = 1,
            PSetLayouts        = &setLayout,
        };
        vk.AllocateDescriptorSets(device, in allocInfo, out _vpSet).Check("vkAllocateDescriptorSets (light VP)");

        var bufInfo = new DescriptorBufferInfo { Buffer = _vpBuffer, Offset = 0, Range = (ulong)sizeof(Matrix4x4) };
        var write   = new WriteDescriptorSet
        {
            SType           = StructureType.WriteDescriptorSet,
            DstSet          = _vpSet,
            DstBinding      = 0,
            DescriptorType  = DescriptorType.UniformBufferDynamic,
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
            SetLayoutCount         = 1,
            PSetLayouts            = &vpLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges    = &push2D,
        };
        vk.CreatePipelineLayout(device, in l2DInfo, null, out _layout2D).Check("vkCreatePipelineLayout (shadow 2D)");

        var pushPt = new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Offset = 0, Size = 80 };
        var lPtInfo = new PipelineLayoutCreateInfo
        {
            SType                  = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount         = 1,
            PSetLayouts            = &vpLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges    = &pushPt,
        };
        vk.CreatePipelineLayout(device, in lPtInfo, null, out _layoutPoint).Check("vkCreatePipelineLayout (shadow point)");

        // ── Shared pipeline state ─────────────────────────────────────────────
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");

        var vert2D = VkHelpers.CreateShaderModule(_ctx, "Content/Shaders/Shadows/Shadow2D.vk.vert.spv");
        var frag2D = VkHelpers.CreateShaderModule(_ctx, "Content/Shaders/Shadows/Shadow2D.vk.frag.spv");
        var vertPt = VkHelpers.CreateShaderModule(_ctx, "Content/Shaders/Shadows/ShadowPoint.vk.vert.spv");
        var fragPt = VkHelpers.CreateShaderModule(_ctx, "Content/Shaders/Shadows/ShadowPoint.vk.frag.spv");

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
            SType = StructureType.PipelineViewportStateCreateInfo,
            ViewportCount = 1,
            ScissorCount = 1,
        };
        // Geometry is authored counter-clockwise (front faces CCW, right-handed). The main pass
        // flips Y with a negative-height viewport, which keeps CCW = front; shadow passes use a
        // standard viewport, which mirrors the winding, so geometric front faces arrive clockwise.
        // FrontFace = Clockwise restores "front" meaning "faces the light"; back faces are culled
        // and the depth bias handles acne. (Before M1 this was CCW + cull FRONT: the same
        // rasterisation, labelled as "front-face culling".)
        var rasterizer = new PipelineRasterizationStateCreateInfo
        {
            SType       = StructureType.PipelineRasterizationStateCreateInfo,
            PolygonMode = PolygonMode.Fill,
            LineWidth   = 1f,
            CullMode    = CullModeFlags.BackBit,
            FrontFace   = FrontFace.Clockwise,
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
        _pipe2D_S32    = BuildPipeline(stages2D, 2, 32, _layout2D, ref inputAssembly, ref viewportState, ref rasterizer, ref multisampling, ref depthStencil, ref dynamicState);
        _pipe2D_S12    = BuildPipeline(stages2D, 2, 12, _layout2D, ref inputAssembly, ref viewportState, ref rasterizer, ref multisampling, ref depthStencil, ref dynamicState);
        _pipePoint_S32 = BuildPipeline(stagesPt, 2, 32, _layoutPoint, ref inputAssembly, ref viewportState, ref rasterizer, ref multisampling, ref depthStencil, ref dynamicState);
        _pipePoint_S12 = BuildPipeline(stagesPt, 2, 12, _layoutPoint, ref inputAssembly, ref viewportState, ref rasterizer, ref multisampling, ref depthStencil, ref dynamicState);

        SilkMarshal.Free((nint)entryPoint);
        vk.DestroyShaderModule(device, vert2D, null);
        vk.DestroyShaderModule(device, frag2D, null);
        vk.DestroyShaderModule(device, vertPt, null);
        vk.DestroyShaderModule(device, fragPt, null);
    }

    private Pipeline BuildPipeline(
        PipelineShaderStageCreateInfo* stages, uint stageCount,
        uint stride, PipelineLayout layout,
        ref PipelineInputAssemblyStateCreateInfo ia,
        ref PipelineViewportStateCreateInfo vps,
        ref PipelineRasterizationStateCreateInfo rast,
        ref PipelineMultisampleStateCreateInfo ms,
        ref PipelineDepthStencilStateCreateInfo ds,
        ref PipelineDynamicStateCreateInfo dyn)
    {
        var bindingDesc = new VertexInputBindingDescription { Binding = 0, Stride = stride, InputRate = VertexInputRate.Vertex };
        var attrib = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType                           = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount   = 1,
            PVertexBindingDescriptions      = &bindingDesc,
            VertexAttributeDescriptionCount = 1,
            PVertexAttributeDescriptions    = &attrib,
        };

        fixed (PipelineInputAssemblyStateCreateInfo* pIA = &ia)
        fixed (PipelineViewportStateCreateInfo* pVPS = &vps)
        fixed (PipelineRasterizationStateCreateInfo* pR = &rast)
        fixed (PipelineMultisampleStateCreateInfo* pMS = &ms)
        fixed (PipelineDepthStencilStateCreateInfo* pDS = &ds)
        fixed (PipelineDynamicStateCreateInfo* pDyn = &dyn)
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
                RenderPass          = _shadowRenderPass,
                Subpass             = 0,
            };
            _ctx.Vk.CreateGraphicsPipelines(_ctx.Device, default, 1, in pipeInfo, null, out var pipe).Check("vkCreateGraphicsPipelines (shadow)");
            return pipe;
        }
    }

    private void CreateShadowMaps()
    {
        for (int i = 0; i < MaxShadowDir; i++) _dirMaps[i]  = CreateMap2D(DirSize);
        for (int i = 0; i < MaxShadowSpot; i++) _spotMaps[i] = CreateMap2D(SpotSize);
        for (int i = 0; i < MaxShadowPoint; i++) _ptMaps[i]   = CreateMapCube(PointSize);
    }

    private Framebuffer CreateDepthFramebuffer(ImageView view, uint size)
    {
        var fbInfo = new FramebufferCreateInfo
        {
            SType           = StructureType.FramebufferCreateInfo,
            RenderPass      = _shadowRenderPass,
            AttachmentCount = 1,
            PAttachments    = &view,
            Width           = size,
            Height          = size,
            Layers          = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in fbInfo, null, out var fb).Check("vkCreateFramebuffer (shadow)");
        return fb;
    }

    private Map2D CreateMap2D(uint size)
    {
        VkHelpers.CreateImage(_ctx, size, size, 1, _depthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, ImageCreateFlags.None,
            out var image, out var memory);
        var view = VkHelpers.CreateDepthView(_ctx, image, _depthFormat, ImageViewType.Type2D, 0, 1);
        return new Map2D { Image = image, Memory = memory, View = view, Framebuffer = CreateDepthFramebuffer(view, size) };
    }

    private MapCube CreateMapCube(uint size)
    {
        var cube = new MapCube();
        VkHelpers.CreateImage(_ctx, size, size, 6, _depthFormat,
            ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, ImageCreateFlags.CreateCubeCompatibleBit,
            out cube.Image, out cube.Memory);

        cube.CubeView = VkHelpers.CreateDepthView(_ctx, cube.Image, _depthFormat, ImageViewType.TypeCube, 0, 6);
        for (uint face = 0; face < 6; face++)
        {
            cube.FaceViews[face] = VkHelpers.CreateDepthView(_ctx, cube.Image, _depthFormat, ImageViewType.Type2D, face, 1);
            cube.FaceFramebuffers[face] = CreateDepthFramebuffer(cube.FaceViews[face], size);
        }
        return cube;
    }

    private void CreateMainDescriptorResources()
    {
        var vk     = _ctx.Vk;
        var device = _ctx.Device;
        const int slots = IVulkanContext.MaxFramesInFlight;

        for (int i = 0; i < slots; i++)
        {
            _matMapped[i] = VkHelpers.CreateMappedBuffer(_ctx, ShadowMatricesUboSize, BufferUsageFlags.UniformBufferBit,
                out _matBuffers[i], out _matMemory[i]);
            Unsafe.InitBlock((void*)_matMapped[i], 0, ShadowMatricesUboSize);
        }

        MainDescSetLayout = CreateMainSetLayout(_ctx, _sampler2DShadow);
        _mainPool = CreateMainPool(_ctx, slots);

        var layouts = stackalloc DescriptorSetLayout[slots];
        for (int i = 0; i < slots; i++) layouts[i] = MainDescSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _mainPool,
            DescriptorSetCount = slots,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* p = _mainSets)
            vk.AllocateDescriptorSets(device, in allocInfo, p).Check("vkAllocateDescriptorSets (shadow set 2)");

        Span<ImageView> dirViews = stackalloc ImageView[MaxShadowDir];
        Span<ImageView> spotViews = stackalloc ImageView[MaxShadowSpot];
        Span<ImageView> cubeViews = stackalloc ImageView[MaxShadowPoint];
        for (int i = 0; i < MaxShadowDir; i++) dirViews[i] = _dirMaps[i].View;
        for (int i = 0; i < MaxShadowSpot; i++) spotViews[i] = _spotMaps[i].View;
        for (int i = 0; i < MaxShadowPoint; i++) cubeViews[i] = _ptMaps[i].CubeView;

        // The image infos are identical for every frame slot; only the matrices UBO differs.
        for (int slot = 0; slot < slots; slot++)
            WriteMainSet(_ctx, _mainSets[slot], _matBuffers[slot], dirViews, spotViews, cubeViews, _samplerCube);
    }

    /// <summary>
    /// Transitions every shadow map slot (including unused ones) to DepthStencilReadOnlyOptimal
    /// so that descriptors referencing all slots are valid from the very first frame.
    /// </summary>
    private void InitializeShadowMapLayouts()
    {
        VkHelpers.SubmitAndWait(_ctx, this, static (self, cb) =>
        {
            var vk = self._ctx.Vk;
            void Transition(Image img, uint layers) => VkHelpers.DepthBarrier(vk, cb, img, layers,
                ImageLayout.Undefined, ImageLayout.DepthStencilReadOnlyOptimal,
                AccessFlags.None, AccessFlags.ShaderReadBit, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit);

            for (int i = 0; i < MaxShadowDir; i++) Transition(self._dirMaps[i].Image, 1);
            for (int i = 0; i < MaxShadowSpot; i++) Transition(self._spotMaps[i].Image, 1);
            for (int i = 0; i < MaxShadowPoint; i++) Transition(self._ptMaps[i].Image, 6);
        });
    }

    // ── Public shadow-pipeline accessors (for shapes' DrawShadow methods) ─────

    /// <summary>Returns the 2D depth pipeline matching the shape's vertex stride (12 or 32 bytes).</summary>
    public Pipeline GetShadow2DPipeline(uint strideBytes) =>
        strideBytes == 12 ? _pipe2D_S12 : _pipe2D_S32;

    /// <summary>Returns the point-light depth pipeline matching the shape's vertex stride.</summary>
    public Pipeline GetShadowPointPipeline(uint strideBytes) =>
        strideBytes == 12 ? _pipePoint_S12 : _pipePoint_S32;

    public PipelineLayout Shadow2DLayout => _layout2D;
    public PipelineLayout ShadowPointLayout => _layoutPoint;

    // ── Dispose ───────────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var vk  = _ctx.Vk;
        var dev = _ctx.Device;
        vk.DeviceWaitIdle(dev);

        // Shadow maps
        for (int i = 0; i < MaxShadowDir; i++) DestroyMap2D(ref _dirMaps[i]);
        for (int i = 0; i < MaxShadowSpot; i++) DestroyMap2D(ref _spotMaps[i]);
        for (int i = 0; i < MaxShadowPoint; i++) DestroyMapCube(ref _ptMaps[i]);

        // Main descriptor resources (before the samplers baked into the layout)
        vk.DestroyDescriptorPool(dev, _mainPool, null);
        vk.DestroyDescriptorSetLayout(dev, MainDescSetLayout, null);
        for (int i = 0; i < _matBuffers.Length; i++)
            VkHelpers.DestroyMappedBuffer(_ctx, _matBuffers[i], _matMemory[i]);

        vk.DestroySampler(dev, _sampler2DShadow, null);
        vk.DestroySampler(dev, _samplerCube, null);

        // Pipelines
        vk.DestroyPipeline(dev, _pipe2D_S32, null);
        vk.DestroyPipeline(dev, _pipe2D_S12, null);
        vk.DestroyPipeline(dev, _pipePoint_S32, null);
        vk.DestroyPipeline(dev, _pipePoint_S12, null);
        vk.DestroyPipelineLayout(dev, _layout2D, null);
        vk.DestroyPipelineLayout(dev, _layoutPoint, null);

        // Light VP ring
        VkHelpers.DestroyMappedBuffer(_ctx, _vpBuffer, _vpMemory);
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
        vk.DestroyImage(dev, m.Image, null);
        vk.FreeMemory(dev, m.Memory, null);
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
        vk.DestroyImage(dev, m.Image, null);
        vk.FreeMemory(dev, m.Memory, null);
    }
}

/// <summary>Records directional/spot shadow casters; see <see cref="ShadowSystem.RenderShadows{TState}"/>.</summary>
public delegate void ShadowDraw2D<in TState>(
    TState state, CommandBuffer cb, Pipeline pipelineStride32, Pipeline pipelineStride12, PipelineLayout layout);

/// <summary>Records point-light shadow casters for one cube face; see <see cref="ShadowSystem.RenderShadows{TState}"/>.</summary>
public delegate void ShadowDrawPoint<in TState>(
    TState state, CommandBuffer cb, Pipeline pipelineStride32, Pipeline pipelineStride12, PipelineLayout layout,
    Vector3 lightPosition, float lightRange);
