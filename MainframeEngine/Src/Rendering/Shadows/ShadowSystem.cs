using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkSampler = Silk.NET.Vulkan.Sampler;

namespace MainframeEngine;

/// <summary>The shadow descriptor set a lit pipeline binds: a real <see cref="ShadowSystem"/> or the "no shadows" fallback.</summary>
public interface IShadowDescriptors
{
    DescriptorSetLayout MainDescSetLayout { get; }

    /// <summary>The set to bind for the frame being recorded.</summary>
    DescriptorSet GetMainSet();
}

/// <summary>
/// Shadow maps for every light type: cascades for the primary directional light (a 2D array), one atlas for spot
/// lights and the other directional lights, and cube maps for point lights. Call
/// <see cref="RenderShadows{TState}(LightEnvironment, ICamera?, in Aabb, TState, ShadowCasterCull{TState}, ShadowCasterDraw{TState})"/>
/// once per frame before the main pass; lit pipelines then bind <see cref="GetMainSet"/> as their shadow set.
/// </summary>
/// <remarks>
/// <para>Planning (which light gets which map, cascade fitting, atlas packing) is CPU-only (<see cref="ShadowPlanner"/>).
/// Each sub-pass gets its own light view-projection in a per-frame-slot dynamic-offset uniform ring, so every recorded
/// pass keeps its matrix until the GPU executes it.</para>
/// <para>Maps are created on first use at the size the lights ask for and re-created when it changes; until then the
/// set points at 1×1 placeholders. Six samplers in all (cascade array, atlas, four cubes).</para>
/// </remarks>
public sealed unsafe class ShadowSystem : IDisposable, IShadowDescriptors
{
    // ── Limits ────────────────────────────────────────────────────────────────

    /// <summary>Shadowed directional lights: the first casts cascades, up to three more get atlas tiles.</summary>
    public const int MaxShadowDir = ShaderLimits.MaxShadowDirectional;

    /// <summary>Shadowed spot lights (atlas tiles): every spot light of the lights UBO.</summary>
    public const int MaxShadowSpot = ShaderLimits.MaxShadowSpot;

    /// <summary>Shadowed point lights (cube maps).</summary>
    public const int MaxShadowPoint = ShaderLimits.MaxShadowPoint;

    /// <summary>Cascades of the primary directional light.</summary>
    public const int MaxCascades = ShaderLimits.MaxShadowCascades;

    /// <summary>Shadow sub-passes per frame: 4 cascades + 11 atlas tiles + 4 × 6 cube faces (39).</summary>
    public const int MaxShadowPasses = ShadowPlanner.MaxPasses;

    /// <summary>Samplers the shadow set adds to the fragment stage: cascade array, atlas, cubes.</summary>
    public const int SamplerCount = 2 + MaxShadowPoint;

    // ── Fields ────────────────────────────────────────────────────────────────

    private readonly IVulkanContext _ctx;
    private readonly Format _depthFormat;
    private readonly bool _linearDepthFiltering;
    private readonly ShadowPlanner _planner = new();
    private bool _disposed;

    // Depth-only render pass (every map type; Undefined → attachment, the whole render area is cleared).
    private RenderPass _shadowRenderPass;

    // Per-object pipelines (Spine and other non-batched visuals): stride 32 (MeshVertex) and 12 (positions only).
    private Pipeline _pipe2D_S32, _pipe2D_S12;
    private Pipeline _pipePoint_S32, _pipePoint_S12;
    private PipelineLayout _layout2D;      // push: mat4 model; set0: light VP (dynamic UBO)
    private PipelineLayout _layoutPoint;   // push: mat4 model + vec4 lightPosRange; set0: light VP (dynamic UBO)
    private PipelineLayout _layout2DCutout, _layoutPointCutout; // + set1: the mesh material (cutout casters)

    // Light-VP ring: MaxFramesInFlight × MaxShadowPasses matrices, bound with a dynamic offset per pass.
    private readonly UniformRing _vpRing;
    private GpuBuffer _vpBuffer = null!;
    private nint _vpMapped;
    private DescriptorPool _vpPool;
    private DescriptorSetLayout _vpSetLayout;
    private DescriptorSet _vpSet;

    // Maps (shared by both frame slots: the barriers in RenderShadows order a frame's writes after the previous
    // frame's sampling on the same queue). Null until a light needs them.
    private GpuImage? _cascades;
    private readonly Framebuffer[] _cascadeFramebuffers = new Framebuffer[MaxCascades];
    private readonly ImageView[] _cascadeLayerViews = new ImageView[MaxCascades];
    private bool _cascadesNeedInit;
    private GpuImage? _atlas;
    private Framebuffer _atlasFramebuffer;
    private bool _atlasNeedsInit;
    private readonly GpuImage?[] _cubes = new GpuImage?[MaxShadowPoint];
    private readonly Framebuffer[] _cubeFramebuffers = new Framebuffer[MaxShadowPoint * 6];
    private readonly bool[] _cubeNeedsInit = new bool[MaxShadowPoint];
    private readonly ShadowPlaceholderMaps _placeholders;

    private readonly VkSampler _comparisonSampler;

    // Main-pass descriptor set, one per frame slot (rewritten when a map is re-created).
    private DescriptorPool _mainPool;
    private readonly DescriptorSet[] _mainSets = new DescriptorSet[IVulkanContext.MaxFramesInFlight];
    private readonly GpuBuffer[] _uniformBuffers = new GpuBuffer[IVulkanContext.MaxFramesInFlight];
    private readonly int[] _setVersion = new int[IVulkanContext.MaxFramesInFlight];
    private readonly ulong[] _uniformFrame = new ulong[IVulkanContext.MaxFramesInFlight];
    private int _mapsVersion = 1;

    private readonly Dictionary<int, Pipeline> _instancedPipelines = [];
    private DescriptorSetLayout _materialLayout;

    // ── Constructor ───────────────────────────────────────────────────────────

    public ShadowSystem(IVulkanContext ctx)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        _ctx = ctx;
        (_depthFormat, _linearDepthFiltering) = ChooseDepthFormat(ctx);

        ctx.Vk.GetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        _vpRing = new UniformRing((ulong)sizeof(Matrix4x4), MaxShadowPasses, IVulkanContext.MaxFramesInFlight,
            props.Limits.MinUniformBufferOffsetAlignment);
        MaxImageSize = (int)Math.Min(props.Limits.MaxImageDimension2D, props.Limits.MaxFramebufferWidth);

        CreateShadowRenderPass();
        CreateVpRingResources();
        CreateShadowPipelines();
        _placeholders = new ShadowPlaceholderMaps(ctx, _depthFormat);
        _comparisonSampler = CreateComparisonSampler(ctx, _linearDepthFiltering);
        CreateMainDescriptorResources();
    }

    // ── Settings & stats ──────────────────────────────────────────────────────

    /// <summary>Filtering of every shadow map (default <see cref="ShadowFilter.Poisson16"/>).</summary>
    public ShadowFilter Filter
    {
        get => _planner.Filter;
        set => _planner.Filter = value;
    }

    /// <summary>Radius of the PCF kernel in texels (default 1.5; clamped to [0.5, 8]).</summary>
    public float FilterRadius
    {
        get => _planner.FilterRadius;
        set => _planner.FilterRadius = Math.Clamp(value, 0.5f, 8f);
    }

    /// <summary>Tints the main view by cascade: red, green, blue, yellow (debug).</summary>
    public bool DebugCascades
    {
        get => _planner.DebugCascades;
        set => _planner.DebugCascades = value;
    }

    /// <summary>Snap cascades to the texel grid (default true). False shows the shimmering this prevents.</summary>
    public bool StableCascades
    {
        get => _planner.StableCascades;
        set => _planner.StableCascades = value;
    }

    /// <summary>Largest shadow atlas (default 4096, a power of two); the atlas grows to fit its tiles up to this size.</summary>
    public int MaxAtlasSize
    {
        get => _planner.MaxAtlasSize;
        set => _planner.MaxAtlasSize = (int)BitOperations.RoundUpToPowerOf2((uint)Math.Clamp(value, ShadowPlanner.MinAtlasSize, Math.Min(MaxImageSize, 16384)));
    }

    /// <summary>Constant depth bias of the caster rasterisation (default 1.25; the hardware's minimum resolvable units).</summary>
    public float DepthBiasConstant { get; set; } = 1.25f;

    /// <summary>Slope-scaled depth bias of the caster rasterisation (default 1.75). Point lights write distance and ignore it.</summary>
    public float DepthBiasSlope { get; set; } = 1.75f;

    /// <summary>The largest image side the device supports.</summary>
    public int MaxImageSize { get; }

    /// <summary>Shadow sub-passes planned in the last frame.</summary>
    public int PlannedPasses { get; private set; }

    /// <summary>Sub-passes recorded in the last frame (planned minus those without casters).</summary>
    public int RenderedPasses { get; private set; }

    /// <summary>CPU time of the last <c>RenderShadows</c> (planning, culling callbacks and recording), in milliseconds.</summary>
    public double LastCpuMilliseconds { get; private set; }

    /// <summary>Side of each cascade layer (0 while there are none).</summary>
    public int CascadeResolution => (int)(_cascades?.Width ?? 0);

    /// <summary>Side of the atlas (0 while there is none).</summary>
    public int AtlasSize => (int)(_atlas?.Width ?? 0);

    /// <summary>Times the atlas was packed (only when its tiles change).</summary>
    public int AtlasPackCount => _planner.AtlasPackCount;

    /// <summary>Bytes of shadow-map memory in use (cascades, atlas, cubes).</summary>
    public long MapMemoryBytes
    {
        get
        {
            long bytes = 0;
            if (_cascades is not null) bytes += (long)_cascades.Width * _cascades.Height * _cascades.ArrayLayers * 4;
            if (_atlas is not null) bytes += (long)_atlas.Width * _atlas.Height * 4;
            foreach (var cube in _cubes)
                if (cube is not null) bytes += (long)cube.Width * cube.Height * 6 * 4;
            return bytes;
        }
    }

    /// <summary>The passes planned for the current frame (after <c>RenderShadows</c>).</summary>
    public ReadOnlySpan<ShadowPass> Passes => _planner.Passes;

    /// <summary>Whether pass <paramref name="index"/> of the current frame was recorded.</summary>
    public bool PassRendered(int index) => _planner.HasCasters(index);

    internal ShadowPlanner Planner => _planner;

    /// <summary>A 2D view of cascade layer <paramref name="cascade"/> (debug display; default while there are no cascades).</summary>
    internal ImageView CascadeLayerView(int cascade) => _cascadeLayerViews[cascade];

    /// <summary>The atlas view (debug display; default while there is no atlas).</summary>
    internal ImageView AtlasView => _atlas?.View ?? default;

    private VkSampler _debugSampler;

    /// <summary>A plain (non-comparison) nearest sampler to display depth maps (created on first use).</summary>
    internal VkSampler DebugSampler
    {
        get
        {
            if (_debugSampler.Handle != 0)
                return _debugSampler;
            var info = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = Silk.NET.Vulkan.Filter.Nearest,
                MinFilter = Silk.NET.Vulkan.Filter.Nearest,
                AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge,
                AddressModeW = SamplerAddressMode.ClampToEdge,
                MipmapMode = SamplerMipmapMode.Nearest,
            };
            _ctx.Vk.CreateSampler(_ctx.Device, in info, null, out _debugSampler).Check("vkCreateSampler (shadow debug)");
            return _debugSampler;
        }
    }

    // ── Public API ────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the shadow descriptor set for the frame being recorded. In a frame without <c>RenderShadows</c> its
    /// uniforms are reset to "no shadows", so lit pipelines never sample stale maps.
    /// </summary>
    public DescriptorSet GetMainSet()
    {
        var slot = _ctx.FrameSlot;
        if (_ctx.FrameStarted)
        {
            if (_uniformFrame[slot] != _ctx.FrameNumber)
            {
                _uniformBuffers[slot].MappedSpan.Clear();
                _uniformFrame[slot] = _ctx.FrameNumber;
            }

            if (_setVersion[slot] != _mapsVersion)
                WriteSlotSet(slot);
        }

        return _mainSets[slot];
    }

    /// <summary>
    /// Plans and records the frame's shadow maps (call once per frame, with the command buffer open and no render pass
    /// active). <paramref name="cull"/> runs for every planned pass first — return false when nothing casts into it, and
    /// the pass is skipped and its map treated as lit; then <paramref name="draw"/> records the casters of every pass
    /// that renders. Use static lambdas: <paramref name="state"/> carries what they need (no allocations).
    /// </summary>
    /// <param name="camera">The main view: cascades and secondary directional maps are fitted to it (null: a fixed
    /// sphere around the origin).</param>
    /// <param name="casterBounds">World bounds of the bounded casters (may be empty): cascades pull their near plane back
    /// to include them.</param>
    public void RenderShadows<TState>(LightEnvironment lights, ICamera? camera, in Aabb casterBounds, TState state,
        ShadowCasterCull<TState> cull, ShadowCasterDraw<TState> draw)
    {
        ArgumentNullException.ThrowIfNull(lights);
        ArgumentNullException.ThrowIfNull(cull);
        ArgumentNullException.ThrowIfNull(draw);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_ctx.FrameStarted)
            return;

        var start = Stopwatch.GetTimestamp();
        var cb = _ctx.CurrentCommandBuffer;
        var frameSlot = _ctx.FrameSlot;

        _planner.Plan(lights, camera, casterBounds);
        var passes = _planner.Passes;
        for (var i = 0; i < passes.Length; i++)
            _planner.SetHasCasters(i, cull(state, passes[i]));
        _planner.ApplyCulling();

        EnsureMaps();

        // Which maps render this frame.
        var renderCascades = false;
        var renderAtlas = false;
        var rendered = 0;
        foreach (ref readonly var pass in passes)
        {
            if (!_planner.HasCasters(pass.Index))
                continue;
            rendered++;
            renderCascades |= pass.Kind == ShadowPassKind.Cascade;
            renderAtlas |= pass.Kind == ShadowPassKind.AtlasTile;
        }

        Span<bool> renderCube = stackalloc bool[MaxShadowPoint];
        for (var c = 0; c < MaxShadowPoint; c++)
            renderCube[c] = _planner.CubeRenders(c) && _cubes[c] is not null;

        TransitionMaps(cb, renderCascades, renderAtlas, renderCube, toWrite: true);

        foreach (ref readonly var pass in passes)
        {
            if (!_planner.HasCasters(pass.Index))
                continue;
            switch (pass.Kind)
            {
                case ShadowPassKind.Cascade:
                    BeginShadowPass(cb, _cascadeFramebuffers[pass.Slot], (uint)pass.Size);
                    RecordPass(cb, frameSlot, pass, state, draw);
                    _ctx.Vk.CmdEndRenderPass(cb);
                    break;
                case ShadowPassKind.CubeFace:
                    BeginShadowPass(cb, _cubeFramebuffers[pass.Slot * 6 + pass.Face], (uint)pass.Size);
                    RecordPass(cb, frameSlot, pass, state, draw);
                    _ctx.Vk.CmdEndRenderPass(cb);
                    break;
            }
        }

        // One render pass for the whole atlas (one clear, one store; each tile is a viewport): far cheaper on tilers
        // than a render pass per tile, which would load and store the whole atlas every time.
        if (renderAtlas)
        {
            BeginShadowPass(cb, _atlasFramebuffer, (uint)_atlas!.Width);
            foreach (ref readonly var pass in passes)
            {
                if (pass.Kind == ShadowPassKind.AtlasTile && _planner.HasCasters(pass.Index))
                    RecordPass(cb, frameSlot, pass, state, draw);
            }

            _ctx.Vk.CmdEndRenderPass(cb);
        }

        TransitionMaps(cb, renderCascades, renderAtlas, renderCube, toWrite: false);

        // This frame slot's uniforms and set (the slot's previous frame has completed).
        _uniformBuffers[frameSlot].Write(_planner.Uniforms);
        _uniformFrame[frameSlot] = _ctx.FrameNumber;
        if (_setVersion[frameSlot] != _mapsVersion)
            WriteSlotSet(frameSlot);

        PlannedPasses = passes.Length;
        RenderedPasses = rendered;
        LastCpuMilliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    /// <summary>
    /// Records shadows without camera fitting or culling: every planned pass calls <paramref name="draw2D"/> (cascades,
    /// atlas tiles) or <paramref name="drawPoint"/> (cube faces) with the per-object pipelines. For tree-less code; the
    /// render server uses the culling overload.
    /// </summary>
    public void RenderShadows<TState>(
        LightEnvironment lights,
        TState state,
        ShadowDraw2D<TState> draw2D,
        ShadowDrawPoint<TState> drawPoint)
    {
        ArgumentNullException.ThrowIfNull(draw2D);
        ArgumentNullException.ThrowIfNull(drawPoint);
        RenderShadows(lights, null, Aabb.Empty, (state, draw2D, drawPoint, this),
            static ((TState, ShadowDraw2D<TState>, ShadowDrawPoint<TState>, ShadowSystem) s, in ShadowPass pass) => true,
            static ((TState State, ShadowDraw2D<TState> Draw2D, ShadowDrawPoint<TState> DrawPoint, ShadowSystem System) s,
                CommandBuffer cb, in ShadowPass pass) =>
            {
                var sys = s.System;
                if (pass.IsPoint)
                    s.DrawPoint(s.State, cb, sys._pipePoint_S32, sys._pipePoint_S12, sys._layoutPoint, pass.LightPosition, pass.LightRange);
                else
                    s.Draw2D(s.State, cb, sys._pipe2D_S32, sys._pipe2D_S12, sys._layout2D);
            });
    }

    /// <summary>Convenience form of <see cref="RenderShadows{TState}(LightEnvironment, TState, ShadowDraw2D{TState}, ShadowDrawPoint{TState})"/>; allocates if the lambdas capture.</summary>
    public void RenderShadows(
        LightEnvironment lights,
        Action<CommandBuffer, Pipeline, Pipeline, PipelineLayout> draw2D,
        Action<CommandBuffer, Pipeline, Pipeline, PipelineLayout, Vector3, float> drawPoint)
    {
        RenderShadows(lights, (draw2D, drawPoint),
            static (s, cb, p32, p12, layout) => s.draw2D(cb, p32, p12, layout),
            static (s, cb, p32, p12, layout, lightPos, lightRange) => s.drawPoint(cb, p32, p12, layout, lightPos, lightRange));
    }

    /// <summary>Ring offset of one pass's matrix in one frame slot (exposed for tests).</summary>
    internal uint PassOffset(int frameSlot, int pass) => _vpRing.Offset(frameSlot, pass);

    /// <summary>The light view-projection stored for one pass (test hook: reads the mapped ring).</summary>
    internal Matrix4x4 ReadPassMatrix(int frameSlot, int pass) => *(Matrix4x4*)(_vpMapped + (nint)_vpRing.Offset(frameSlot, pass));

    private void RecordPass<TState>(CommandBuffer cb, int frameSlot, in ShadowPass pass, TState state, ShadowCasterDraw<TState> draw)
    {
        var vk = _ctx.Vk;
        var offset = _vpRing.Offset(frameSlot, pass.Index);
        *(Matrix4x4*)(_vpMapped + (nint)offset) = pass.ViewProjection;

        // Standard viewport (no Y flip): the shaders map NDC to UV with xy · 0.5 + 0.5.
        var viewport = new Viewport { X = pass.X, Y = pass.Y, Width = pass.Size, Height = pass.Size, MinDepth = 0f, MaxDepth = 1f };
        vk.CmdSetViewport(cb, 0, 1, &viewport);
        var scissor = new Rect2D(new Offset2D(pass.X, pass.Y), new Extent2D((uint)pass.Size, (uint)pass.Size));
        vk.CmdSetScissor(cb, 0, 1, &scissor);
        if (pass.IsPoint)
            vk.CmdSetDepthBias(cb, 0f, 0f, 0f); // linear distance is written by the fragment shader
        else
            vk.CmdSetDepthBias(cb, DepthBiasConstant, 0f, DepthBiasSlope);

        var vpSet = _vpSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, pass.IsPoint ? _layoutPoint : _layout2D, 0, 1, &vpSet, 1, &offset);
        draw(state, cb, pass);
    }

    // Begins a depth-only render pass over a whole framebuffer (cleared to far depth).
    private void BeginShadowPass(CommandBuffer cb, Framebuffer framebuffer, uint size)
    {
        var clear = new ClearValue { DepthStencil = new ClearDepthStencilValue { Depth = 1.0f, Stencil = 0 } };
        var info = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _shadowRenderPass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Extent = new Extent2D(size, size) },
            ClearValueCount = 1,
            PClearValues = &clear,
        };
        _ctx.Vk.CmdBeginRenderPass(cb, &info, SubpassContents.Inline);
    }

    // ── Maps ──────────────────────────────────────────────────────────────────

    // Creates or re-creates maps to the sizes planned this frame (the old ones go through the deletion queue).
    private void EnsureMaps()
    {
        var cascadeSize = Math.Min(_planner.CascadeResolution, MaxImageSize);
        if (cascadeSize > 0 && (_cascades is null || _cascades.Width != cascadeSize))
        {
            DestroyCascades();
            _cascades = GpuImage.Create(_ctx, new GpuImageDesc((uint)cascadeSize, (uint)cascadeSize, _depthFormat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit)
            {
                ArrayLayers = MaxCascades,
                ViewType = ImageViewType.Type2DArray,
            });
            for (var c = 0; c < MaxCascades; c++)
            {
                _cascadeLayerViews[c] = _cascades.CreateView(ImageViewType.Type2D, (uint)c, 1);
                _cascadeFramebuffers[c] = CreateDepthFramebuffer(_cascadeLayerViews[c], (uint)cascadeSize);
            }
            _cascadesNeedInit = true;
            _mapsVersion++;
        }

        var atlasSize = Math.Min(_planner.AtlasSize, MaxImageSize);
        if (atlasSize > 0 && (_atlas is null || _atlas.Width != atlasSize))
        {
            DestroyAtlas();
            _atlas = GpuImage.Create(_ctx, new GpuImageDesc((uint)atlasSize, (uint)atlasSize, _depthFormat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit));
            _atlasFramebuffer = CreateDepthFramebuffer(_atlas.View, (uint)atlasSize);
            _atlasNeedsInit = true;
            _mapsVersion++;
        }
        else if (atlasSize == 0 && _atlas is not null)
        {
            DestroyAtlas();
            _mapsVersion++;
        }

        for (var c = 0; c < MaxShadowPoint; c++)
        {
            var size = Math.Min(_planner.CubeResolutions[c], MaxImageSize);
            if (size == 0 || (_cubes[c] is { } existing && existing.Width == size))
                continue;
            DestroyCube(c);
            var cube = GpuImage.Create(_ctx, new GpuImageDesc((uint)size, (uint)size, _depthFormat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit)
            {
                ArrayLayers = 6,
                Flags = ImageCreateFlags.CreateCubeCompatibleBit,
                ViewType = ImageViewType.TypeCube,
            });
            for (var face = 0; face < 6; face++)
                _cubeFramebuffers[c * 6 + face] = CreateDepthFramebuffer(cube.CreateView(ImageViewType.Type2D, (uint)face, 1), (uint)size);
            _cubes[c] = cube;
            _cubeNeedsInit[c] = true;
            _mapsVersion++;
        }
    }

    /// <summary>
    /// Layout transitions around the passes. To write: maps that render this frame go from Undefined (their contents
    /// are cleared) to attachment, after the previous frame's sampling. To read: they return to read-only, and maps
    /// created this frame that did not render are initialised to read-only, so every map the set points at is valid.
    /// </summary>
    private void TransitionMaps(CommandBuffer cb, bool cascades, bool atlas, ReadOnlySpan<bool> cubes, bool toWrite)
    {
        var barriers = stackalloc ImageMemoryBarrier[2 + MaxShadowPoint];
        var count = 0;
        var aspect = VkHelpers.DepthBarrierAspects(_depthFormat);

        void Add(ImageMemoryBarrier* list, ref int n, Image image, uint layers, bool renders, ref bool needsInit, bool write, ImageAspectFlags aspects)
        {
            if (renders)
            {
                list[n++] = Barrier(image, layers, aspects,
                    write ? ImageLayout.Undefined : ImageLayout.DepthStencilAttachmentOptimal,
                    write ? ImageLayout.DepthStencilAttachmentOptimal : ImageLayout.DepthStencilReadOnlyOptimal,
                    write ? AccessFlags.ShaderReadBit : AccessFlags.DepthStencilAttachmentWriteBit,
                    write ? AccessFlags.DepthStencilAttachmentWriteBit | AccessFlags.DepthStencilAttachmentReadBit : AccessFlags.ShaderReadBit);
                needsInit = false;
            }
            else if (!write && needsInit)
            {
                list[n++] = Barrier(image, layers, aspects, ImageLayout.Undefined, ImageLayout.DepthStencilReadOnlyOptimal, 0, AccessFlags.ShaderReadBit);
                needsInit = false;
            }
        }

        if (_cascades is not null)
            Add(barriers, ref count, _cascades.Handle, MaxCascades, cascades, ref _cascadesNeedInit, toWrite, aspect);
        if (_atlas is not null)
            Add(barriers, ref count, _atlas.Handle, 1, atlas, ref _atlasNeedsInit, toWrite, aspect);
        for (var c = 0; c < MaxShadowPoint; c++)
            if (_cubes[c] is { } cube)
                Add(barriers, ref count, cube.Handle, 6, cubes[c], ref _cubeNeedsInit[c], toWrite, aspect);

        if (count == 0)
            return;
        var srcStage = toWrite
            ? PipelineStageFlags.FragmentShaderBit
            : PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit;
        var dstStage = toWrite
            ? PipelineStageFlags.EarlyFragmentTestsBit | PipelineStageFlags.LateFragmentTestsBit
            : PipelineStageFlags.FragmentShaderBit;
        _ctx.Vk.CmdPipelineBarrier(cb, srcStage, dstStage, 0, 0, null, 0, null, (uint)count, barriers);
    }

    private static ImageMemoryBarrier Barrier(Image image, uint layers, ImageAspectFlags aspect, ImageLayout oldLayout,
        ImageLayout newLayout, AccessFlags srcAccess, AccessFlags dstAccess) => new()
        {
            SType = StructureType.ImageMemoryBarrier,
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange { AspectMask = aspect, LevelCount = 1, LayerCount = layers },
        };

    private void DestroyCascades()
    {
        if (_cascades is null)
            return;
        for (var c = 0; c < MaxCascades; c++)
        {
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_cascadeFramebuffers[c]));
            _cascadeFramebuffers[c] = default;
            _cascadeLayerViews[c] = default; // owned by the image
        }

        _cascades.Dispose();
        _cascades = null;
        _cascadesNeedInit = false;
    }

    private void DestroyAtlas()
    {
        if (_atlas is null)
            return;
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_atlasFramebuffer));
        _atlasFramebuffer = default;
        _atlas.Dispose();
        _atlas = null;
        _atlasNeedsInit = false;
    }

    private void DestroyCube(int c)
    {
        if (_cubes[c] is not { } cube)
            return;
        for (var face = 0; face < 6; face++)
        {
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_cubeFramebuffers[c * 6 + face]));
            _cubeFramebuffers[c * 6 + face] = default;
        }

        cube.Dispose();
        _cubes[c] = null;
        _cubeNeedsInit[c] = false;
    }

    private Framebuffer CreateDepthFramebuffer(ImageView view, uint size)
    {
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _shadowRenderPass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = size,
            Height = size,
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out var framebuffer).Check("vkCreateFramebuffer (shadow)");
        return framebuffer;
    }

    // ── Shared with ShadowFallback ────────────────────────────────────────────

    /// <summary>
    /// Depth format for shadow maps: must support depth attachment + sampling; linear filtering (hardware 2×2 PCF
    /// through the comparison sampler) is preferred but optional. Depth-only formats come first (no stencil is used);
    /// combined formats are a last resort and their barriers name both aspects (<see cref="VkHelpers.DepthBarrierAspects"/>).
    /// </summary>
    internal static (Format Format, bool LinearFilter) ChooseDepthFormat(IVulkanContext ctx)
    {
        ReadOnlySpan<Format> candidates = [Format.D32Sfloat, Format.D16Unorm, Format.D32SfloatS8Uint, Format.D24UnormS8Uint];
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

    /// <summary>The comparison sampler of every shadow map (lit when the reference is less than the stored depth).</summary>
    internal static VkSampler CreateComparisonSampler(IVulkanContext ctx, bool linear)
    {
        var filter = linear ? Silk.NET.Vulkan.Filter.Linear : Silk.NET.Vulkan.Filter.Nearest;
        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = filter,
            MinFilter = filter,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            CompareEnable = true,
            CompareOp = CompareOp.Less,
            MipmapMode = SamplerMipmapMode.Nearest,
            BorderColor = BorderColor.FloatOpaqueWhite,
        };
        ctx.Vk.CreateSampler(ctx.Device, in info, null, out var sampler).Check("vkCreateSampler (shadow comparison)");
        return sampler;
    }

    /// <summary>
    /// The shadow set layout (<c>include/shadows.glsl</c>): b0 uniforms, b1 cascade array, b2 atlas, b3 point cubes.
    /// Every map uses the comparison sampler, baked in as an immutable sampler: Metal via MoltenVK reports
    /// mutableComparisonSamplers = false, which forbids writing compare-enabled samplers through vkUpdateDescriptorSets.
    /// </summary>
    internal static DescriptorSetLayout CreateMainSetLayout(IVulkanContext ctx, VkSampler comparisonSampler)
    {
        var samplers = stackalloc VkSampler[MaxShadowPoint];
        for (var i = 0; i < MaxShadowPoint; i++)
            samplers[i] = comparisonSampler;

        var bindings = stackalloc DescriptorSetLayoutBinding[]
        {
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit, PImmutableSamplers = samplers },
            new() { Binding = 2, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit, PImmutableSamplers = samplers },
            new() { Binding = 3, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = MaxShadowPoint, StageFlags = ShaderStageFlags.FragmentBit, PImmutableSamplers = samplers },
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 4,
            PBindings = bindings,
        };
        ctx.Vk.CreateDescriptorSetLayout(ctx.Device, in info, null, out var layout).Check("vkCreateDescriptorSetLayout (shadow set)");
        return layout;
    }

    /// <summary>Writes the four bindings of a shadow set: uniforms, cascade array view, atlas view, cube views.</summary>
    internal static void WriteMainSet(IVulkanContext ctx, DescriptorSet set, Silk.NET.Vulkan.Buffer uniforms,
        ImageView cascades, ImageView atlas, ReadOnlySpan<ImageView> cubes)
    {
        var bufferInfo = new DescriptorBufferInfo { Buffer = uniforms, Offset = 0, Range = (ulong)ShadowUniforms.Size };
        // Samplers stay null: every map binding uses the layout's immutable comparison sampler.
        var images = stackalloc DescriptorImageInfo[2 + MaxShadowPoint];
        images[0] = new DescriptorImageInfo { ImageView = cascades, ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };
        images[1] = new DescriptorImageInfo { ImageView = atlas, ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };
        for (var i = 0; i < MaxShadowPoint; i++)
            images[2 + i] = new DescriptorImageInfo { ImageView = cubes[i], ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal };

        var writes = stackalloc WriteDescriptorSet[4];
        writes[0] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bufferInfo };
        writes[1] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = images };
        writes[2] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 2, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = images + 1 };
        writes[3] = new() { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 3, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = MaxShadowPoint, PImageInfo = images + 2 };
        ctx.Vk.UpdateDescriptorSets(ctx.Device, 4, writes, 0, null);
    }

    /// <summary>Descriptor pool for <paramref name="setCount"/> shadow sets.</summary>
    internal static DescriptorPool CreateMainPool(IVulkanContext ctx, uint setCount)
    {
        var poolSizes = stackalloc DescriptorPoolSize[]
        {
            new() { Type = DescriptorType.UniformBuffer, DescriptorCount = setCount },
            new() { Type = DescriptorType.CombinedImageSampler, DescriptorCount = SamplerCount * setCount },
        };
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            MaxSets = setCount,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
        };
        ctx.Vk.CreateDescriptorPool(ctx.Device, in info, null, out var pool).Check("vkCreateDescriptorPool (shadow set)");
        return pool;
    }

    private void WriteSlotSet(int slot)
    {
        Span<ImageView> cubes = stackalloc ImageView[MaxShadowPoint];
        for (var c = 0; c < MaxShadowPoint; c++)
            cubes[c] = _cubes[c]?.View ?? _placeholders.Cube;
        WriteMainSet(_ctx, _mainSets[slot], _uniformBuffers[slot].Handle,
            _cascades?.View ?? _placeholders.Array, _atlas?.View ?? _placeholders.Map2D, cubes);
        _setVersion[slot] = _mapsVersion;
    }

    // ── Resource creation ─────────────────────────────────────────────────────

    private void CreateShadowRenderPass()
    {
        var depth = new AttachmentDescription
        {
            Format = _depthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.DepthStencilAttachmentOptimal, // transitioned (and discarded) by RenderShadows
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };
        var depthRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            PDepthStencilAttachment = &depthRef,
        };
        var info = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &depth,
            SubpassCount = 1,
            PSubpasses = &subpass,
        };
        _ctx.Vk.CreateRenderPass(_ctx.Device, in info, null, out _shadowRenderPass).Check("vkCreateRenderPass (shadow)");
    }

    private void CreateVpRingResources()
    {
        var vk = _ctx.Vk;
        var device = _ctx.Device;

        _vpBuffer = GpuBuffer.Create(_ctx, _vpRing.Size, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
        _vpMapped = _vpBuffer.MappedPointer;
        _vpBuffer.MappedSpan.Clear();

        // Binding 0 = dynamic UBO (vertex stage): one matrix per sub-pass, selected by the dynamic offset.
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBufferDynamic,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
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
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _vpPool,
            DescriptorSetCount = 1,
            PSetLayouts = &setLayout,
        };
        vk.AllocateDescriptorSets(device, in allocInfo, out _vpSet).Check("vkAllocateDescriptorSets (light VP)");

        var bufferInfo = _vpBuffer.Descriptor(0, (ulong)sizeof(Matrix4x4));
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _vpSet,
            DstBinding = 0,
            DescriptorType = DescriptorType.UniformBufferDynamic,
            DescriptorCount = 1,
            PBufferInfo = &bufferInfo,
        };
        vk.UpdateDescriptorSets(device, 1, &write, 0, null);
    }

    private PipelineLayout CreateCasterLayout(bool point, DescriptorSetLayout material, string what)
    {
        var sets = stackalloc DescriptorSetLayout[2];
        sets[0] = _vpSetLayout;
        sets[1] = material;
        var push = point
            ? new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, Offset = 0, Size = 80 }
            : new PushConstantRange { StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = 64 };
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = material.Handle == 0 ? 1u : 2u,
            PSetLayouts = sets,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &push,
        };
        _ctx.Vk.CreatePipelineLayout(_ctx.Device, in info, null, out var layout).Check($"vkCreatePipelineLayout ({what})");
        return layout;
    }

    private void CreateShadowPipelines()
    {
        _layout2D = CreateCasterLayout(point: false, default, "shadow 2D");
        _layoutPoint = CreateCasterLayout(point: true, default, "shadow point");

        _pipe2D_S32 = BuildPipeline(CasterShaders.Plain2D, CullMode.Back, mirrored: false, _layout2D, ObjectBindings(32), ObjectAttributes, "shadow");
        _pipe2D_S12 = BuildPipeline(CasterShaders.Plain2D, CullMode.Back, mirrored: false, _layout2D, ObjectBindings(12), ObjectAttributes, "shadow");
        _pipePoint_S32 = BuildPipeline(CasterShaders.PlainPoint, CullMode.Back, mirrored: false, _layoutPoint, ObjectBindings(32), ObjectAttributes, "shadow");
        _pipePoint_S12 = BuildPipeline(CasterShaders.PlainPoint, CullMode.Back, mirrored: false, _layoutPoint, ObjectBindings(12), ObjectAttributes, "shadow");
    }

    private static VertexInputBindingDescription[] ObjectBindings(uint stride) =>
        [new VertexInputBindingDescription { Binding = 0, Stride = stride, InputRate = VertexInputRate.Vertex }];

    private static readonly VertexInputAttributeDescription[] ObjectAttributes =
        [new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 }];

    private enum CasterShaders
    {
        Plain2D,
        PlainPoint,
        Instanced2D,
        InstancedPoint,
        Cutout2D,
        CutoutPoint,
    }

    private static (string Vertex, string Fragment) ShaderPaths(CasterShaders shaders) => shaders switch
    {
        CasterShaders.Plain2D => ("Shaders/Shadows/Shadow2D.vk.vert.spv", "Shaders/Shadows/Shadow2D.vk.frag.spv"),
        CasterShaders.PlainPoint => ("Shaders/Shadows/ShadowPoint.vk.vert.spv", "Shaders/Shadows/ShadowPoint.vk.frag.spv"),
        CasterShaders.Instanced2D => ("Shaders/Shadows/Shadow2DInstanced.vk.vert.spv", "Shaders/Shadows/Shadow2D.vk.frag.spv"),
        CasterShaders.InstancedPoint => ("Shaders/Shadows/ShadowPointInstanced.vk.vert.spv", "Shaders/Shadows/ShadowPoint.vk.frag.spv"),
        CasterShaders.Cutout2D => ("Shaders/Shadows/Shadow2DCutoutInstanced.vk.vert.spv", "Shaders/Shadows/ShadowCutout.vk.frag.spv"),
        _ => ("Shaders/Shadows/ShadowPointCutoutInstanced.vk.vert.spv", "Shaders/Shadows/ShadowPointCutout.vk.frag.spv"),
    };

    // Geometry is authored counter-clockwise (front faces CCW, right-handed). The main pass flips Y with a
    // negative-height viewport, which keeps CCW = front; shadow passes use a standard viewport, which mirrors the
    // winding, so geometric front faces (facing the light) arrive clockwise: FrontFace = Clockwise, back faces culled,
    // mirrored instances counter-clockwise. Depth bias is dynamic (set per pass).
    private Pipeline BuildPipeline(CasterShaders shaders, CullMode cull, bool mirrored, PipelineLayout layout,
        VertexInputBindingDescription[] bindings, VertexInputAttributeDescription[] attributes, string what)
    {
        var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var (vertexPath, fragmentPath) = ShaderPaths(shaders);
            var stages = stackalloc PipelineShaderStageCreateInfo[]
            {
                new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = _ctx.Shaders.Get(vertexPath), PName = entryPoint },
                new() { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = _ctx.Shaders.Get(fragmentPath), PName = entryPoint },
            };

            fixed (VertexInputBindingDescription* pBindings = bindings)
            fixed (VertexInputAttributeDescription* pAttributes = attributes)
            {
                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                    VertexBindingDescriptionCount = (uint)bindings.Length,
                    PVertexBindingDescriptions = pBindings,
                    VertexAttributeDescriptionCount = (uint)attributes.Length,
                    PVertexAttributeDescriptions = pAttributes,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
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
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    LineWidth = 1f,
                    CullMode = cull switch
                    {
                        CullMode.Front => CullModeFlags.FrontBit,
                        CullMode.Disabled => CullModeFlags.None,
                        _ => CullModeFlags.BackBit,
                    },
                    FrontFace = mirrored ? FrontFace.CounterClockwise : FrontFace.Clockwise,
                    DepthBiasEnable = true,
                };
                var multisampling = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var depthStencil = new PipelineDepthStencilStateCreateInfo
                {
                    SType = StructureType.PipelineDepthStencilStateCreateInfo,
                    DepthTestEnable = true,
                    DepthWriteEnable = true,
                    DepthCompareOp = CompareOp.Less,
                };
                var dynamicStates = stackalloc[] { DynamicState.Viewport, DynamicState.Scissor, DynamicState.DepthBias };
                var dynamicState = new PipelineDynamicStateCreateInfo
                {
                    SType = StructureType.PipelineDynamicStateCreateInfo,
                    DynamicStateCount = 3,
                    PDynamicStates = dynamicStates,
                };
                var info = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = stages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterizer,
                    PMultisampleState = &multisampling,
                    PDepthStencilState = &depthStencil,
                    PDynamicState = &dynamicState,
                    Layout = layout,
                    RenderPass = _shadowRenderPass,
                    Subpass = 0,
                };
                return _ctx.Pipelines.CreateGraphicsPipeline(info, what);
            }
        }
        finally
        {
            SilkMarshal.Free((nint)entryPoint); // modules belong to the shared ShaderModuleCache
        }
    }

    private void CreateMainDescriptorResources()
    {
        const int slots = IVulkanContext.MaxFramesInFlight;
        for (var i = 0; i < slots; i++)
        {
            _uniformBuffers[i] = GpuBuffer.Create(_ctx, (ulong)ShadowUniforms.Size, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
            _uniformBuffers[i].MappedSpan.Clear();
        }

        MainDescSetLayout = CreateMainSetLayout(_ctx, _comparisonSampler);
        _mainPool = CreateMainPool(_ctx, slots);
        var layouts = stackalloc DescriptorSetLayout[slots];
        for (var i = 0; i < slots; i++)
            layouts[i] = MainDescSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _mainPool,
            DescriptorSetCount = slots,
            PSetLayouts = layouts,
        };
        fixed (DescriptorSet* p = _mainSets)
            _ctx.Vk.AllocateDescriptorSets(_ctx.Device, in allocInfo, p).Check("vkAllocateDescriptorSets (shadow set)");
        for (var slot = 0; slot < slots; slot++)
            WriteSlotSet(slot);
    }

    public DescriptorSetLayout MainDescSetLayout { get; private set; }

    // ── Pipeline accessors ────────────────────────────────────────────────────

    /// <summary>The per-object directional/spot depth pipeline for a vertex stride (12 or 32 bytes; position first).</summary>
    public Pipeline GetShadow2DPipeline(uint strideBytes) => strideBytes == 12 ? _pipe2D_S12 : _pipe2D_S32;

    /// <summary>The per-object point-light depth pipeline for a vertex stride.</summary>
    public Pipeline GetShadowPointPipeline(uint strideBytes) => strideBytes == 12 ? _pipePoint_S12 : _pipePoint_S32;

    public PipelineLayout Shadow2DLayout => _layout2D;
    public PipelineLayout ShadowPointLayout => _layoutPoint;

    /// <summary>
    /// Sets the mesh material set layout used by the cutout caster pipelines (their set 1). Called once by the mesh
    /// renderer.
    /// </summary>
    internal void SetMaterialSetLayout(DescriptorSetLayout materialLayout)
    {
        if (_materialLayout.Handle == materialLayout.Handle)
            return;
        if (_materialLayout.Handle != 0)
            throw new InvalidOperationException("The material set layout of the cutout casters is already set.");
        _materialLayout = materialLayout;
        _layout2DCutout = CreateCasterLayout(point: false, materialLayout, "shadow 2D cutout");
        _layoutPointCutout = CreateCasterLayout(point: true, materialLayout, "shadow point cutout");
    }

    /// <summary>The layout of the cutout caster pipelines (set 0 light VP, set 1 material).</summary>
    internal PipelineLayout CutoutLayout(bool point) => point ? _layoutPointCutout : _layout2DCutout;

    /// <summary>
    /// The depth pipeline for instanced mesh batches: 2D or point, culling per the material (double-sided casters cull
    /// nothing), front faces flipped for mirrored instances, and for cutout materials an alpha test against the
    /// material (set 1 of <see cref="CutoutLayout"/>). Plain casters use the <see cref="Shadow2DLayout"/>/
    /// <see cref="ShadowPointLayout"/> layouts; point batches push the light position and range at offset 64. Built on
    /// first use.
    /// </summary>
    internal Pipeline GetInstancedCasterPipeline(bool point, CullMode cull, bool mirrored, bool cutout = false)
    {
        var key = (point ? 1 : 0) | ((int)cull << 1) | (mirrored ? 8 : 0) | (cutout ? 16 : 0);
        if (_instancedPipelines.TryGetValue(key, out var pipeline))
            return pipeline;
        if (cutout && _materialLayout.Handle == 0)
            throw new InvalidOperationException("Cutout casters need the material set layout (SetMaterialSetLayout).");

        pipeline = cutout
            ? BuildPipeline(point ? CasterShaders.CutoutPoint : CasterShaders.Cutout2D, cull, mirrored, CutoutLayout(point),
                VertexLayouts.ShadowInstancedBindings, VertexLayouts.ShadowCutoutInstancedAttributes, "shadow (instanced, cutout)")
            : BuildPipeline(point ? CasterShaders.InstancedPoint : CasterShaders.Instanced2D, cull, mirrored, point ? _layoutPoint : _layout2D,
                VertexLayouts.ShadowInstancedBindings, VertexLayouts.ShadowInstancedAttributes, "shadow (instanced)");
        _instancedPipelines[key] = pipeline;
        return pipeline;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    /// <summary>Releases every GPU object through the deletion queue (no device wait).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var deletions = _ctx.Deletions;
        DestroyCascades();
        DestroyAtlas();
        for (var c = 0; c < MaxShadowPoint; c++)
            DestroyCube(c);
        _placeholders.Dispose();

        // Descriptor resources (before the sampler baked into the layout)
        deletions.Enqueue(GpuDeletion.Of(_mainPool));
        deletions.Enqueue(GpuDeletion.Of(MainDescSetLayout));
        foreach (var buffer in _uniformBuffers)
            buffer.Dispose();
        deletions.Enqueue(GpuDeletion.Of(_comparisonSampler));
        if (_debugSampler.Handle != 0)
            deletions.Enqueue(GpuDeletion.Of(_debugSampler));

        deletions.Enqueue(GpuDeletion.Of(_pipe2D_S32));
        deletions.Enqueue(GpuDeletion.Of(_pipe2D_S12));
        deletions.Enqueue(GpuDeletion.Of(_pipePoint_S32));
        deletions.Enqueue(GpuDeletion.Of(_pipePoint_S12));
        foreach (var pipeline in _instancedPipelines.Values)
            deletions.Enqueue(GpuDeletion.Of(pipeline));
        _instancedPipelines.Clear();
        deletions.Enqueue(GpuDeletion.Of(_layout2D));
        deletions.Enqueue(GpuDeletion.Of(_layoutPoint));
        if (_layout2DCutout.Handle != 0)
            deletions.Enqueue(GpuDeletion.Of(_layout2DCutout));
        if (_layoutPointCutout.Handle != 0)
            deletions.Enqueue(GpuDeletion.Of(_layoutPointCutout));

        _vpBuffer.Dispose();
        deletions.Enqueue(GpuDeletion.Of(_vpPool));
        deletions.Enqueue(GpuDeletion.Of(_vpSetLayout));
        deletions.Enqueue(GpuDeletion.Of(_shadowRenderPass));
    }
}

/// <summary>1×1 shadow maps cleared to far depth (cascade array, 2D, cube) for set bindings without a real map.</summary>
internal sealed class ShadowPlaceholderMaps : IDisposable
{
    private readonly GpuImage _array, _map2D, _cube;
    private bool _disposed;

    public ShadowPlaceholderMaps(IVulkanContext ctx, Format format)
    {
        // Depth-attachment usage is required for the DEPTH_STENCIL_READ_ONLY_OPTIMAL layout the set expects.
        const ImageUsageFlags usage = ImageUsageFlags.SampledBit | ImageUsageFlags.TransferDstBit | ImageUsageFlags.DepthStencilAttachmentBit;
        _array = GpuImage.Create(ctx, new GpuImageDesc(1, 1, format, usage) { ViewType = ImageViewType.Type2DArray });
        _map2D = GpuImage.Create(ctx, new GpuImageDesc(1, 1, format, usage));
        _cube = GpuImage.Create(ctx, new GpuImageDesc(1, 1, format, usage)
        {
            ArrayLayers = 6,
            Flags = ImageCreateFlags.CreateCubeCompatibleBit,
            ViewType = ImageViewType.TypeCube,
        });

        // Cleared to far depth (and left read-only) by the upload queue at the start of the next frame.
        var aspect = VkHelpers.DepthBarrierAspects(format);
        ctx.Uploads.ClearDepthToFar(_array.Handle, aspect, 1);
        ctx.Uploads.ClearDepthToFar(_map2D.Handle, aspect, 1);
        ctx.Uploads.ClearDepthToFar(_cube.Handle, aspect, 6);
        ctx.Uploads.FlushIfRecording();
    }

    public ImageView Array => _array.View;
    public ImageView Map2D => _map2D.View;
    public ImageView Cube => _cube.View;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _array.Dispose();
        _map2D.Dispose();
        _cube.Dispose();
    }
}

/// <summary>Records directional/spot shadow casters with the per-object pipelines (legacy form).</summary>
public delegate void ShadowDraw2D<in TState>(
    TState state, CommandBuffer cb, Pipeline pipelineStride32, Pipeline pipelineStride12, PipelineLayout layout);

/// <summary>Records point-light shadow casters for one cube face with the per-object pipelines (legacy form).</summary>
public delegate void ShadowDrawPoint<in TState>(
    TState state, CommandBuffer cb, Pipeline pipelineStride32, Pipeline pipelineStride12, PipelineLayout layout,
    Vector3 lightPosition, float lightRange);
