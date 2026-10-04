namespace MainframeEngine;

/// <summary>
/// Marks a property or field of a <see cref="Node"/> as replicated: the server sends its value to every client when
/// it changes (delta snapshots at <see cref="Networking.MultiplayerApi.TickRate"/>), and clients apply it. The
/// member must be readable and writable from the declaring assembly (public or internal).
/// <c>MainframeEngine.Generators</c> emits typed change detection and serialization for it; no reflection runs.
/// </summary>
/// <remarks>
/// Supported types: <c>bool</c>, integer and floating-point primitives, <c>decimal</c>, <c>char</c>, <c>string</c>,
/// enums, <c>Vector2/3/4</c>, <c>Quaternion</c>, <c>System.Drawing.Color</c>, <see cref="Transform3D"/>,
/// <see cref="Transform2D"/>, <see cref="Networking.PeerId"/> and structs implementing both
/// <see cref="Networking.INetworkTransferable"/> and <see cref="IEquatable{T}"/>. A type may declare at most 64.
/// </remarks>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, Inherited = true)]
public sealed class ReplicatedAttribute : Attribute
{
    /// <summary>
    /// Clients render the value <see cref="Networking.MultiplayerApi.InterpolationDelay"/> in the past, blending
    /// between snapshots (and extrapolating briefly when they are late) instead of snapping. For <c>float</c>,
    /// <c>double</c>, <c>Vector2/3/4</c> and <c>Quaternion</c> (slerp).
    /// </summary>
    public bool Interpolate { get; set; }
}

/// <summary>Who may call an <see cref="RpcAttribute"/> method. The server may always call every RPC.</summary>
public enum RpcMode : byte
{
    /// <summary>Server → clients only; calls from clients are refused.</summary>
    Server = 0,

    /// <summary>The node's <see cref="Node.NetworkAuthority"/> (and the server). A client that owns the node calls it on the server.</summary>
    Authority = 1,

    /// <summary>Any peer. Server-side handlers should validate <see cref="Networking.MultiplayerApi.RemoteSender"/>.</summary>
    AnyPeer = 2,
}

/// <summary>
/// Marks a method of a <see cref="Node"/> as a remote procedure call. The generator emits
/// <c>node.Rpc&lt;Method&gt;(args)</c> (server → every client, client → server) and <c>node.Rpc&lt;Method&gt;To(peer, args)</c>
/// extension methods and a typed dispatcher. The method must be public or internal, return void and take
/// by-value parameters of the <see cref="ReplicatedAttribute"/> types.
/// </summary>
/// <param name="mode">Who may call it (default <see cref="RpcMode.Authority"/>).</param>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
public sealed class RpcAttribute(RpcMode mode = RpcMode.Authority) : Attribute
{
    public RpcMode Mode { get; } = mode;

    /// <summary>Reliable, ordered delivery (default). False uses the unreliable channel (latest-wins effects).</summary>
    public bool Reliable { get; set; } = true;

    /// <summary>Also run the method on the calling peer.</summary>
    public bool CallLocal { get; set; }
}
