using System.Runtime.CompilerServices;

namespace MainframeEngine.Networking;

/// <summary>
/// Represents a unique identifier for a network peer.
/// Provides equality comparison and implicit conversion mechanisms
/// to and from a <see cref="ulong"/> value.
/// </summary>
public readonly struct NetworkPeerId(in ulong id) : IEquatable<NetworkPeerId>, IEquatable<ulong>
{
    private readonly ulong _id = id;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override string ToString() => $"(PeerId: {_id})";

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override int GetHashCode() => _id.GetHashCode();
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(ulong other) => _id == other;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public override bool Equals(object? obj) => obj is NetworkPeerId peerId && Equals(peerId);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(NetworkPeerId other) => _id == other._id;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator ==(NetworkPeerId a, NetworkPeerId b) => a._id == b._id;
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool operator !=(NetworkPeerId a, NetworkPeerId b) => !(a == b);
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator ulong(NetworkPeerId peerId) => peerId._id;  
    
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static implicit operator NetworkPeerId(ulong id) => new(id);
}