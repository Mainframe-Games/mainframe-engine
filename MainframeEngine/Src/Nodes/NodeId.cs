using System.Runtime.CompilerServices;

namespace MainframeEngine.Networking;

/// <summary>
/// Represents a unique identifier for a network node within a networking system.
/// </summary>
/// <remarks>
/// This struct is designed to provide a lightweight and efficient way to identify and compare network nodes.
/// It supports comparison operations, equality checks, and implicit type conversions.
/// <para></para>
/// The Id of the node. Is unique. Starts at 1 so 0 can be considered error
/// </remarks>
public readonly struct NodeId(in uint id) : IEquatable<NodeId>, IEquatable<uint>
{
    private readonly uint _id = id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString() => $"(NodeId: {_id})";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _id.GetHashCode();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(uint other) => _id == other;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is NodeId nodeId && Equals(nodeId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(NodeId other) => _id == other._id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(NodeId a, NodeId b) => a._id == b._id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(NodeId a, NodeId b) => !(a == b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ulong(NodeId peerId) => peerId._id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator NodeId(uint id) => new(id);

    private static uint _nextId = 1;
    /// <summary>
    /// Retrieves the next unique <see cref="NodeId"/> to be assigned.
    /// </summary>
    /// <returns>A new <see cref="NodeId"/> instance representing the next unique identifier.</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static NodeId GetNext() => new(_nextId++);

}