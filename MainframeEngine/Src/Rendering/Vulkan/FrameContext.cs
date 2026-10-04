using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>std140 camera block at set 0, binding 0 (<c>include/frame.glsl</c>, 368 bytes).</summary>
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

    /// <summary>Bytes in the std140 block.</summary>
    public const int Size = 5 * 64 + 3 * 16;

    /// <summary>Fills the block from a camera's matrices.</summary>
    public static FrameData From(in Matrix4x4 view, in Matrix4x4 projection, Vector3 cameraPosition, Extent2D extent,
        float time, float exposure)
    {
        Matrix4x4.Invert(projection, out var invProj);
        var viewRotation = view;
        viewRotation.M41 = viewRotation.M42 = viewRotation.M43 = 0f;
        Matrix4x4.Invert(viewRotation, out var invViewRotation);
        var (near, far) = ClipPlanes(projection);
        float w = Math.Max(1u, extent.Width), h = Math.Max(1u, extent.Height);
        return new FrameData
        {
            View = view,
            Projection = projection,
            ViewProjection = view * projection,
            InverseProjection = invProj,
            InverseViewRotation = invViewRotation,
            CameraPosition = new Vector4(cameraPosition, 0f),
            Viewport = new Vector4(w, h, 1f / w, 1f / h),
            Clip = new Vector4(near, far, time, exposure),
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
/// The per-frame shared descriptor set 0: camera (<see cref="FrameData"/>, binding 0) and lights (the
/// <see cref="LightEnvironment"/> UBO, binding 1), written once per frame into the frame slot's buffer and bound
/// by every scene pipeline — instead of each object writing and binding its own copies. Set 1 is the shadow set
/// (<see cref="ShadowSystem"/> or the renderer's fallback); per-material and per-object data follow in set 2+
/// and push constants (model matrix).
/// </summary>
/// <remarks>
/// <para>Call <see cref="Begin"/> once per frame before drawing (from <c>OnRenderMainPass</c>). Renderer-owned
/// drawers (sky, grid, Spine) also call the <c>Ensure*</c> methods with the camera they were given, which write
/// the data only if nothing has this frame, so games that never call <see cref="Begin"/> keep working.</para>
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

    private const int Slots = IVulkanContext.MaxFramesInFlight;

    private readonly IVulkanContext _ctx;
    private readonly ulong _lightsOffset;
    private readonly GpuBuffer[] _buffers = new GpuBuffer[Slots];
    private readonly DescriptorSet[] _sets = new DescriptorSet[Slots];
    private readonly DescriptorPool _pool;
    private readonly ulong[] _cameraFrame = new ulong[Slots];
    private readonly ulong[] _lightsFrame = new ulong[Slots];
    private bool _disposed;

    internal FrameContext(IVulkanContext ctx)
    {
        _ctx = ctx;
        ctx.Vk.GetPhysicalDeviceProperties(ctx.PhysicalDevice, out var props);
        _lightsOffset = FreeListBlock.AlignUp(FrameData.Size, Math.Max(256ul, props.Limits.MinUniformBufferOffsetAlignment));

        ReadOnlySpan<DescriptorSetLayoutBinding> bindings =
        [
            new() { Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = PushConstantStages },
            new() { Binding = 1, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = PushConstantStages },
        ];
        SetLayout = PipelineBuilder.CreateSetLayout(ctx, bindings, "frame set 0");
        _pool = PipelineBuilder.CreatePool(ctx, Slots,
            [new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = 2 * Slots }], "frame set 0");

        for (var i = 0; i < Slots; i++)
        {
            _buffers[i] = GpuBuffer.Create(ctx, _lightsOffset + LightEnvironment.UboSize, BufferUsageFlags.UniformBufferBit, GpuMemoryUsage.Dynamic);
            _buffers[i].MappedSpan.Clear();
            _sets[i] = PipelineBuilder.AllocateSet(ctx, _pool, SetLayout, "frame set 0");
            PipelineBuilder.WriteUniformBuffer(ctx, _sets[i], 0, _buffers[i].Descriptor(0, FrameData.Size));
            PipelineBuilder.WriteUniformBuffer(ctx, _sets[i], 1, _buffers[i].Descriptor(_lightsOffset, LightEnvironment.UboSize));
        }
    }

    /// <summary>Layout of set 0 (binding 0 camera, binding 1 lights).</summary>
    public DescriptorSetLayout SetLayout { get; }

    /// <summary>Set 0 for the frame being recorded.</summary>
    public DescriptorSet CurrentSet => _sets[_ctx.FrameSlot];

    /// <summary>Time in seconds written to <see cref="FrameData.Clip"/>.z by <see cref="Begin"/>.</summary>
    public float Time { get; set; }

    /// <summary>Writes this frame's camera and lights. Call once per frame before the main-pass draws.</summary>
    public void Begin(ICamera camera, LightEnvironment? lights)
    {
        ArgumentNullException.ThrowIfNull(camera);
        WriteCamera(camera.ViewMatrix, camera.ProjectionMatrix, camera.Position);
        if (lights is not null)
            WriteLights(lights, camera.Position);
    }

    /// <summary>Writes the camera block for this frame (overwrites anything written earlier this frame).</summary>
    public void WriteCamera(in Matrix4x4 view, in Matrix4x4 projection, Vector3 position)
    {
        if (!_ctx.FrameStarted) return;
        var slot = _ctx.FrameSlot;
        _buffers[slot].Write(FrameData.From(view, projection, position, _ctx.SwapchainExtent, Time, _ctx.Exposure));
        _cameraFrame[slot] = _ctx.FrameNumber;
    }

    /// <summary>Writes the lights block for this frame.</summary>
    public void WriteLights(LightEnvironment lights, Vector3 cameraPosition)
    {
        ArgumentNullException.ThrowIfNull(lights);
        if (!_ctx.FrameStarted) return;
        var slot = _ctx.FrameSlot;
        lights.WriteUbo(_buffers[slot].MappedSpan.Slice((int)_lightsOffset, LightEnvironment.UboSize), cameraPosition);
        _lightsFrame[slot] = _ctx.FrameNumber;
    }

    /// <summary>Writes the camera block unless something already did this frame.</summary>
    public void EnsureCamera(in Matrix4x4 view, in Matrix4x4 projection, Vector3 position)
    {
        if (_ctx.FrameStarted && _cameraFrame[_ctx.FrameSlot] != _ctx.FrameNumber)
            WriteCamera(view, projection, position);
    }

    /// <summary>Writes the lights block unless something already did this frame.</summary>
    public void EnsureLights(LightEnvironment lights, Vector3 cameraPosition)
    {
        if (_ctx.FrameStarted && _lightsFrame[_ctx.FrameSlot] != _ctx.FrameNumber)
            WriteLights(lights, cameraPosition);
    }

    /// <summary>True once this frame's camera block has been written.</summary>
    public bool HasCameraThisFrame => _ctx.FrameStarted && _cameraFrame[_ctx.FrameSlot] == _ctx.FrameNumber;

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
        _ctx.Deletions.Enqueue(GpuDeletion.Of(_pool));
        _ctx.Deletions.Enqueue(GpuDeletion.Of(SetLayout));
    }
}
