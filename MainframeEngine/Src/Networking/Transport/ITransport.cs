namespace MainframeEngine.Networking;

/// <summary>Delivery channel. Every transport opens exactly these channels.</summary>
public enum NetChannel : byte
{
    /// <summary>Channel 0: reliable, ordered. Spawn/despawn, RPCs, control messages.</summary>
    Reliable = 0,

    /// <summary>Channel 1: unreliable, sequenced (late packets are dropped). State snapshots.</summary>
    Unreliable = 1,
}

/// <summary>
/// Why a peer went away. Values other than <see cref="Timeout"/> and <see cref="ConnectFailed"/> travel over the
/// wire as the disconnect data, so the remote side sees the reason its peer gave.
/// </summary>
public enum DisconnectReason : uint
{
    /// <summary>Closed normally (remote <c>Disconnect</c> without a reason, or local shutdown).</summary>
    Closed = 0,

    /// <summary>The peer stopped answering (detected locally).</summary>
    Timeout = 1,

    /// <summary>The peer's protocol version / message registry does not match ours.</summary>
    ProtocolMismatch = 2,

    /// <summary>Removed by the server.</summary>
    Kicked = 3,

    /// <summary>The other side is shutting down.</summary>
    Shutdown = 4,

    /// <summary>A client's connection attempt never completed (detected locally).</summary>
    ConnectFailed = 5,
}

/// <summary>
/// Receives a transport's events during <see cref="ITransport.Poll"/>. Implemented by <see cref="MessageBus"/>;
/// an interface (not events) so the receive path can pass spans and never allocates.
/// </summary>
public interface ITransportListener
{
    /// <summary>A peer connected. <paramref name="connectData"/> is the value the connecting side passed (servers only).</summary>
    void OnPeerConnected(PeerId peer, uint connectData);

    /// <summary>A peer disconnected or timed out. The id is never reused for another connection.</summary>
    void OnPeerDisconnected(PeerId peer, DisconnectReason reason);

    /// <summary>A packet arrived. <paramref name="payload"/> is only valid during the call.</summary>
    void OnReceive(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload);
}

/// <summary>
/// Moves byte packets between peers over two channels (<see cref="NetChannel"/>). Implementations: <see cref="EnetTransport"/>
/// (UDP), <see cref="LoopbackTransport"/> (in-process). Single-threaded: call every member from the game loop.
/// </summary>
public interface ITransport : IDisposable
{
    /// <summary>True for the accepting side (server / listen host), false for a client.</summary>
    bool IsServer { get; }

    /// <summary>Delivers pending events to <paramref name="listener"/> without blocking.</summary>
    void Poll(ITransportListener listener);

    /// <summary>
    /// Queues <paramref name="payload"/> (copied) for <paramref name="peer"/>. Returns false, without throwing, when the
    /// peer is unknown or the transport refuses the packet.
    /// </summary>
    bool Send(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload);

    /// <summary>Starts a graceful disconnect; <see cref="ITransportListener.OnPeerDisconnected"/> follows on a later poll.</summary>
    void Disconnect(PeerId peer, DisconnectReason reason);

    /// <summary>Sends queued packets now instead of on the next <see cref="Poll"/>.</summary>
    void Flush();
}
