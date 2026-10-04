using System.Runtime.CompilerServices;

namespace MainframeEngine.Networking;

/// <summary>
/// Represents a unique identifier for a network peer.
/// Provides equality comparison and implicit conversion mechanisms
/// to and from a <see cref="ulong"/> value.
/// </summary>
public readonly struct PeerId(in ulong id) : IEquatable<PeerId>, IEquatable<ulong>
{
    private readonly ulong _id = id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString() => $"(PeerId: {_id})";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _id.GetHashCode();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ulong other) => _id == other;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is PeerId peerId && Equals(peerId);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(PeerId other) => _id == other._id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(PeerId a, PeerId b) => a._id == b._id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(PeerId a, PeerId b) => !(a == b);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ulong(PeerId peerId) => peerId._id;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator PeerId(ulong id) => new(id);
}