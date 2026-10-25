using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

namespace MainframeEngine;

/// <summary>
/// The compute pre-skinning source of one vertex (64 bytes, std430; <c>Skinning/Skin.vk.comp</c>): bind-pose position
/// and normal with the UV in their w components, four joint indices and their weights.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SkinVertex(Vector3 position, Vector3 normal, Vector2 uv, int j0, int j1, int j2, int j3, Vector4 weights)
{
    public Vector4 PositionU = new(position, uv.X);
    public Vector4 NormalV = new(normal, uv.Y);
    public uint Joint0 = (uint)j0, Joint1 = (uint)j1, Joint2 = (uint)j2, Joint3 = (uint)j3;
    public Vector4 Weights = weights;

    public const int Size = 64;
}

/// <summary>
/// A skinned <see cref="GeometryInstance3D"/>'s GPU state (ADR 0207): one <see cref="MeshVertex"/> buffer per frame slot
/// that the skinning pass writes, the descriptor sets that point at them, and <see cref="View"/>, a per-instance
/// <see cref="MeshGpu"/> sharing the source mesh's indices and streams whose vertices are this frame's skinned buffer. Draw
/// lists, shadow casters and picking use <see cref="View"/> like any mesh, so every pipeline draws the pose unchanged.
/// </summary>
internal sealed class SkinnedMeshGpu
{
    private const int Slots = IVulkanContext.MaxFramesInFlight;

    public SkinnedMeshGpu(MeshGpu source, MeshGpu view)
    {
        Source = source;
        View = view;
    }

    public MeshGpu Source { get; }

    public MeshGpu View { get; }

    /// <summary>The source mesh generation <see cref="View"/> mirrors.</summary>
    public int SourceGeneration = -1;

    public readonly GpuBuffer?[] Outputs = new GpuBuffer?[Slots];
    public readonly DescriptorSet[] Sets = new DescriptorSet[Slots];

    // The buffers each slot's set was written for (rewritten when one is replaced).
    public readonly (ulong Source, ulong Palette, ulong Output)[] SetBuffers = new (ulong, ulong, ulong)[Slots];
    public DescriptorPool Pool;

    /// <summary>This frame's palette and bounds (set by the renderer while preparing).</summary>
    public SkinState? Pose;

    /// <summary>First float4 of this instance's palette in this frame's palette buffer.</summary>
    public uint PaletteBase;

    /// <summary>Frame stamp of the last queueing (once per frame).</summary>
    public ulong QueuedFrame;

    /// <summary>The buffer the last dispatch wrote and its frame number: last frame's pose for motion vectors (ADR 0163).</summary>
    public GpuBuffer? LastOutput;
    public ulong LastFrame;

    /// <summary>Last frame's skinned vertices for this frame's prepass (this frame's when the instance was not skinned then).</summary>
    public GpuBuffer? Previous;
}

/// <summary>
/// The compute pre-skinning pass (ADR 0207): <see cref="MeshRenderer"/> queues the skinned instances it prepares; after
/// <c>BeginFrame</c> and before the shadow pass, <see cref="Dispatch"/> writes every queued instance's joint palette into a
/// host-visible ring keyed by <see cref="IVulkanContext.FrameSlot"/>, dispatches <c>Skinning/Skin.vk.comp</c> once per
/// instance into its frame-slot vertex buffer, and adds one barrier (compute write → vertex attribute read).
/// </summary>
/// <remarks>Render thread only. Steady-state frames allocate nothing.</remarks>
internal sealed unsafe class MeshSkinner : IDisposable
{
    private const int Slots = IVulkanContext.MaxFramesInFlight;
    private const uint GroupSize = 64;
    private const int PushSize = 16;

    private readonly IVulkanContext _ctx;
    private readonly DescriptorSetLayout _setLayout;
    private readonly PipelineLayout _layout;
    private Pipeline _pipeline;
    private readonly GpuBuffer?[] _palettes = new GpuBuffer?[Slots];
    private SkinnedMeshGpu?[] _queue = new SkinnedMeshGpu?[16];
    private int _queueCount;
    private bool _disposed;

    public MeshSkinner(IVulkanContext ctx)
    {
        _ctx = ctx;
        ReadOnlySpan<DescriptorSetLayoutBinding> bindings =
        [
            new() { Binding = 0, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new() { Binding = 1, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
            new() { Binding = 2, DescriptorType = DescriptorType.StorageBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.ComputeBit },
        ];
        _setLayout = PipelineBuilder.CreateSetLayout(ctx, bindings, "skinning set 0");
        _layout = PipelineBuilder.CreateLayout(ctx, [_setLayout], PushSize, ShaderStageFlags.ComputeBit, "skinning");
    }

    /// <summary>Instances skinned by the last <see cref="Dispatch"/> (diagnostics, tests).</summary>
    public int LastDispatchCount { get; private set; }

    /// <summary>Vertices skinned by the last <see cref="Dispatch"/>.</summary>
    public int LastVertexCount { get; private set; }

    /// <summary>Drops the queue (a new frame's preparation starts; a skipped frame never dispatched it).</summary>
    public void BeginPreparation()
    {
        Array.Clear(_queue, 0, _queueCount);
        _queueCount = 0;
    }

    /// <summary>Queues <paramref name="skin"/> for this frame's pass (once per frame).</summary>
    public void Queue(SkinnedMeshGpu skin, ulong frame)
    {
        if (skin.QueuedFrame == frame)
            return;
        skin.QueuedFrame = frame;
        if (_queueCount == _queue.Length)
            Array.Resize(ref _queue, _queue.Length * 2);
        _queue[_queueCount++] = skin;
    }

    /// <summary>The per-instance state for a skinned node drawing <paramref name="source"/>.</summary>
    public SkinnedMeshGpu Create(MeshGpu source, int viewId)
    {
        var view = MeshGpu.CreateSkinnedView(_ctx, source, viewId);
        var skin = new SkinnedMeshGpu(source, view);
        ReadOnlySpan<DescriptorPoolSize> sizes = [new() { Type = DescriptorType.StorageBuffer, DescriptorCount = 3 * Slots }];
        skin.Pool = PipelineBuilder.CreatePool(_ctx, Slots, sizes, "skinning");
        for (var s = 0; s < Slots; s++)
            skin.Sets[s] = PipelineBuilder.AllocateSet(_ctx, skin.Pool, _setLayout, "skinning");
        view.Skin = skin;
        return skin;
    }

    /// <summary>Releases an instance's buffers and sets (deferred until frames in flight finish).</summary>
    public void Release(SkinnedMeshGpu skin)
    {
        for (var s = 0; s < Slots; s++)
        {
            skin.Outputs[s]?.Dispose();
            skin.Outputs[s] = null;
        }

        skin.LastOutput = skin.Previous = null;
        if (skin.Pool.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(skin.Pool));
        skin.Pool = default;
        skin.View.Skin = null;
        for (var i = 0; i < _queueCount; i++)
            if (ReferenceEquals(_queue[i], skin))
                _queue[i] = null;
    }

    /// <summary>
    /// Records the frame's skinning (no render pass active; after the upload queue's copies, before anything draws a
    /// skinned instance). Call once per started frame.
    /// </summary>
    public void Dispatch(CommandBuffer cb)
    {
        LastDispatchCount = 0;
        LastVertexCount = 0;
        if (_queueCount == 0 || !_ctx.FrameStarted)
            return;

        var slot = _ctx.FrameSlot;
        var frame = _ctx.FrameNumber;
        var joints = 0;
        for (var i = 0; i < _queueCount; i++)
            if (_queue[i] is { Pose: { } pose, Source.SkinSource: not null })
                joints += pose.JointCount;

        var palette = EnsurePalette(slot, joints * 3);
        var rows = MemoryMarshal.Cast<byte, Vector4>(palette.MappedSpan);
        var vk = _ctx.Vk;

        // WAR: last frame's prepass may still read the buffers this frame writes (as its previous pose).
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.VertexInputBit, PipelineStageFlags.ComputeShaderBit, 0, 0, null, 0, null, 0, null);
        vk.CmdBindPipeline(cb, PipelineBindPoint.Compute, Pipeline);
        var row = 0u;
        for (var i = 0; i < _queueCount; i++)
        {
            if (_queue[i] is not { Pose: { } pose, Source: { SkinSource: { } source } mesh } skin)
                continue;
            var vertexCount = mesh.VertexCount;
            if (vertexCount == 0)
                continue;

            skin.PaletteBase = row;
            var matrices = pose.Palette;
            for (var j = 0; j < pose.JointCount; j++)
            {
                // The column-vector rows: the C# row-vector matrix's columns.
                ref readonly var m = ref matrices[j];
                rows[(int)row++] = new Vector4(m.M11, m.M21, m.M31, m.M41);
                rows[(int)row++] = new Vector4(m.M12, m.M22, m.M32, m.M42);
                rows[(int)row++] = new Vector4(m.M13, m.M23, m.M33, m.M43);
            }

            var output = EnsureOutput(skin, slot, vertexCount);
            WriteSet(skin, slot, source, palette, output);
            var set = skin.Sets[slot];
            vk.CmdBindDescriptorSets(cb, PipelineBindPoint.Compute, _layout, 0, 1, &set, 0, null);
            var push = new SkinPush((uint)vertexCount, skin.PaletteBase, (uint)pose.JointCount);
            vk.CmdPushConstants(cb, _layout, ShaderStageFlags.ComputeBit, 0, PushSize, &push);
            vk.CmdDispatch(cb, ((uint)vertexCount + GroupSize - 1) / GroupSize, 1, 1);

            skin.Previous = skin.LastFrame == frame - 1 && skin.LastOutput is { } last ? last : output;
            skin.LastOutput = output;
            skin.LastFrame = frame;
            skin.View.SetSkinnedVertices(output);
            LastDispatchCount++;
            LastVertexCount += vertexCount;
        }

        var barrier = new MemoryBarrier
        {
            SType = StructureType.MemoryBarrier,
            SrcAccessMask = AccessFlags.ShaderWriteBit,
            DstAccessMask = AccessFlags.VertexAttributeReadBit,
        };
        vk.CmdPipelineBarrier(cb, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.VertexInputBit, 0, 1, &barrier, 0, null, 0, null);
        BeginPreparation();
    }

    private Pipeline Pipeline
    {
        get
        {
            if (_pipeline.Handle != 0)
                return _pipeline;
            var entry = SilkMarshalEntry;
            var info = new ComputePipelineCreateInfo
            {
                SType = StructureType.ComputePipelineCreateInfo,
                Stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = _ctx.Shaders.Get("Shaders/Skinning/Skin.vk.comp.spv"),
                    PName = (byte*)entry,
                },
                Layout = _layout,
            };
            _ctx.Pipelines.CompletePendingLoad(wait: false);
            _ctx.Vk.CreateComputePipelines(_ctx.Device, _ctx.Pipelines.Handle, 1, in info, null, out _pipeline)
                .Check("vkCreateComputePipelines (skinning)");
            return _pipeline;
        }
    }

    private static readonly nint SilkMarshalEntry = Silk.NET.Core.Native.SilkMarshal.StringToPtr("main"); // process lifetime

    // This slot's palette buffer, grown (doubled) when the frame needs more rows; the old one stays valid for frames in flight.
    private GpuBuffer EnsurePalette(int slot, int rows)
    {
        var current = _palettes[slot];
        var needed = (ulong)Math.Max(rows, 1) * 16;
        if (current is not null && current.Size >= needed)
            return current;
        current?.Dispose();
        var size = Math.Max(needed, Math.Max(current?.Size * 2 ?? 0, 64 * 3 * 16 * 4));
        return _palettes[slot] = GpuBuffer.Create(_ctx, size, BufferUsageFlags.StorageBufferBit, GpuMemoryUsage.Dynamic);
    }

    private GpuBuffer EnsureOutput(SkinnedMeshGpu skin, int slot, int vertexCount)
    {
        var size = (ulong)vertexCount * MeshVertex.Size;
        if (skin.Outputs[slot] is { } output && output.Size == size)
            return output;
        skin.Outputs[slot]?.Dispose();
        return skin.Outputs[slot] = GpuBuffer.Create(_ctx, size, BufferUsageFlags.VertexBufferBit | BufferUsageFlags.StorageBufferBit,
            GpuMemoryUsage.DeviceLocal);
    }

    private void WriteSet(SkinnedMeshGpu skin, int slot, GpuBuffer source, GpuBuffer palette, GpuBuffer output)
    {
        var key = (source.Handle.Handle, palette.Handle.Handle, output.Handle.Handle);
        if (skin.SetBuffers[slot] == key)
            return;
        skin.SetBuffers[slot] = key;
        var infos = stackalloc DescriptorBufferInfo[3];
        infos[0] = new DescriptorBufferInfo { Buffer = source.Handle, Offset = 0, Range = Vk.WholeSize };
        infos[1] = new DescriptorBufferInfo { Buffer = palette.Handle, Offset = 0, Range = Vk.WholeSize };
        infos[2] = new DescriptorBufferInfo { Buffer = output.Handle, Offset = 0, Range = Vk.WholeSize };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = skin.Sets[slot],
            DstBinding = 0,
            DescriptorCount = 3,
            DescriptorType = DescriptorType.StorageBuffer,
            PBufferInfo = infos,
        };
        _ctx.Vk.UpdateDescriptorSets(_ctx.Device, 1, &write, 0, null);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct SkinPush(uint vertexCount, uint paletteBase, uint jointCount)
    {
        public readonly uint VertexCount = vertexCount;
        public readonly uint PaletteBase = paletteBase;
        public readonly uint JointCount = jointCount;
        public readonly uint Pad;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        for (var s = 0; s < Slots; s++)
        {
            _palettes[s]?.Dispose();
            _palettes[s] = null;
        }

        if (_pipeline.Handle != 0)
            _ctx.Deletions.Enqueue(GpuDeletion.Of(_pipeline));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_layout));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_setLayout));
    }
}
