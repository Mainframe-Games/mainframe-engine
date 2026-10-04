namespace MainframeEngine;

/// <summary>
/// Offset math for a dynamic-offset uniform ring: <c>framesInFlight × slotsPerFrame</c> elements, each
/// padded to the device's <c>minUniformBufferOffsetAlignment</c> (at least 256 bytes, the largest value
/// the spec allows, so the layout is identical on every device). A sub-pass writes its element at
/// <see cref="Offset"/> and binds the set with that dynamic offset, so each recorded pass keeps its own
/// data until the GPU executes it, and frame slots never overwrite each other.
/// </summary>
internal readonly record struct UniformRing
{
    /// <summary>Upper bound of <c>minUniformBufferOffsetAlignment</c> in the Vulkan spec.</summary>
    public const ulong MaxSpecAlignment = 256;

    public UniformRing(ulong elementSize, int slotsPerFrame, int framesInFlight, ulong minAlignment)
    {
        ArgumentOutOfRangeException.ThrowIfZero(elementSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(slotsPerFrame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(framesInFlight);
        if (minAlignment != 0 && (minAlignment & (minAlignment - 1)) != 0)
            throw new ArgumentException($"Alignment {minAlignment} is not a power of two.", nameof(minAlignment));

        ElementSize = elementSize;
        SlotsPerFrame = slotsPerFrame;
        FramesInFlight = framesInFlight;
        Stride = AlignUp(elementSize, Math.Max(MaxSpecAlignment, minAlignment));
    }

    public ulong ElementSize { get; }
    public int SlotsPerFrame { get; }
    public int FramesInFlight { get; }

    /// <summary>Bytes between consecutive elements (a multiple of the alignment).</summary>
    public ulong Stride { get; }

    /// <summary>Total buffer size.</summary>
    public ulong Size => Stride * (ulong)(SlotsPerFrame * FramesInFlight);

    /// <summary>Byte offset of <paramref name="slot"/> within <paramref name="frameSlot"/>'s region.</summary>
    public uint Offset(int frameSlot, int slot)
    {
        if ((uint)frameSlot >= (uint)FramesInFlight)
            throw new ArgumentOutOfRangeException(nameof(frameSlot));
        if ((uint)slot >= (uint)SlotsPerFrame)
            throw new ArgumentOutOfRangeException(nameof(slot));
        return checked((uint)(((ulong)frameSlot * (ulong)SlotsPerFrame + (ulong)slot) * Stride));
    }

    public static ulong AlignUp(ulong value, ulong alignment) => (value + alignment - 1) & ~(alignment - 1);
}
