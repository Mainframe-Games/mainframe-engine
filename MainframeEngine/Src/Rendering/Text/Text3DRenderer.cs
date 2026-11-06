using DrawingColor = System.Drawing.Color;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>One glyph quad as <c>Text/Text3D.vk.vert</c> reads it (binding 0, instance rate; 40 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Text3DGlyphInstance
{
    /// <summary>x0, y0 (bottom), x1, y1 (top) in label space (world units).</summary>
    public Vector4 Rect;

    /// <summary>u0, v0 (top-left), u1, v1 (bottom-right) in the atlas page.</summary>
    public Vector4 Uv;

    /// <summary>The label record's index in this frame slot's record buffer; bit 31 marks an outline glyph.</summary>
    public uint Record;

    /// <summary>The page's slot in its group (0–7).</summary>
    public uint Page;

    public const int Size = 40;
    public const uint OutlineBit = 0x80000000u;
}

/// <summary>A label's per-view data as <c>include/text3d.slang</c> reads it (set 2, std430; 112 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Text3DLabelRecord
{
    public Matrix4x4 Model;
    public Vector4 Color;
    public Vector4 OutlineColor;
    public uint ObjectId;
    public uint Flags;
    public uint Pad0;
    public uint Pad1;

    public const int Size = 112;
}

/// <summary>The push constants of the text pipelines (<c>TextPush</c> in <c>include/text3d.slang</c>).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct Text3DPush
{
    public uint Cutout;
    public float Cutoff;
    public uint Pad0;
    public uint Pad1;
}

/// <summary>Per-frame counters of the batched text renderer (tests, the renderer debug window).</summary>
public struct Text3DStats
{
    /// <summary>Labels drawn in colour passes (summed over views).</summary>
    public int Labels;

    /// <summary>Labels outside the frustum or their visibility range (summed over views).</summary>
    public int Culled;

    /// <summary>Glyph quads drawn in colour passes (outline glyphs included).</summary>
    public int Glyphs;

    /// <summary>Instanced draws in colour passes.</summary>
    public int DrawCalls;

    /// <summary>Instanced draws in object-ID passes.</summary>
    public int ObjectIdDrawCalls;

    /// <summary>Atlas pages resident (all fonts).</summary>
    public int Pages;
}

/// <summary>
/// Draws every unshaded <see cref="Label3D"/> of a world in a handful of instanced draws (ADR 0258; the proposal's
/// <c>Text3DRenderer</c>), beside the mesh batcher like <see cref="ParticleRenderer"/>. Each label keeps a glyph run that is
/// rebuilt only when it changes (<see cref="Label3D.SyncGlyphs"/>); the font atlas pages it uses take slots in groups of
/// eight (one descriptor set per group: a sampler and eight images, so glyphs of different pages and fonts share a draw).
/// Per view, labels are posed on the CPU (<see cref="Billboard.Face"/>, <see cref="Billboard.FixedSize"/>), culled by
/// their bounds and visibility range, and ordered: cutouts, then blended labels by <see cref="Label3D.TextRenderPriority"/>
/// and back to front, then <see cref="Label3D.NoDepthTest"/> labels the same way. Their records (model matrix, colours,
/// object id) go to a per-frame-slot storage buffer and their glyphs to a per-frame-slot instance buffer; every run of
/// glyphs with the same pipeline and page group is one draw of six vertices per glyph. Object IDs draw the same quads.
/// </summary>
/// <remarks>Render thread only. Steady frames allocate nothing; resources are created when labels or pages change.</remarks>
internal sealed unsafe class Text3DRenderer : IDisposable
{
    /// <summary>Atlas pages per descriptor set (<c>kTextPages</c> in <c>include/text3d.slang</c>).</summary>
    public const int PagesPerGroup = 8;

    /// <summary>The alpha below which a cutout label discards (<see cref="StandardMaterial3D.AlphaCutoff"/>'s default).</summary>
    public const float CutoutThreshold = 0.5f;

    private const uint PageSetIndex = 1;
    private const uint RecordSetIndex = 2;
    private const int Slots = IVulkanContext.MaxFramesInFlight;

    /// <summary>What the renderer keeps per label: the page slot of each glyph of the run it last saw.</summary>
    internal sealed class LabelState
    {
        public int Version = -1;
        public int[] Slots = [];
        public int Count;
    }

    private sealed class PageSlot(Texture2D texture, TextureGpu gpu)
    {
        public Texture2D Texture { get; } = texture;
        public TextureGpu Gpu { get; } = gpu;
        public int Refs;
        public int Generation = -1;
        public bool Ready;
    }

    private sealed class PageGroup
    {
        public DescriptorSet Set;
        public DescriptorPool Pool;
        public bool Dirty = true;
    }

    private struct Run
    {
        public int Pipeline;
        public int Group;
        public uint First;
        public uint Count;
        public uint Cutout;
    }

    private readonly IVulkanContext _ctx;
    private readonly DescriptorSetLayout _pageLayout;
    private readonly DescriptorSetLayout _recordLayout;
    private readonly PipelineLayout _layout;
    private readonly MaterialDescriptorAllocator _pageSets;
    private readonly MaterialDescriptorAllocator _recordSets;
    private readonly GpuTexture _empty;
    private readonly RenderPass _idPass;
    private readonly StreamRing<Text3DGlyphInstance> _glyphs;
    private readonly StreamRing<Text3DLabelRecord> _records;
    private readonly DescriptorSet[] _recordSet = new DescriptorSet[Slots];
    private readonly DescriptorPool[] _recordPool = new DescriptorPool[Slots];
    private readonly GpuBuffer?[] _recordSetBuffer = new GpuBuffer?[Slots];
    private readonly Dictionary<int, Pipeline> _pipelines = [];
    private readonly List<PageSlot?> _slots = [];
    private readonly Dictionary<Texture2D, int> _slotByTexture = new(ReferenceEqualityComparer.Instance);
    private readonly List<PageGroup> _groups = [];

    // Per-view scratch (grow-only).
    private Label3D[] _visible = new Label3D[64];
    private Matrix4x4[] _models = new Matrix4x4[64];
    private double[] _keys = new double[64];
    private int[] _order = new int[64];
    private Run[] _runs = new Run[16];
    private int[] _staging = new int[64];
    private ulong _statsFrame = ulong.MaxValue;
    private Text3DStats _stats;
    private bool _disposed;

    public Text3DRenderer(IVulkanContext ctx)
    {
        _ctx = ctx;
        ReadOnlySpan<DescriptorSetLayoutBinding> pageBindings =
        [
            new() { Binding = 0, DescriptorType = DescriptorType.Sampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit },
            new() { Binding = 1, DescriptorType = DescriptorType.SampledImage, DescriptorCount = PagesPerGroup, StageFlags = ShaderStageFlags.FragmentBit },
        ];
        _pageLayout = PipelineBuilder.CreateSetLayout(ctx, pageBindings, "text3d pages set 1");
        ReadOnlySpan<DescriptorSetLayoutBinding> recordBindings =
        [
            new() { Binding = 0, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit },
        ];
        _recordLayout = PipelineBuilder.CreateSetLayout(ctx, recordBindings, "text3d labels set 2");
        _layout = ctx.Frame.CreatePipelineLayout(null, [_pageLayout, _recordLayout], "text3d");
        _pageSets = new MaterialDescriptorAllocator(ctx, _pageLayout, samplers: 1, images: PagesPerGroup);
        _recordSets = new MaterialDescriptorAllocator(ctx, _recordLayout, samplers: 0, images: 0, storage: 1);
        // Empty slots sample transparent white: a glyph whose page is not resident yet draws nothing.
        _empty = GpuTexture.Create2D(ctx, 1, 1, [255, 255, 255, 0], TextureColorSpace.Srgb, TextureSampling.LinearClamp);
        _idPass = RenderTarget.CreateRenderPass(ctx, MeshRenderer.ObjectIdTargetDesc(MeshRenderer.FindDepthFormat(ctx)));
        _glyphs = new StreamRing<Text3DGlyphInstance>(ctx, Text3DGlyphInstance.Size, BufferUsageFlags.VertexBufferBit, 4096);
        _records = new StreamRing<Text3DLabelRecord>(ctx, Text3DLabelRecord.Size, BufferUsageFlags.StorageBufferBit, 256);
    }

    /// <summary>This frame's counters (zero until something draws).</summary>
    public Text3DStats Stats => _statsFrame == _ctx.FrameNumber ? _stats with { Pages = _slotByTexture.Count } : new Text3DStats { Pages = _slotByTexture.Count };

    // ── Preparation ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds the changed glyph runs of <paramref name="world"/>'s batched labels, assigns their pages to slots and
    /// uploads changed pages. Call before the frame records (<see cref="RenderServer.PrepareFrame"/>); once per frame.
    /// </summary>
    public void Prepare(World3D world)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (world.TextPreparedFrame == _ctx.FrameNumber)
            return;
        world.TextPreparedFrame = _ctx.FrameNumber;
        var labels = world.LabelList;
        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (!label.IsBatchedText)
            {
                if (label.TextState is { Count: > 0 })
                    Release(label); // turned lit, or into a shadow-casting cutout: the mesh batcher draws it now
                continue;
            }

            if (!label.IsVisibleInTree())
                continue;
            label.SyncGlyphs();
            var state = label.TextState ??= new LabelState();
            if (state.Version != label.GlyphsVersion)
                AssignSlots(label, state);
        }

        // Pages whose pixels changed (new glyphs) re-upload; their group's set takes the new image.
        for (var s = 0; s < _slots.Count; s++)
        {
            if (_slots[s] is not { } slot)
                continue;
            slot.Ready = slot.Gpu.Update();
            if (slot.Gpu.Generation != slot.Generation)
            {
                slot.Generation = slot.Gpu.Generation;
                _groups[s / PagesPerGroup].Dirty = true;
            }
        }

        for (var g = 0; g < _groups.Count; g++)
            if (_groups[g].Dirty)
                WriteGroup(g);
    }

    /// <summary>Returns <paramref name="label"/>'s page references (it left the tree, was freed, or turned unbatched).</summary>
    public void Release(Label3D label)
    {
        if (_disposed || label.TextState is not { } state)
            return;
        for (var i = 0; i < state.Count; i++)
            ReleaseSlot(state.Slots[i]);
        state.Count = 0;
        state.Version = -1;
    }

    private void AssignSlots(Label3D label, LabelState state)
    {
        var glyphs = label.Glyphs;
        if (_staging.Length < glyphs.Length)
            _staging = new int[Math.Max(glyphs.Length, _staging.Length * 2)];
        // Acquire the new pages before releasing the old ones, so a page both runs use keeps its slot.
        for (var i = 0; i < glyphs.Length; i++)
            _staging[i] = AcquireSlot(glyphs[i].Page);
        for (var i = 0; i < state.Count; i++)
            ReleaseSlot(state.Slots[i]);
        if (state.Slots.Length < glyphs.Length)
            state.Slots = new int[Math.Max(glyphs.Length, state.Slots.Length * 2)];
        Array.Copy(_staging, state.Slots, glyphs.Length);
        state.Count = glyphs.Length;
        state.Version = label.GlyphsVersion;
    }

    private int AcquireSlot(Texture2D page)
    {
        if (_slotByTexture.TryGetValue(page, out var index))
        {
            _slots[index]!.Refs++;
            return index;
        }

        index = _slots.IndexOf(null);
        if (index < 0)
        {
            index = _slots.Count;
            _slots.Add(null);
            if (index / PagesPerGroup >= _groups.Count)
                _groups.Add(new PageGroup());
        }

        var gpu = new TextureGpu(_ctx, page, page.ImportSettings.ResolveColorSpace(colorUsage: true));
        _slots[index] = new PageSlot(page, gpu) { Refs = 1 };
        _slotByTexture.Add(page, index);
        _groups[index / PagesPerGroup].Dirty = true;
        return index;
    }

    private void ReleaseSlot(int index)
    {
        if (index < 0 || index >= _slots.Count || _slots[index] is not { } slot || --slot.Refs > 0)
            return;
        slot.Gpu.Release();
        _slotByTexture.Remove(slot.Texture);
        _slots[index] = null;
        _groups[index / PagesPerGroup].Dirty = true;
    }

    private void WriteGroup(int g)
    {
        var group = _groups[g];
        group.Dirty = false;
        // A set may still be bound by frames in flight: write a fresh one, return the old one when they finish.
        _pageSets.Free(group.Pool, group.Set);
        group.Set = _pageSets.Allocate(out var pool);
        group.Pool = pool;
        var images = stackalloc DescriptorImageInfo[1 + PagesPerGroup];
        var sampler = _empty.Sampler;
        for (var i = 0; i < PagesPerGroup; i++)
        {
            var s = g * PagesPerGroup + i;
            var texture = s < _slots.Count && _slots[s] is { Ready: true, Gpu.Gpu: { } gpu } ? gpu : _empty;
            if (!ReferenceEquals(texture, _empty) && sampler.Handle == _empty.Sampler.Handle)
                sampler = texture.Sampler; // the pages' own sampling (all font pages share their settings)
            images[1 + i] = new DescriptorImageInfo { ImageView = texture.View, ImageLayout = ImageLayout.ShaderReadOnlyOptimal };
        }

        images[0] = new DescriptorImageInfo { Sampler = sampler };
        var writes = stackalloc WriteDescriptorSet[2];
        writes[0] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = group.Set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.Sampler,
            PImageInfo = images,
        };
        writes[1] = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = group.Set,
            DstBinding = 1,
            DescriptorCount = PagesPerGroup,
            DescriptorType = DescriptorType.SampledImage,
            PImageInfo = images + 1,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 2, writes, 0, null);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Draws <paramref name="world"/>'s batched labels into the current scene pass for <paramref name="camera"/> (the frame's
    /// current view): cutouts, blended labels, then <see cref="Label3D.NoDepthTest"/> ones. <paramref name="exposure"/> is the
    /// view's effective exposure, which <see cref="Label3D.IgnoreExposure"/> labels divide their colour by.
    /// </summary>
    public void Draw(World3D world, ICamera camera, CommandBuffer cb, float exposure) => Record(world, camera, cb, exposure, ids: false);

    /// <summary>Draws the batched labels' object ids into the current object-ID pass.</summary>
    public void DrawObjectIds(World3D world, ICamera camera, CommandBuffer cb) => Record(world, camera, cb, 1f, ids: true);

    private void Record(World3D world, ICamera camera, CommandBuffer cb, float exposure, bool ids)
    {
        if (_disposed || !_ctx.FrameStarted || world.LabelList.Count == 0)
            return;
        Prepare(world); // a no-op after PrepareFrame
        ResetStatsIfNewFrame();

        var count = Collect(world, camera, out var glyphCount);
        if (count == 0 || glyphCount == 0)
            return;

        var records = _records.Allocate(count, out var recordBuffer, out var firstRecord);
        var glyphs = _glyphs.Allocate(glyphCount, out var glyphBuffer, out var firstGlyph);
        var invExposure = exposure > 1e-6f ? 1f / exposure : 1f;
        var runCount = 0;
        var written = 0;
        for (var k = 0; k < count; k++)
        {
            var n = _order[k];
            var label = _visible[n];
            var state = label.TextState!;
            var scale = label.IgnoreExposure ? invExposure : 1f;
            records[k] = new Text3DLabelRecord
            {
                Model = _models[n],
                Color = Linear(label.Modulate, scale),
                OutlineColor = Linear(label.OutlineModulate, scale),
                ObjectId = label.ObjectId,
            };

            var depthTest = !label.NoDepthTest;
            var cutout = label.AlphaCut == SpriteAlphaCut.Discard;
            var cull = label.DoubleSided ? 0 : MeshRenderer.Determinant3(_models[n]) < 0f ? 2 : 1;
            var pipeline = (depthTest ? 1 : 0) | (cutout ? 2 : 0) | (cull << 2);
            var record = firstRecord + (uint)k;
            var run = label.Glyphs;
            var textFirst = label.OutlineRenderPriority > label.TextRenderPriority;
            for (var pass = 0; pass < 2; pass++)
            {
                var outlines = pass == 0 != textFirst;
                for (var i = 0; i < run.Length; i++)
                {
                    ref readonly var glyph = ref run[i];
                    if (glyph.Outline != outlines)
                        continue;
                    var slot = state.Slots[i];
                    var group = slot / PagesPerGroup;
                    glyphs[written] = new Text3DGlyphInstance
                    {
                        Rect = glyph.Rect,
                        Uv = glyph.Uv,
                        Record = record | (glyph.Outline ? Text3DGlyphInstance.OutlineBit : 0u),
                        Page = (uint)(slot % PagesPerGroup),
                    };

                    if (runCount > 0 && _runs[runCount - 1].Pipeline == pipeline && _runs[runCount - 1].Group == group)
                    {
                        _runs[runCount - 1].Count++;
                    }
                    else
                    {
                        if (runCount == _runs.Length)
                            Array.Resize(ref _runs, runCount * 2);
                        _runs[runCount++] = new Run
                        {
                            Pipeline = pipeline, Group = group, First = firstGlyph + (uint)written, Count = 1, Cutout = cutout ? 1u : 0u,
                        };
                    }

                    written++;
                }
            }
        }

        var vk = _ctx.Vk;
        var frame = _ctx.Frame;
        PipelineBuilder.SetViewport(vk, cb, frame.Extent, flipY: true);
        var handle = glyphBuffer.Handle;
        var zero = 0ul;
        vk.CmdBindVertexBuffers(cb, 0, 1, &handle, &zero);
        frame.Bind(cb, _layout);
        var recordSet = RecordSet(recordBuffer);
        vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, RecordSetIndex, 1, &recordSet, 0, null);

        Pipeline bound = default;
        var boundGroup = -1;
        var boundCutout = uint.MaxValue;
        for (var r = 0; r < runCount; r++)
        {
            ref var run = ref _runs[r];
            var pipeline = GetPipeline(run.Pipeline, ids);
            if (pipeline.Handle != bound.Handle)
            {
                vk.CmdBindPipeline(cb, PipelineBindPoint.Graphics, pipeline);
                bound = pipeline;
            }

            if (run.Group != boundGroup)
            {
                var set = _groups[run.Group].Set;
                vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Graphics, _layout, PageSetIndex, 1, &set, 0, null);
                boundGroup = run.Group;
            }

            if (run.Cutout != boundCutout)
            {
                var push = new Text3DPush { Cutout = run.Cutout, Cutoff = CutoutThreshold };
                vk.CmdPushConstants(cb, _layout, FrameContext.PushConstantStages, 0, (uint)sizeof(Text3DPush), &push);
                boundCutout = run.Cutout;
            }

            vk.CmdDraw(cb, 6, run.Count, 0, run.First);
            run = default;
        }

        if (ids)
        {
            _stats.ObjectIdDrawCalls += runCount;
        }
        else
        {
            _stats.Labels += count;
            _stats.Glyphs += glyphCount;
            _stats.DrawCalls += runCount;
        }

        Array.Clear(_visible, 0, count); // drop the references
    }

    // Poses, culls and orders the view's labels into _visible/_models/_order; returns how many draw.
    private int Collect(World3D world, ICamera camera, out int glyphCount)
    {
        glyphCount = 0;
        var labels = world.LabelList;
        var view = camera.ViewMatrix;
        var projection = camera.ProjectionMatrix;
        var frustum = new Frustum(view * projection);
        var cameraPosition = camera.Position;
        var forward = -new Vector3(view.M13, view.M23, view.M33);
        var pixelScale = LodSelection.PixelScale(projection, _ctx.Frame.Extent.Height);
        var perspective = LodSelection.IsPerspective(projection);
        if (_visible.Length < labels.Count)
        {
            var size = Math.Max(labels.Count, _visible.Length * 2);
            _visible = new Label3D[size];
            _models = new Matrix4x4[size];
            _keys = new double[size];
            _order = new int[size];
        }

        var count = 0;
        for (var i = 0; i < labels.Count; i++)
        {
            var label = labels[i];
            if (!label.IsBatchedText || !label.IsVisibleInTree() || label.TextState is not { } state ||
                state.Version != label.GlyphsVersion || state.Count == 0)
                continue;
            var model = Billboard.Face(label.ModelMatrix, label.Billboard, view);
            if (label.FixedSize)
                model = Billboard.FixedSize(model, label.PixelSize, view, pixelScale, perspective);
            var bounds = label.RunBounds.Transform(model);
            if (!label.IsInVisibilityRange(cameraPosition, bounds) || !frustum.Intersects(bounds))
            {
                _stats.Culled++;
                continue;
            }

            // Cutouts first (any order), blended by priority then farthest first, no-depth-test labels last the same way.
            var category = label.NoDepthTest ? 2 : label.AlphaCut == SpriteAlphaCut.Discard ? 0 : 1;
            var depth = Vector3.Dot(new Vector3(model.M41, model.M42, model.M43) - cameraPosition, forward);
            var key = category * 1e15;
            if (category != 0)
                key += (Math.Clamp(label.TextRenderPriority, -128, 127) + 128) * 1e9 + (1e8 - Math.Clamp(depth, -1e8 + 1, 1e8 - 1));
            _visible[count] = label;
            _models[count] = model;
            _keys[count] = key + count * 1e-7; // equal keys keep the tree order
            _order[count] = count;
            glyphCount += state.Count;
            count++;
        }

        if (count > 1)
            _keys.AsSpan(0, count).Sort(_order.AsSpan(0, count));
        return count;
    }

    private DescriptorSet RecordSet(GpuBuffer buffer)
    {
        var slot = _ctx.FrameSlot;
        if (ReferenceEquals(_recordSetBuffer[slot], buffer) && _recordSet[slot].Handle != 0)
            return _recordSet[slot];
        _recordSets.Free(_recordPool[slot], _recordSet[slot]);
        var set = _recordSets.Allocate(out var pool);
        var info = buffer.Descriptor();
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = set,
            DstBinding = 0,
            DescriptorCount = 1,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = &info,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
        _recordSet[slot] = set;
        _recordPool[slot] = pool;
        _recordSetBuffer[slot] = buffer;
        return set;
    }

    private static Vector4 Linear(DrawingColor c, float scale) =>
        new(ColorSpace.SrgbToLinear(c.R / 255f) * scale, ColorSpace.SrgbToLinear(c.G / 255f) * scale, ColorSpace.SrgbToLinear(c.B / 255f) * scale, c.A / 255f);

    private void ResetStatsIfNewFrame()
    {
        if (_statsFrame == _ctx.FrameNumber)
            return;
        _statsFrame = _ctx.FrameNumber;
        _stats = default;
    }

    // ── Pipelines ────────────────────────────────────────────────────────────────────────────────────────────

    // key: bit 0 depth test, bit 1 cutout, bits 2-3 cull (0 none, 1 back, 2 front).
    private Pipeline GetPipeline(int key, bool ids)
    {
        var full = key | (ids ? 1 << 4 : 0);
        if (_pipelines.TryGetValue(full, out var pipeline))
            return pipeline;
        var depthTest = (key & 1) != 0;
        var cutout = (key & 2) != 0;
        var state = new PipelineState
        {
            CullMode = ((key >> 2) & 3) switch
            {
                1 => CullModeFlags.BackBit,
                2 => CullModeFlags.FrontBit,
                _ => CullModeFlags.None,
            },
            FrontFace = FrontFace.CounterClockwise,
            DepthTest = depthTest,
            DepthWrite = depthTest && (cutout || ids),
            DepthCompare = CompareOp.Less,
            Blend = cutout || ids ? BlendMode.Opaque : BlendMode.Alpha,
        };
        ReadOnlySpan<VertexInputBindingDescription> bindings =
        [
            new() { Binding = 0, Stride = Text3DGlyphInstance.Size, InputRate = VertexInputRate.Instance },
        ];
        ReadOnlySpan<VertexInputAttributeDescription> attributes =
        [
            new() { Location = 0, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 0 },
            new() { Location = 1, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 16 },
            new() { Location = 2, Binding = 0, Format = Format.R32G32Uint, Offset = 32 },
        ];
        pipeline = ids
            ? PipelineBuilder.Create(_ctx, state, _layout, _idPass, "Shaders/Text/Text3DId.vk.vert.spv", "Shaders/Text/Text3DId.vk.frag.spv",
                bindings, attributes, "text3d ids")
            : PipelineBuilder.Create(_ctx, state, _layout, _ctx.RenderPass, "Shaders/Text/Text3D.vk.vert.spv", "Shaders/Text/Text3D.vk.frag.spv",
                bindings, attributes, "text3d");
        _pipelines.Add(full, pipeline);
        return pipeline;
    }

    /// <summary>Releases every GPU object through the deletion queue (safe while frames are in flight).</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var slot in _slots)
            slot?.Gpu.Release();
        _slots.Clear();
        _slotByTexture.Clear();
        _groups.Clear();
        _pageSets.Dispose();
        _recordSets.Dispose();
        _glyphs.Dispose();
        _records.Dispose();
        _empty.Dispose();
        var deletions = _ctx.Deletions;
        foreach (var pipeline in _pipelines.Values)
            deletions.Enqueue(GpuDeletion.Of(pipeline));
        _pipelines.Clear();
        deletions.Enqueue(GpuDeletion.Of(_idPass));
        deletions.Enqueue(GpuDeletion.Of(_layout));
        deletions.Enqueue(GpuDeletion.Of(_pageLayout));
        deletions.Enqueue(GpuDeletion.Of(_recordLayout));
    }
}

/// <summary>
/// A per-frame-slot streaming buffer of <typeparamref name="T"/> (a bump allocator reset each frame, keyed by frame
/// number, like <see cref="InstanceBuffer"/>); grows by doubling, the old buffer going through the deletion queue (still
/// valid for draws recorded from it). Steady-state frames allocate nothing.
/// </summary>
internal sealed class StreamRing<T> : IDisposable where T : unmanaged
{
    private const int Slots = IVulkanContext.MaxFramesInFlight;

    private readonly IVulkanContext _ctx;
    private readonly int _stride;
    private readonly BufferUsageFlags _usage;
    private readonly int _initialCapacity;
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[Slots];
    private readonly ulong[] _frame = new ulong[Slots];
    private readonly int[] _used = new int[Slots];
    private bool _disposed;

    public StreamRing(IVulkanContext ctx, int stride, BufferUsageFlags usage, int initialCapacity)
    {
        _ctx = ctx;
        _stride = stride;
        _usage = usage;
        _initialCapacity = initialCapacity;
    }

    /// <summary>Reserves <paramref name="count"/> elements in this frame's buffer, starting at element <paramref name="first"/>.</summary>
    public Span<T> Allocate(int count, out GpuBuffer buffer, out uint first)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slot = _ctx.FrameSlot;
        if (_frame[slot] != _ctx.FrameNumber)
        {
            _frame[slot] = _ctx.FrameNumber; // the slot's fence has been waited: its buffer is free again
            _used[slot] = 0;
        }

        var current = _buffers[slot];
        var capacity = (int)((current?.Size ?? 0) / (ulong)_stride);
        if (current is null || _used[slot] + count > capacity)
        {
            var grown = Math.Max(_initialCapacity, Math.Max(capacity * 2, _used[slot] + count));
            current?.Dispose(); // draws recorded earlier this frame keep it alive until the frame finishes
            current = GpuBuffer.Create(_ctx, (ulong)grown * (ulong)_stride, _usage, GpuMemoryUsage.Dynamic);
            _buffers[slot] = current;
            _used[slot] = 0;
        }

        buffer = current;
        first = (uint)_used[slot];
        _used[slot] += count;
        return MemoryMarshal.Cast<byte, T>(current.MappedSpan).Slice((int)first, count);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var i = 0; i < Slots; i++)
        {
            _buffers[i]?.Dispose();
            _buffers[i] = null;
        }
    }
}
