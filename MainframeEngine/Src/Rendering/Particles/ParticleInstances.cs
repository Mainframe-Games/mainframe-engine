using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace MainframeEngine;

/// <summary>One particle as <c>Particles/Particle.vk.vert</c> reads it (binding 1, instance rate; 48 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ParticleInstance
{
    /// <summary>xyz = position (world, or the emitter's space with local coordinates), w = scale.</summary>
    public Vector4 PositionScale;

    /// <summary>xyz = velocity (same space), w = angle in radians.</summary>
    public Vector4 VelocityAngle;

    /// <summary>Linear RGBA: the process material's colour × initial ramp (hue-turned) × colour ramp at its age.</summary>
    public Vector4 Color;

    public const int Size = 48;
}

/// <summary>Builds a frame's <see cref="ParticleInstance"/>s from a simulation: order, interpolation, curves and colour (ADR 0206).</summary>
internal static class ParticleInstances
{
    /// <summary>
    /// Writes the live particles into <paramref name="destination"/> in <paramref name="order"/> (back to front from
    /// <paramref name="cameraPosition"/> along <paramref name="cameraForward"/> for <see cref="GpuParticles3D.DrawOrderEnum.ViewDepth"/>),
    /// drawn <paramref name="interpolation"/> of the way from their previous step to their current one, with the material's
    /// scale curve and colour ramp at age / life. <paramref name="indices"/> and <paramref name="keys"/> are scratch arrays
    /// at least as long as the particles. Returns the particles written. Allocation-free.
    /// </summary>
    public static int Write(ReadOnlySpan<Particle3D> particles, ParticleProcessMaterial? material, float lifetime, float interpolation,
        GpuParticles3D.DrawOrderEnum order, in Matrix4x4 toWorld, Vector3 cameraPosition, Vector3 cameraForward,
        Span<int> indices, Span<float> keys, Span<ParticleInstance> destination)
    {
        var count = 0;
        var t = Math.Clamp(interpolation, 0f, 1f);
        for (var i = 0; i < particles.Length && count < destination.Length; i++)
        {
            ref readonly var p = ref particles[i];
            if (!p.Alive)
                continue;
            indices[count] = i;
            switch (order)
            {
                case GpuParticles3D.DrawOrderEnum.ViewDepth:
                    var world = Vector3.Transform(Vector3.Lerp(p.PreviousPosition, p.Position, t), toWorld);
                    keys[count] = -Vector3.Dot(world - cameraPosition, cameraForward); // farthest first
                    break;
                case GpuParticles3D.DrawOrderEnum.Lifetime:
                    keys[count] = Remaining(p, lifetime); // the oldest (least left) first, the youngest on top
                    break;
                case GpuParticles3D.DrawOrderEnum.ReverseLifetime:
                    keys[count] = -Remaining(p, lifetime);
                    break;
            }

            count++;
        }

        if (order != GpuParticles3D.DrawOrderEnum.Index && count > 1)
            keys[..count].Sort(indices[..count]);

        var scaleCurve = material?.ScaleCurve;
        var ramp = material?.ColorRamp;
        for (var k = 0; k < count; k++)
        {
            ref readonly var p = ref particles[indices[k]];
            var life = Math.Clamp(p.Age / (p.Life > 0f ? p.Life : lifetime), 0f, 1f);
            var scale = p.Scale * (scaleCurve?.Sample(life) ?? 1f);
            var color = Linear(p.Color);
            if (ramp is not null)
                color *= Linear(ramp.Sample(life));
            var position = Vector3.Lerp(p.PreviousPosition, p.Position, t);
            var angle = p.PreviousAngle + (p.Angle - p.PreviousAngle) * t;
            destination[k] = new ParticleInstance
            {
                PositionScale = new Vector4(position, scale),
                VelocityAngle = new Vector4(p.Velocity, angle),
                Color = color,
            };
        }

        return count;
    }

    private static float Remaining(in Particle3D p, float lifetime) => (p.Life > 0f ? p.Life : lifetime) - p.Age;

    /// <summary>A straight sRGB colour (components may exceed 1) in linear light, alpha kept.</summary>
    public static Vector4 Linear(Vector4 c) =>
        new(ColorSpace.SrgbToLinear(c.X), ColorSpace.SrgbToLinear(c.Y), ColorSpace.SrgbToLinear(c.Z), c.W);
}

/// <summary>
/// Per-frame-slot streaming buffer of <see cref="ParticleInstance"/>s (a bump allocator reset each frame, keyed by frame
/// number, like the meshes' <see cref="InstanceBuffer"/>); grows by doubling, the old buffer going through the deletion
/// queue. Steady-state frames allocate nothing.
/// </summary>
internal sealed class ParticleInstanceRing : IDisposable
{
    private const int Slots = IVulkanContext.MaxFramesInFlight;
    private const int InitialCapacity = 4096;

    private readonly IVulkanContext _ctx;
    private readonly GpuBuffer?[] _buffers = new GpuBuffer?[Slots];
    private readonly ulong[] _frame = new ulong[Slots];
    private readonly int[] _used = new int[Slots];
    private bool _disposed;

    public ParticleInstanceRing(IVulkanContext ctx) => _ctx = ctx;

    /// <summary>Instances written this frame (diagnostics, tests).</summary>
    public int UsedThisFrame => _frame[_ctx.FrameSlot] == _ctx.FrameNumber ? _used[_ctx.FrameSlot] : 0;

    /// <summary>Reserves <paramref name="count"/> instances in this frame's buffer: draw them with <c>firstInstance</c> = <paramref name="first"/>.</summary>
    public Span<ParticleInstance> Allocate(int count, out GpuBuffer buffer, out uint first)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var slot = _ctx.FrameSlot;
        if (_frame[slot] != _ctx.FrameNumber)
        {
            _frame[slot] = _ctx.FrameNumber; // the slot's fence has been waited: its buffer is free again
            _used[slot] = 0;
        }

        var current = _buffers[slot];
        var capacity = (int)((current?.Size ?? 0) / ParticleInstance.Size);
        if (current is null || _used[slot] + count > capacity)
        {
            var grown = Math.Max(InitialCapacity, Math.Max(capacity * 2, _used[slot] + count));
            current?.Dispose(); // draws recorded earlier this frame keep it alive until the frame finishes
            current = GpuBuffer.Create(_ctx, (ulong)grown * ParticleInstance.Size, BufferUsageFlags.VertexBufferBit, GpuMemoryUsage.Dynamic);
            _buffers[slot] = current;
            _used[slot] = 0;
        }

        buffer = current;
        first = (uint)_used[slot];
        _used[slot] += count;
        return MemoryMarshal.Cast<byte, ParticleInstance>(current.MappedSpan).Slice((int)first, count);
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
