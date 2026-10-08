using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// Draws the <see cref="CanvasServer"/>'s <see cref="CanvasFrame"/> on the engine's Vulkan device (ADR 0111): into an
/// offscreen <c>R8G8B8A8_UNORM</c> layer sized to the swapchain, blending in gamma space with Godot's blend states, then
/// composited (premultiplied) onto the swapchain right after the tonemap, below the game UI and the dev overlay. Textures are
/// uploaded in the frame step (<see cref="PrepareTextures"/>) so their uploads are recorded at the next frame's start,
/// before the pass that samples them.
/// </summary>
public sealed unsafe class VulkanCanvasRenderer : IOverlayRenderer, IDisposable
{
    internal const Format LayerFormat = Format.R8G8B8A8Unorm;
    private const int PushSize = 112;
    private const int SetsPerPool = 256;
    private const int EvictAfterFrames = 600;

    private static readonly nint EntryPoint = SilkMarshal.StringToPtr("main");

    private readonly IVulkanContext _ctx;
    private readonly CanvasServer _server;
    private readonly Sampler[] _samplers = new Sampler[6];
    private readonly Dictionary<Texture2D, TextureEntry> _textures = new(ReferenceEqualityComparer.Instance);
    private readonly List<Texture2D> _evict = [];
    private readonly Dictionary<PipelineKey, Pipeline> _pipelines = [];
    private readonly List<DescriptorPool> _pools = [];
    private readonly FrameBuffers[] _frames = new FrameBuffers[IVulkanContext.MaxFramesInFlight];
    private RenderPass _pass;
    private DescriptorSetLayout _textureSetLayout;
    private PipelineLayout _layout;
    private PipelineLayout _compositeLayout;
    private Pipeline _compositePipeline;
    private GpuTexture _white = null!;
    private DescriptorSet[] _whiteSets = new DescriptorSet[6];
    private GpuImage? _target;
    private Framebuffer _framebuffer;
    private DescriptorSet _targetSet;
    private Extent2D _targetExtent;
    private bool _hasContent;
    private bool _disposed;

    private sealed class TextureEntry
    {
        public GpuTexture Texture = null!;
        public int Version;
        public readonly DescriptorSet[] Sets = new DescriptorSet[6];
        public ulong LastUsed;
    }

    private struct FrameBuffers
    {
        public GpuBuffer? Vertices;
        public GpuBuffer? Indices;
    }

    private readonly record struct PipelineKey(CanvasBlendMode Blend, CanvasPrimitive Primitive);

    // One canvas shader: its material set layout (binding 0 = dynamic UBO, 1… = samplers), pipeline layout and pipelines.
    private sealed class ShaderEntry
    {
        public DescriptorSetLayout SetLayout;
        public PipelineLayout Layout;
        public DescriptorPool Pool;
        public readonly Dictionary<PipelineKey, Pipeline> Pipelines = [];
    }

    // One ShaderMaterial's set: the uniform ring buffer + its sampler textures (reallocated when they change).
    private sealed class MaterialEntry
    {
        public DescriptorSet Set;
        public DescriptorPool Pool;
        public Texture2D?[] Textures = [];
        public GpuTexture?[] Uploads = [];   // the GPU textures the set points at (a texture whose pixels changed re-uploads)
        public GpuBuffer? Ring;
        public ulong LastUsed;
    }

    private readonly Dictionary<Shader, ShaderEntry> _shaders = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ShaderMaterial, MaterialEntry> _materials = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ShaderMaterial, uint> _materialOffsets = new(ReferenceEqualityComparer.Instance);
    private GpuBuffer? _ring;
    private ulong _ringSlotSize;
    private ulong _ringUsed;
    private const ulong RingAlignment = 256;

    [StructLayout(LayoutKind.Sequential)]
    private struct CanvasPush
    {
        public Vector4 ModelAxes;
        public Vector4 ModelOrigin;      // origin, texture pixel size
        public Vector4 CanvasAxes;
        public Vector4 CanvasOrigin;     // origin, time, flags (bits)
        public Vector4 Screen;
        public Vector4 CanvasModulation;
        public Vector4 Lights; // x: light mask (uint bits)
    }

    // canvas_lights.slang's block: per light two matrix rows, colour, flags (std140, 64 bytes).
    private struct LightGpuData
    {
        public Vector4 MatrixX;
        public Vector4 MatrixY;
        public Vector4 Color;
        public uint Blend;
#pragma warning disable CS0649 // std140 padding, never written
        public uint Pad0, Pad1, Pad2;
#pragma warning restore CS0649
    }

    private DescriptorSetLayout _lightSetLayout;
    private DescriptorPool _lightPool;
    private readonly DescriptorSet[] _lightSets = new DescriptorSet[IVulkanContext.MaxFramesInFlight];
    private readonly GpuBuffer?[] _lightBuffers = new GpuBuffer?[IVulkanContext.MaxFramesInFlight];

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositePush
    {
        public Vector2 UvOffset;
        public Vector2 UvScale;
        public float Factor;
        public uint Linearize;
    }

    public VulkanCanvasRenderer(IVulkanContext ctx, CanvasServer server)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(server);
        _ctx = ctx;
        _server = server;
        CreateResources();
        ctx.AddOverlayRenderer(this, OverlayOrder.Canvas);
    }

    /// <summary>Draw calls of the last frame.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>Textures currently uploaded for the canvas.</summary>
    public int TextureCount => _textures.Count;

    // ── Frame step ───────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Uploads (or re-uploads after a change) every texture the frame uses and evicts textures unused for a while. Called
    /// by the canvas server after building the frame, outside any command buffer.
    /// </summary>
    public void PrepareTextures(CanvasFrame frame)
    {
        if (_disposed)
            return;
        var now = _ctx.FrameNumber;
        foreach (var light in frame.Lights)
            Prepare(light.Texture, now);
        var batches = frame.Batches;
        for (var i = 0; i < batches.Count; i++)
        {
            if (batches[i].Texture is { } texture)
                Prepare(texture, now);
            if (batches[i].Material is ShaderMaterial { Shader.Program: { } program } material)
                foreach (var u in program.Uniforms)
                    if (u.IsSampler && material.GetShaderParameter(u.Name) is Texture2D samplerTexture)
                        Prepare(samplerTexture, now);
        }

        if (now % 120 == 0)
        {
            foreach (var (texture, entry) in _textures)
                if (now - entry.LastUsed > EvictAfterFrames)
                    _evict.Add(texture);
            foreach (var texture in _evict)
            {
                Release(_textures[texture]);
                _textures.Remove(texture);
            }

            _evict.Clear();

            foreach (var (viewport, target) in _subTargets)
                if (!viewport.IsInsideTree || now - target.LastUsed > EvictAfterFrames)
                    _subEvict.Add(viewport);
            foreach (var viewport in _subEvict)
            {
                ReleaseSubTarget(_subTargets[viewport]);
                _subTargets.Remove(viewport);
            }

            _subEvict.Clear();

            foreach (var (owner, target) in _groupTargets)
                if (!owner.IsInsideTree || now - target.LastUsed > EvictAfterFrames)
                    _groupEvict.Add(owner);
            foreach (var owner in _groupEvict)
            {
                ReleaseSubTarget(_groupTargets[owner]);
                _groupTargets.Remove(owner);
            }

            _groupEvict.Clear();
        }
    }

    private void Prepare(Texture2D texture, ulong now)
    {
        if (texture.Viewport is not null || texture.ClipGroup is not null)
            return; // drawn by the GPU each frame (EnsureSubTarget / EnsureGroupTarget), nothing to upload
        if (!_textures.TryGetValue(texture, out var entry) || entry.Version != texture.Version)
        {
            if (entry is not null)
                Release(entry);
            entry = Upload(texture);
            _textures[texture] = entry;
        }

        entry.LastUsed = now;
    }

    private TextureEntry Upload(Texture2D texture)
    {
        var (rgba, width, height) = texture.DecodePixels();
        var gpu = GpuTexture.Create2D(_ctx, (uint)width, (uint)height, rgba, TextureColorSpace.Linear, TextureSampling.LinearClamp);
        return new TextureEntry { Texture = gpu, Version = texture.Version };
    }

    private void Release(TextureEntry entry)
    {
        foreach (var set in entry.Sets)
            if (set.Handle != 0)
                FreeSet(set);
        entry.Texture.Dispose(); // deletion queue
    }

    // ── IOverlayRenderer ─────────────────────────────────────────────────────────────────────────────────────

    void IOverlayRenderer.RecordOffscreen(CommandBuffer cb)
    {
        _hasContent = false;
        DrawCalls = 0;
        if (_disposed)
            return;
        var frame = _server.Frame;
        var extent = _ctx.SwapchainExtent;
        if (extent.Width == 0 || extent.Height == 0)
            return;
        var passes = frame.Passes;
        var hasSubPasses = passes.Count > 1;
        if (frame.Batches.Count == 0 && frame.ClearColor is null && !hasSubPasses)
            return;

        var slot = _ctx.FrameSlot;
        var (vertexBuffer, indexBuffer) = Upload(slot, frame);
        BeginMaterialRing(slot, frame);
        UpdateLights(slot, frame);
        Barrier(cb);

        // 2D sub-viewports first (their targets are sampled by later passes), then the main canvas layer.
        var now = _ctx.FrameNumber;
        for (var p = 0; p < passes.Count; p++)
        {
            var pass = passes[p];
            if (pass.ClipOwner is { } owner)
            {
                // A clip-children group: the owner and its subtree on their own transparent target the canvas's size.
                var group = EnsureGroupTarget(owner, new Extent2D((uint)pass.Size.X, (uint)pass.Size.Y), now);
                RecordPass(cb, frame, pass, group.Framebuffer, group.Extent, Vector4.Zero, vertexBuffer, indexBuffer);
                Barrier(cb); // the main pass samples this target
                continue;
            }

            if (pass.Viewport is { } viewport)
            {
                var target = EnsureSubTarget(viewport, now);
                RecordPass(cb, frame, pass, target.Framebuffer, target.Extent, pass.ClearColor ?? Vector4.Zero, vertexBuffer, indexBuffer);
                Barrier(cb); // later passes sample this target
                continue;
            }

            if (pass.BatchCount == 0 && frame.ClearColor is null)
                continue;
            EnsureTarget(extent);
            RecordPass(cb, frame, pass, _framebuffer, extent, frame.ClearColor ?? Vector4.Zero, vertexBuffer, indexBuffer);
            Barrier(cb); // the overlay pass samples the layer
            _hasContent = true;
        }
    }

    private void RecordPass(CommandBuffer cb, CanvasFrame frame, in CanvasPass pass, Framebuffer framebuffer, Extent2D extent, Vector4 clear,
        GpuBuffer? vertexBuffer, GpuBuffer? indexBuffer)
    {
        var vk = _ctx.Vk;
        var clearValue = new ClearValue { Color = new ClearColorValue(clear.X, clear.Y, clear.Z, clear.W) };
        var begin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo,
            RenderPass = _pass,
            Framebuffer = framebuffer,
            RenderArea = new Rect2D { Extent = extent },
            ClearValueCount = 1,
            PClearValues = &clearValue,
        };
        vk.CmdBeginRenderPass(cb, &begin, SubpassContents.Inline);
        PipelineBuilder.SetViewport(vk, cb, extent, flipY: false);

        if (pass.BatchCount > 0 && vertexBuffer is not null && indexBuffer is not null)
        {
            var vb = vertexBuffer.Handle;
            ulong offset = 0;
            vk.CmdBindVertexBuffers(cb, 0, 1, &vb, &offset);
            vk.CmdBindIndexBuffer(cb, indexBuffer.Handle, 0, IndexType.Uint32);

            var screen = new Vector4(2f / extent.Width, 2f / extent.Height, -1f, -1f);
            var time = _server.Time;
            var boundLayout = _layout;
            var boundPipeline = default(Pipeline);
            var boundSet = default(DescriptorSet);
            var lightsLayout = default(PipelineLayout);
            var lightSet = _lightSets[_ctx.FrameSlot];
            var batches = frame.Batches;
            for (var i = pass.FirstBatch; i < pass.FirstBatch + pass.BatchCount; i++)
            {
                var batch = batches[i];
                var shaderMaterial = batch.Material as ShaderMaterial;
                var shader = shaderMaterial?.Shader is { Program: not null, VertexSpvPath: not null } s && File.Exists(s.VertexSpvPath) ? s : null;
                Pipeline pipeline;
                PipelineLayout layout;
                if (shader is not null)
                {
                    var entry = GetShader(shader);
                    layout = entry.Layout;
                    pipeline = GetShaderPipeline(shader, entry, new PipelineKey(batch.Blend, batch.Primitive));
                }
                else
                {
                    layout = _layout;
                    pipeline = GetPipeline(new PipelineKey(batch.Blend, batch.Primitive));
                }

                if (pipeline.Handle != boundPipeline.Handle)
                {
                    vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                    boundPipeline = pipeline;
                }

                if (layout.Handle != boundLayout.Handle)
                {
                    boundLayout = layout;
                    boundSet = default; // set 0 must be rebound against the new layout
                }

                if (layout.Handle != lightsLayout.Handle)
                {
                    // The frame's light block: set 1 of the default layout, set 2 of a shader's (after its material set).
                    vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, layout, shader is null ? 1u : 2u, 1, &lightSet, 0, null);
                    lightsLayout = layout;
                }

                var set = TextureSet(batch.Texture, batch.Sampler, out var pixelSize);
                if (set.Handle != boundSet.Handle)
                {
                    vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, layout, 0, 1, &set, 0, null);
                    boundSet = set;
                }

                if (shader is not null)
                {
                    var (materialSet, materialOffset) = MaterialSet(shaderMaterial!, shader);
                    var dynamicOffset = materialOffset;
                    vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, layout, 1, 1, &materialSet, 1, &dynamicOffset);
                }

                var unshaded = batch.Material is CanvasItemMaterial { LightMode: CanvasLightMode.Unshaded } || shader is { Unshaded: true };
                var flags = (unshaded ? 1u : 0u) | (batch.Blend == CanvasBlendMode.Atop ? 4u : 0u);   // canvas.slang CANVAS_FLAG_*
                var push = new CanvasPush
                {
                    ModelAxes = new Vector4(batch.Model.X, batch.Model.Y.X, batch.Model.Y.Y),
                    ModelOrigin = new Vector4(batch.Model.Origin, pixelSize.X, pixelSize.Y),
                    CanvasAxes = new Vector4(batch.CanvasTransform.X, batch.CanvasTransform.Y.X, batch.CanvasTransform.Y.Y),
                    CanvasOrigin = new Vector4(batch.CanvasTransform.Origin, time, BitConverter.UInt32BitsToSingle(flags)),
                    Screen = screen,
                    CanvasModulation = batch.CanvasModulate,
                    Lights = new Vector4(BitConverter.UInt32BitsToSingle(batch.LightMask), 0, 0, 0),
                };
                vk.CmdPushConstants(cb, layout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, PushSize, &push);
                vk.CmdDrawIndexed(cb, (uint)batch.IndexCount, 1, (uint)batch.FirstIndex, 0, 0);
                DrawCalls++;
            }
        }

        vk.CmdEndRenderPass(cb);
    }

    // The frame-slot light block and textures (unused slots: zero colour, the white texture).
    private void UpdateLights(int slot, CanvasFrame frame)
    {
        if (_lightSets[slot].Handle == 0)
        {
            var layout = _lightSetLayout;
            var info = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _lightPool,
                DescriptorSetCount = 1,
                PSetLayouts = &layout,
            };
            _ctx.Vk.AllocateDescriptorSets(_ctx.Device, in info, out _lightSets[slot]).Check("vkAllocateDescriptorSets (canvas lights)");
            _lightBuffers[slot] = GpuBuffer.Create(_ctx, (ulong)(sizeof(LightGpuData) * CanvasFrame.MaxLights), BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
            var bufferInfo = new DescriptorBufferInfo { Buffer = _lightBuffers[slot]!.Handle, Offset = 0, Range = (ulong)(sizeof(LightGpuData) * CanvasFrame.MaxLights) };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = _lightSets[slot],
                DstBinding = 0,
                DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1,
                PBufferInfo = &bufferInfo,
            };
            _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
        }

        var data = stackalloc LightGpuData[CanvasFrame.MaxLights];
        var images = stackalloc DescriptorImageInfo[CanvasFrame.MaxLights];
        var lights = frame.Lights;
        for (var i = 0; i < CanvasFrame.MaxLights; i++)
        {
            var view = _white.View;
            if (i < lights.Count)
            {
                var l = lights[i];
                data[i] = new LightGpuData { MatrixX = l.MatrixX, MatrixY = l.MatrixY, Color = l.Color, Blend = l.Blend };
                if (_textures.TryGetValue(l.Texture, out var entry))
                    view = entry.Texture.View;
            }
            else
            {
                data[i] = default;
            }

            images[i] = new DescriptorImageInfo { Sampler = _samplers[(int)CanvasSampler.LinearClamp], ImageView = view, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        }

        _lightBuffers[slot]!.Write(new ReadOnlySpan<LightGpuData>(data, CanvasFrame.MaxLights));
        var imageWrite = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _lightSets[slot],
            DstBinding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = CanvasFrame.MaxLights,
            PImageInfo = images,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &imageWrite, 0, null);
    }

    // ── 2D sub-viewport targets ──────────────────────────────────────────────────────────────────────────────

    private sealed class SubTarget
    {
        public GpuImage Image = null!;
        public Framebuffer Framebuffer;
        public Extent2D Extent;
        public readonly DescriptorSet[] Sets = new DescriptorSet[6];
        public ulong LastUsed;
        public ulong Generation;
    }

    private ulong _subGeneration;

    /// <summary>
    /// The 2D sub-viewport's colour target as the game UI samples it (<see cref="UiServer.RegisterTexture(string, SubViewport)"/>):
    /// premultiplied gamma values in <c>SHADER_READ_ONLY_OPTIMAL</c>, drawn before the UI each frame; empty before its first pass.
    /// </summary>
    internal UiTextureView ViewOf(SubViewport viewport) =>
        _subTargets.TryGetValue(viewport, out var t)
            ? new UiTextureView(t.Image.View, _samplers[(int)CanvasSampler.LinearClamp], ImageLayout.ShaderReadOnlyOptimal, t.Extent.Width, t.Extent.Height, t.Generation)
            : default;

    private readonly Dictionary<SubViewport, SubTarget> _subTargets = new(ReferenceEqualityComparer.Instance);
    private readonly List<SubViewport> _subEvict = [];

    /// <summary>
    /// Drops the targets keyed by scene objects (2D sub-viewports, clip-children owners) so nothing here keeps them alive
    /// (the editor, before it unloads game code); they are recreated on the next frame that draws them.
    /// </summary>
    public void ReleaseSceneTargets()
    {
        foreach (var target in _subTargets.Values)
            ReleaseSubTarget(target);
        _subTargets.Clear();
        foreach (var target in _groupTargets.Values)
            ReleaseSubTarget(target);
        _groupTargets.Clear();
    }

    /// <summary>2D sub-viewport targets alive (tests, diagnostics).</summary>
    public int SubViewportTargetCount => _subTargets.Count;

    private readonly Dictionary<CanvasItem, SubTarget> _groupTargets = new(ReferenceEqualityComparer.Instance);
    private readonly List<CanvasItem> _groupEvict = [];

    private SubTarget EnsureGroupTarget(CanvasItem owner, Extent2D extent, ulong now)
    {
        if (_groupTargets.TryGetValue(owner, out var target) && target.Extent.Width == extent.Width && target.Extent.Height == extent.Height)
        {
            target.LastUsed = now;
            return target;
        }

        if (target is not null)
            ReleaseSubTarget(target);
        target = CreateSubTarget(extent, now);
        _groupTargets[owner] = target;
        return target;
    }

    private SubTarget EnsureSubTarget(SubViewport viewport, ulong now)
    {
        var extent = new Extent2D((uint)Math.Max(1, viewport.Width), (uint)Math.Max(1, viewport.Height));
        if (_subTargets.TryGetValue(viewport, out var target) && target.Extent.Width == extent.Width && target.Extent.Height == extent.Height)
        {
            target.LastUsed = now;
            return target;
        }

        if (target is not null)
            ReleaseSubTarget(target);
        target = CreateSubTarget(extent, now);
        _subTargets[viewport] = target;
        return target;
    }

    private SubTarget CreateSubTarget(Extent2D extent, ulong now)
    {
        var target = new SubTarget { Extent = extent, LastUsed = now, Generation = ++_subGeneration };
        target.Image = GpuImage.Create(_ctx, new GpuImageDesc(extent.Width, extent.Height, LayerFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit));
        var view = target.Image.View;
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _pass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = extent.Width,
            Height = extent.Height,
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out target.Framebuffer).Check("vkCreateFramebuffer (canvas sub-viewport)");
        return target;
    }

    private void ReleaseSubTarget(SubTarget target)
    {
        foreach (var set in target.Sets)
            if (set.Handle != 0)
                FreeSet(set);
        _ctx.Deletions.Enqueue(GpuDeletion.Of(target.Framebuffer));
        target.Image.Dispose(); // deletion queue
    }

    void IOverlayRenderer.RecordOverlay(CommandBuffer cb)
    {
        if (!_hasContent)
            return;
        var vk = _ctx.Vk;
        vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, _compositePipeline);
        PipelineBuilder.SetViewport(vk, cb, _ctx.SwapchainExtent, flipY: false);
        var set = _targetSet;
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _compositeLayout, 0, 1, &set, 0, null);
        var push = new CompositePush { UvScale = Vector2.One, Factor = 1f, Linearize = _ctx.OverlayEncodesSrgb ? 1u : 0u };
        vk.CmdPushConstants(cb, _compositeLayout, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, 0, (uint)sizeof(CompositePush), &push);
        vk.CmdDraw(cb, 3, 1, 0, 0);
    }

    // ── Per-frame geometry ───────────────────────────────────────────────────────────────────────────────────

    private (GpuBuffer? Vertices, GpuBuffer? Indices) Upload(int slot, CanvasFrame frame)
    {
        ref var buffers = ref _frames[slot];
        var vertexBytes = (ulong)(frame.VertexCount * CanvasVertex.SizeInBytes);
        var indexBytes = (ulong)(frame.IndexCount * sizeof(uint));
        if (vertexBytes == 0 || indexBytes == 0)
            return (null, null);
        if (buffers.Vertices is null || buffers.Vertices.Size < vertexBytes)
        {
            buffers.Vertices?.Dispose();
            buffers.Vertices = GpuBuffer.Create(_ctx, Grow(vertexBytes), BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
        }

        if (buffers.Indices is null || buffers.Indices.Size < indexBytes)
        {
            buffers.Indices?.Dispose();
            buffers.Indices = GpuBuffer.Create(_ctx, Grow(indexBytes), BufferUsageFlags.IndexBufferBit, GpuMemoryUsage.Dynamic);
        }

        buffers.Vertices.Write(frame.Vertices);
        buffers.Indices.Write(frame.Indices);
        return (buffers.Vertices, buffers.Indices);
    }

    private static ulong Grow(ulong bytes) => Math.Max(64 * 1024, (ulong)BitOperations.RoundUpToPowerOf2(bytes));

    // ── Textures and descriptor sets ─────────────────────────────────────────────────────────────────────────

    private DescriptorSet TextureSet(Texture2D? texture, CanvasSampler sampler, out Vector2 pixelSize)
    {
        var s = (int)sampler;
        if (texture?.ClipGroup is { } owner)
        {
            if (_groupTargets.TryGetValue(owner, out var group))
            {
                pixelSize = new Vector2(1f / group.Extent.Width, 1f / group.Extent.Height);
                if (group.Sets[s].Handle == 0)
                    group.Sets[s] = AllocateSet(group.Image.View, _samplers[s]);
                return group.Sets[s];
            }
        }
        else if (texture?.Viewport is { } viewport)
        {
            if (_subTargets.TryGetValue(viewport, out var target))
            {
                target.LastUsed = _ctx.FrameNumber; // a view that stopped updating keeps its image while it is sampled
                pixelSize = new Vector2(1f / target.Extent.Width, 1f / target.Extent.Height);
                if (target.Sets[s].Handle == 0)
                    target.Sets[s] = AllocateSet(target.Image.View, _samplers[s]);
                return target.Sets[s];
            }
        }
        else if (texture is not null && _textures.TryGetValue(texture, out var entry))
        {
            pixelSize = new Vector2(1f / entry.Texture.Width, 1f / entry.Texture.Height);
            if (entry.Sets[s].Handle == 0)
                entry.Sets[s] = AllocateSet(entry.Texture.View, _samplers[s]);
            return entry.Sets[s];
        }

        pixelSize = Vector2.One;
        if (_whiteSets[s].Handle == 0)
            _whiteSets[s] = AllocateSet(_white.View, _samplers[s]);
        return _whiteSets[s];
    }

    private DescriptorSet AllocateSet(ImageView view, Sampler sampler)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (_pools.Count > 0)
            {
                var layout = _textureSetLayout;
                var info = new DescriptorSetAllocateInfo
                {
                    SType = StructureType.DescriptorSetAllocateInfo,
                    DescriptorPool = _pools[^1],
                    DescriptorSetCount = 1,
                    PSetLayouts = &layout,
                };
                var result = _ctx.Vk.AllocateDescriptorSets(_ctx.Device, in info, out var set);
                if (result == Result.Success)
                {
                    PipelineBuilder.WriteImage(_ctx, set, 0, new DescriptorImageInfo
                    {
                        Sampler = sampler,
                        ImageView = view,
                        ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
                    });
                    _setPools[set.Handle] = _pools[^1];
                    return set;
                }

                if (result is not (Result.ErrorOutOfPoolMemory or Result.ErrorFragmentedPool))
                    result.Check("vkAllocateDescriptorSets (canvas)");
            }

            var size = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = SetsPerPool };
            var poolInfo = new DescriptorPoolCreateInfo
            {
                SType = StructureType.DescriptorPoolCreateInfo,
                Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
                MaxSets = SetsPerPool,
                PoolSizeCount = 1,
                PPoolSizes = &size,
            };
            _ctx.Vk.CreateDescriptorPool(_ctx.Device, in poolInfo, null, out var pool).Check("vkCreateDescriptorPool (canvas)");
            _pools.Add(pool);
        }

        throw new VulkanException("[Canvas] Could not allocate a texture descriptor set.");
    }

    private readonly Dictionary<ulong, DescriptorPool> _setPools = [];

    private void FreeSet(DescriptorSet set)
    {
        if (_setPools.Remove(set.Handle, out var pool))
            _ctx.Deletions.Enqueue(GpuDeletion.Of(pool, set));
    }

    // ── Canvas shaders and materials ─────────────────────────────────────────────────────────────────────────

    private ShaderEntry GetShader(Shader shader)
    {
        if (_shaders.TryGetValue(shader, out var entry))
            return entry;
        var program = shader.Program!;
        var bindings = new List<DescriptorSetLayoutBinding>
        {
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBufferDynamic, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit },
        };
        foreach (var u in program.Uniforms)
            if (u.IsSampler)
                bindings.Add(new DescriptorSetLayoutBinding { Binding = (uint)u.Binding, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit });
        entry = new ShaderEntry();
        entry.SetLayout = PipelineBuilder.CreateSetLayout(_ctx, bindings.ToArray(), "canvas material");
        entry.Layout = PipelineBuilder.CreateLayout(_ctx, [_textureSetLayout, entry.SetLayout, _lightSetLayout], PushSize, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, "canvas shader");
        var samplers = Math.Max(1, program.SamplerCount);
        // Sets are freed one by one when a material's textures change: the pool needs FREE_DESCRIPTOR_SET.
        var sizes = stackalloc DescriptorPoolSize[2];
        sizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBufferDynamic, DescriptorCount = 64 };
        sizes[1] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)(64 * samplers) };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            Flags = DescriptorPoolCreateFlags.FreeDescriptorSetBit,
            MaxSets = 64,
            PoolSizeCount = 2,
            PPoolSizes = sizes,
        };
        _ctx.Vk.CreateDescriptorPool(_ctx.Device, in poolInfo, null, out entry.Pool).Check("vkCreateDescriptorPool (canvas materials)");
        _shaders[shader] = entry;
        return entry;
    }

    private Pipeline GetShaderPipeline(Shader shader, ShaderEntry entry, PipelineKey key)
    {
        if (entry.Pipelines.TryGetValue(key, out var pipeline))
            return pipeline;
        var topology = key.Primitive == CanvasPrimitive.Lines ? PrimitiveTopology.LineList : PrimitiveTopology.TriangleList;
        pipeline = BuildPipeline(_pass, entry.Layout, shader.VertexSpvPath!, shader.FragmentSpvPath!, geometry: true, key.Blend, topology,
            $"canvas shader {Path.GetFileName(shader.ResourcePath ?? shader.VertexSpvPath)}");
        entry.Pipelines[key] = pipeline;
        return pipeline;
    }

    // Material uniforms live in one host-visible ring (every frame slot's region), written once per material per frame.
    private void BeginMaterialRing(int slot, CanvasFrame frame)
    {
        _materialOffsets.Clear();
        _ringUsed = 0;
        _ringSlot = slot;
        // Size the ring for every distinct material of the frame up front: it cannot grow once offsets are handed out.
        ulong needed = 0;
        var batches = frame.Batches;
        for (var i = 0; i < batches.Count; i++)
            if (batches[i].Material is ShaderMaterial { Shader.Program: { } program } material && _materialOffsets.TryAdd(material, 0))
                needed += (ulong)Math.Max(16, program.UniformBlockSize) + RingAlignment;
        _materialOffsets.Clear();
        if (needed > 0)
            EnsureRing(needed);
    }

    private int _ringSlot;

    private (DescriptorSet Set, uint Offset) MaterialSet(ShaderMaterial material, Shader shader)
    {
        var program = shader.Program!;
        var blockSize = (ulong)Math.Max(16, program.UniformBlockSize);
        if (!_materialOffsets.TryGetValue(material, out var offset32))
        {
            var aligned = (_ringUsed + RingAlignment - 1) / RingAlignment * RingAlignment;
            EnsureRing(aligned + blockSize);
            var offset = (ulong)_ringSlot * _ringSlotSize + aligned;
            Span<byte> block = stackalloc byte[(int)blockSize];
            shader.WriteUniformBlock(material.Parameters, block);
            _ring!.Write<byte>(block, offset);
            _ringUsed = aligned + blockSize;
            offset32 = (uint)offset;
            _materialOffsets[material] = offset32;
        }

        // The set: the ring (whole slot range per dynamic offset) + the material's textures.
        var textures = new Texture2D?[program.SamplerCount];
        var t = 0;
        foreach (var u in program.Uniforms)
            if (u.IsSampler)
                textures[t++] = material.GetShaderParameter(u.Name) as Texture2D;
        if (!_materials.TryGetValue(material, out var entry) || !ReferenceEquals(entry.Ring, _ring) || !SameTextures(entry, textures))
        {
            if (entry is not null && entry.Set.Handle != 0)
                _ctx.Deletions.Enqueue(GpuDeletion.Of(entry.Pool, entry.Set));
            entry = new MaterialEntry { Textures = textures, Ring = _ring, Uploads = textures.Select(UploadOf).ToArray() };
            var shaderEntry = GetShader(shader);
            entry.Pool = shaderEntry.Pool;
            entry.Set = PipelineBuilder.AllocateSet(_ctx, shaderEntry.Pool, shaderEntry.SetLayout, "canvas material");
            var bufferInfo = new DescriptorBufferInfo { Buffer = _ring!.Handle, Offset = 0, Range = blockSize };
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet,
                DstSet = entry.Set,
                DstBinding = 0,
                DescriptorType = DescriptorType.UniformBufferDynamic,
                DescriptorCount = 1,
                PBufferInfo = &bufferInfo,
            };
            _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
            t = 0;
            foreach (var u in program.Uniforms)
            {
                if (!u.IsSampler)
                    continue;
                var texture = textures[t++];
                var sampler = _samplers[(int)CanvasFrame.SamplerFor(u.Filter == CanvasTextureFilter.ParentNode ? CanvasTextureFilter.Linear : u.Filter,
                    u.Repeat == CanvasTextureRepeat.ParentNode ? CanvasTextureRepeat.Disabled : u.Repeat)];
                var view = texture is not null && _textures.TryGetValue(texture, out var te) ? te.Texture.View : _white.View;
                PipelineBuilder.WriteImage(_ctx, entry.Set, (uint)u.Binding, new DescriptorImageInfo { Sampler = sampler, ImageView = view, ImageLayout = ImageLayout.ShaderReadOnlyOptimal });
            }

            _materials[material] = entry;
        }

        entry.LastUsed = _ctx.FrameNumber;
        return (entry.Set, offset32);
    }

    private GpuTexture? UploadOf(Texture2D? texture) => texture is not null && _textures.TryGetValue(texture, out var e) ? e.Texture : null;

    /// <summary>The set still points at the material's textures as uploaded now (same textures, none re-uploaded since).</summary>
    private bool SameTextures(MaterialEntry entry, Texture2D?[] textures)
    {
        if (entry.Textures.Length != textures.Length)
            return false;
        for (var i = 0; i < textures.Length; i++)
            if (!ReferenceEquals(entry.Textures[i], textures[i]) || !ReferenceEquals(entry.Uploads[i], UploadOf(textures[i])))
                return false;
        return true;
    }

    private void EnsureRing(ulong neededPerSlot)
    {
        if (_ring is not null && neededPerSlot <= _ringSlotSize)
            return;
        // Growing mid-frame would invalidate offsets already recorded this frame; size generously and grow between frames.
        var size = Math.Max(64 * 1024UL, (ulong)BitOperations.RoundUpToPowerOf2(neededPerSlot * 2));
        _ring?.Dispose(); // deletion queue: frames in flight keep it until they complete
        _ringSlotSize = size;
        _ring = GpuBuffer.Create(_ctx, size * IVulkanContext.MaxFramesInFlight, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
    }

    // ── Target ───────────────────────────────────────────────────────────────────────────────────────────────

    private void EnsureTarget(Extent2D extent)
    {
        if (_target is not null && extent.Width == _targetExtent.Width && extent.Height == _targetExtent.Height)
            return;
        DestroyTarget();
        _targetExtent = extent;
        _target = GpuImage.Create(_ctx, new GpuImageDesc(extent.Width, extent.Height, LayerFormat,
            ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit | ImageUsageFlags.TransferSrcBit));
        var view = _target.View;
        var info = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo,
            RenderPass = _pass,
            AttachmentCount = 1,
            PAttachments = &view,
            Width = extent.Width,
            Height = extent.Height,
            Layers = 1,
        };
        _ctx.Vk.CreateFramebuffer(_ctx.Device, in info, null, out _framebuffer).Check("vkCreateFramebuffer (canvas)");
        _targetSet = AllocateSet(_target.View, _samplers[(int)CanvasSampler.LinearClamp]);
    }

    private void DestroyTarget()
    {
        if (_target is null)
            return;
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_framebuffer));
        FreeSet(_targetSet);
        _target.Dispose();
        _target = null;
        _framebuffer = default;
        _targetSet = default;
    }

    // ── Resources ────────────────────────────────────────────────────────────────────────────────────────────

    private void CreateResources()
    {
        var attachment = new AttachmentDescription
        {
            Format = LayerFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
        };
        var colorRef = new AttachmentReference(0, ImageLayout.ColorAttachmentOptimal);
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorRef,
        };
        const PipelineStageFlags stages = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit;
        var deps = stackalloc SubpassDependency[2];
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = stages,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit,
            DstStageMask = stages,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.ColorAttachmentReadBit,
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0,
            DstSubpass = Vk.SubpassExternal,
            SrcStageMask = stages,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstStageMask = stages,
            DstAccessMask = AccessFlags.ShaderReadBit,
        };
        var passInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 1,
            PAttachments = &attachment,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 2,
            PDependencies = deps,
        };
        _ctx.Vk.CreateRenderPass(_ctx.Device, in passInfo, null, out _pass).Check("vkCreateRenderPass (canvas)");

        Span<(Filter Filter, SamplerAddressMode Mode)> samplers =
        [
            (Filter.Linear, SamplerAddressMode.ClampToEdge),
            (Filter.Nearest, SamplerAddressMode.ClampToEdge),
            (Filter.Linear, SamplerAddressMode.Repeat),
            (Filter.Nearest, SamplerAddressMode.Repeat),
            (Filter.Linear, SamplerAddressMode.MirroredRepeat),
            (Filter.Nearest, SamplerAddressMode.MirroredRepeat),
        ];
        for (var i = 0; i < samplers.Length; i++)
        {
            var info = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = samplers[i].Filter,
                MinFilter = samplers[i].Filter,
                MipmapMode = SamplerMipmapMode.Nearest,
                AddressModeU = samplers[i].Mode,
                AddressModeV = samplers[i].Mode,
                AddressModeW = samplers[i].Mode,
                MaxLod = 0f,
            };
            _ctx.Vk.CreateSampler(_ctx.Device, in info, null, out _samplers[i]).Check("vkCreateSampler (canvas)");
        }

        _textureSetLayout = PipelineBuilder.CreateSetLayout(_ctx,
            [new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit }],
            "canvas texture");
        _lightSetLayout = PipelineBuilder.CreateSetLayout(_ctx,
            [
                new DescriptorSetLayoutBinding { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
                new DescriptorSetLayoutBinding { Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = CanvasFrame.MaxLights, StageFlags = ShaderStageFlags.FragmentBit },
            ],
            "canvas lights");
        _lightPool = PipelineBuilder.CreatePool(_ctx, IVulkanContext.MaxFramesInFlight,
            [
                new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = IVulkanContext.MaxFramesInFlight },
                new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = IVulkanContext.MaxFramesInFlight * CanvasFrame.MaxLights },
            ],
            "canvas lights");
        _layout = PipelineBuilder.CreateLayout(_ctx, [_textureSetLayout, _lightSetLayout], PushSize, ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, "canvas");
        _compositeLayout = PipelineBuilder.CreateLayout(_ctx, [_textureSetLayout], (uint)sizeof(CompositePush),
            ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit, "canvas composite");
        _white = GpuTexture.Create2D(_ctx, 1, 1, [255, 255, 255, 255], TextureColorSpace.Linear, TextureSampling.NearestClamp);
        _compositePipeline = BuildPipeline(_ctx.OverlayRenderPass, _compositeLayout, "Shaders/UI/UiFullscreen.vk.vert.spv",
            "Shaders/UI/UiPassthrough.vk.frag.spv", geometry: false, CanvasBlendMode.PremultAlpha, PrimitiveTopology.TriangleList, "canvas composite");
    }

    private Pipeline GetPipeline(PipelineKey key)
    {
        if (_pipelines.TryGetValue(key, out var pipeline))
            return pipeline;
        var topology = key.Primitive == CanvasPrimitive.Lines ? PrimitiveTopology.LineList : PrimitiveTopology.TriangleList;
        pipeline = BuildPipeline(_pass, _layout, "Shaders/Canvas/Canvas.vk.vert.spv", "Shaders/Canvas/Canvas.vk.frag.spv", geometry: true,
            key.Blend, topology, $"canvas {key.Blend} {key.Primitive}");
        _pipelines[key] = pipeline;
        return pipeline;
    }

    private Pipeline BuildPipeline(RenderPass pass, PipelineLayout layout, string vertexSpv, string fragmentSpv, bool geometry,
        CanvasBlendMode blend, PrimitiveTopology topology, string what)
    {
        var stages = stackalloc PipelineShaderStageCreateInfo[2];
        stages[0] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.VertexBit,
            Module = _ctx.Shaders.Get(vertexSpv),
            PName = (byte*)EntryPoint,
        };
        stages[1] = new PipelineShaderStageCreateInfo
        {
            SType = StructureType.PipelineShaderStageCreateInfo,
            Stage = ShaderStageFlags.FragmentBit,
            Module = _ctx.Shaders.Get(fragmentSpv),
            PName = (byte*)EntryPoint,
        };

        var binding = new VertexInputBindingDescription(0, CanvasVertex.SizeInBytes, VertexInputRate.Vertex);
        var attributes = stackalloc VertexInputAttributeDescription[3];
        attributes[0] = new VertexInputAttributeDescription(0, 0, Format.R32G32Sfloat, 0);
        attributes[1] = new VertexInputAttributeDescription(1, 0, Format.R32G32Sfloat, 8);
        attributes[2] = new VertexInputAttributeDescription(2, 0, Format.R32G32B32A32Sfloat, 16);
        var vertexInput = new PipelineVertexInputStateCreateInfo
        {
            SType = StructureType.PipelineVertexInputStateCreateInfo,
            VertexBindingDescriptionCount = geometry ? 1u : 0u,
            PVertexBindingDescriptions = geometry ? &binding : null,
            VertexAttributeDescriptionCount = geometry ? 3u : 0u,
            PVertexAttributeDescriptions = geometry ? attributes : null,
        };
        var inputAssembly = new PipelineInputAssemblyStateCreateInfo
        {
            SType = StructureType.PipelineInputAssemblyStateCreateInfo,
            Topology = topology,
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
            CullMode = CullModeFlags.None,
            FrontFace = FrontFace.CounterClockwise,
        };
        var multisampling = new PipelineMultisampleStateCreateInfo
        {
            SType = StructureType.PipelineMultisampleStateCreateInfo,
            RasterizationSamples = SampleCountFlags.Count1Bit,
        };
        var depthStencil = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo };
        var blendAttachment = BlendState(blend);
        var colorBlend = new PipelineColorBlendStateCreateInfo
        {
            SType = StructureType.PipelineColorBlendStateCreateInfo,
            AttachmentCount = 1,
            PAttachments = &blendAttachment,
        };
        var dynamicStates = stackalloc DynamicState[] { DynamicState.Viewport, DynamicState.Scissor };
        var dynamicState = new PipelineDynamicStateCreateInfo
        {
            SType = StructureType.PipelineDynamicStateCreateInfo,
            DynamicStateCount = 2,
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
            PColorBlendState = &colorBlend,
            PDynamicState = &dynamicState,
            Layout = layout,
            RenderPass = pass,
            Subpass = 0,
        };
        return _ctx.Pipelines.CreateGraphicsPipeline(info, what);
    }

    /// <summary>Godot's canvas blend states (<c>MaterialStorage::ShaderData::blend_mode_to_blend_attachment</c>).</summary>
    internal static PipelineColorBlendAttachmentState BlendState(CanvasBlendMode mode)
    {
        const ColorComponentFlags rgba = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit;
        var s = new PipelineColorBlendAttachmentState { BlendEnable = true, ColorWriteMask = rgba, ColorBlendOp = BlendOp.Add, AlphaBlendOp = BlendOp.Add };
        switch (mode)
        {
            case CanvasBlendMode.Add:
                s.SrcColorBlendFactor = BlendFactor.SrcAlpha;
                s.DstColorBlendFactor = BlendFactor.One;
                s.SrcAlphaBlendFactor = BlendFactor.SrcAlpha;
                s.DstAlphaBlendFactor = BlendFactor.One;
                break;
            case CanvasBlendMode.Sub:
                s.ColorBlendOp = BlendOp.ReverseSubtract;
                s.AlphaBlendOp = BlendOp.ReverseSubtract;
                s.SrcColorBlendFactor = BlendFactor.SrcAlpha;
                s.DstColorBlendFactor = BlendFactor.One;
                s.SrcAlphaBlendFactor = BlendFactor.SrcAlpha;
                s.DstAlphaBlendFactor = BlendFactor.One;
                break;
            case CanvasBlendMode.Mul:
                s.SrcColorBlendFactor = BlendFactor.DstColor;
                s.DstColorBlendFactor = BlendFactor.Zero;
                s.SrcAlphaBlendFactor = BlendFactor.DstAlpha;
                s.DstAlphaBlendFactor = BlendFactor.Zero;
                break;
            case CanvasBlendMode.PremultAlpha:
                s.SrcColorBlendFactor = BlendFactor.One;
                s.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;
                s.SrcAlphaBlendFactor = BlendFactor.One;
                s.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
                break;
            case CanvasBlendMode.Atop:
                s.SrcColorBlendFactor = BlendFactor.DstAlpha;          // the shader outputs premultiplied colour (CANVAS_FLAG_PREMULTIPLY)
                s.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;
                s.SrcAlphaBlendFactor = BlendFactor.Zero;
                s.DstAlphaBlendFactor = BlendFactor.One;
                break;
            case CanvasBlendMode.Disabled:
                s.BlendEnable = false;
                break;
            default: // Mix
                s.SrcColorBlendFactor = BlendFactor.SrcAlpha;
                s.DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha;
                s.SrcAlphaBlendFactor = BlendFactor.One;
                s.DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha;
                break;
        }

        return s;
    }

    private void Barrier(CommandBuffer cb)
    {
        const PipelineStageFlags stages = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.FragmentShaderBit;
        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
            DstAccessMask = AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit | AccessFlags.ShaderReadBit,
        };
        _ctx.Vk.CmdPipelineBarrier(cb, stages, stages, 0, 1, &barrier, 0, null, 0, null);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _ctx.RemoveOverlayRenderer(this);
        foreach (var entry in _textures.Values)
            Release(entry);
        _textures.Clear();
        foreach (var target in _subTargets.Values)
            ReleaseSubTarget(target);
        _subTargets.Clear();
        foreach (var target in _groupTargets.Values)
            ReleaseSubTarget(target);
        _groupTargets.Clear();
        DestroyTarget();
        foreach (ref var frame in _frames.AsSpan())
        {
            frame.Vertices?.Dispose();
            frame.Indices?.Dispose();
        }

        _white.Dispose();
        _materials.Clear(); // their sets go with the shaders' pools
        foreach (var shader in _shaders.Values)
        {
            foreach (var pipeline in shader.Pipelines.Values)
                _ctx.Deletions.Enqueue(GpuDeletion.Of(pipeline));
            _ctx.Deletions.Enqueue(GpuDeletion.Of(shader.Pool));
            _ctx.Deletions.Enqueue(GpuDeletion.Of(shader.Layout));
            _ctx.Deletions.Enqueue(GpuDeletion.Of(shader.SetLayout));
        }

        _shaders.Clear();
        _ring?.Dispose();
        _ring = null;
        foreach (var pipeline in _pipelines.Values)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(pipeline));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_compositePipeline));
        foreach (var pool in _pools)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(pool));
        foreach (var sampler in _samplers)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(sampler));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_pass));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_layout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_compositeLayout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_textureSetLayout));
        foreach (var buffer in _lightBuffers)
            buffer?.Dispose();
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_lightPool));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_lightSetLayout));
    }
}
