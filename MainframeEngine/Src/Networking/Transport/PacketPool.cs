using System.Buffers;

namespace MainframeEngine.Networking;

/// <summary>
/// Private array pools for the in-process transports' packet copies (<see cref="LoopbackTransport"/>,
/// <see cref="SimulatedTransport"/>).
/// </summary>
/// <remarks>
/// Not <see cref="ArrayPool{T}.Shared"/>: the shared pool's per-core partitions are used by every thread in the
/// process, so another thread renting the same size can take the array a transport just returned (and a Gen2 GC can
/// trim it), and the transport's next rent then allocates. A pool owned by the transport only ever sees that
/// transport's packets, so steady-state traffic reliably allocates nothing.
/// </remarks>
internal static class PacketPool
{
    /// <summary>Largest packet copy kept for reuse; larger ones are allocated and dropped (rare, oversize snapshots).</summary>
    public const int MaxPooledLength = 128 * 1024;

    /// <summary>Idle arrays kept per size class: covers every packet in flight at once in tests and listen servers.</summary>
    public const int ArraysPerSizeClass = 256;

    public static ArrayPool<byte> Create() => ArrayPool<byte>.Create(MaxPooledLength, ArraysPerSizeClass);
}
