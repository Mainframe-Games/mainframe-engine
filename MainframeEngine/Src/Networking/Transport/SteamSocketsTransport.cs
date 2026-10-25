using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

/// <summary>
/// <see cref="ITransport"/> over Steam Networking Sockets relayed by Valve's network (SDR, Facepunch's
/// <c>CreateRelaySocket</c>/<c>ConnectRelay</c>): peers reach each other by SteamID, without open ports or a known IP.
/// Available while <see cref="Steam.Valid"/>.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>Both channels share one connection: <see cref="NetChannel.Reliable"/> is sent
/// <c>k_nSteamNetworkingSend_Reliable</c> (flushed by <see cref="Flush"/>), <see cref="NetChannel.Unreliable"/>
/// <c>k_nSteamNetworkingSend_UnreliableNoNagle</c>. The receiver tells them apart by the message's reliable flag, and
/// drops an unreliable message older than the last one it delivered (sequenced, like ENet's unreliable channel).</description></item>
/// <item><description>Steam has no connect data, so a client's first message is a reliable 8-byte hello carrying it (the
/// <see cref="MessageRegistry.Fingerprint"/>). The host reports the peer connected only once the hello arrives, and keeps
/// the value as the connection's Steam user data.</description></item>
/// <item><description>The host is the server itself (<see cref="MultiplayerApi.ServerPeerId"/>): it never connects to its own
/// socket. One Steam account cannot relay-connect to itself, so <see cref="TryConnect(ulong, int, uint, out ITransport)"/>
/// refuses this account's own SteamID at once and a lobby connect string falls through to its next address.</description></item>
/// <item><description><see cref="PeerId"/>s count up from 1 per transport and are never reused; <see cref="GetRemoteSteamId"/>
/// gives a peer's SteamID. Disconnect reasons travel as Steam app end reasons (1000 + <see cref="DisconnectReason"/>).</description></item>
/// <item><description>Polling reads each message in place (a span over Steam's buffer, released after the listener
/// returns) into a stack-allocated batch, so steady-state receiving allocates nothing; sends are copied into Facepunch's
/// pooled native buffers.</description></item>
/// </list>
/// Connection state changes arrive in <see cref="Steam.RunCallbacks"/> (the engine's <c>SteamServer</c>); they are queued
/// and reported by the next <see cref="Poll"/>. Not thread-safe: use one transport from the main thread.
/// </remarks>
public sealed class SteamSocketsTransport : ITransport
{
    /// <summary>Steam's first application end reason (<c>k_ESteamNetConnectionEnd_App_Min</c>): disconnect reasons are sent as this plus <see cref="DisconnectReason"/>.</summary>
    public const int AppEndReasonBase = 1000;

    /// <summary>Messages taken from Steam per receive call (a stack buffer of handles).</summary>
    public const int ReceiveBatchSize = 32;

    /// <summary>The client's hello: "MFS1" then the connect data, little-endian.</summary>
    internal const uint HelloMagic = 0x3153_464D;

    internal const int HelloSize = 8;

    private enum PeerState : byte
    {
        /// <summary>Host: accepted, Steam has not finished connecting.</summary>
        Accepting,

        /// <summary>Host: connected, waiting for the hello. Client: connecting.</summary>
        Pending,

        Connected,
    }

    [StructLayout(LayoutKind.Auto)]
    private struct Peer
    {
        public PeerId    Id;
        public ulong     SteamId;
        public long      LastUnreliable;
        public PeerState State;
    }

    private readonly ISteamSocketsBackend    _backend;
    private readonly int                     _endpoint;
    private readonly int                     _maxPeers;
    private readonly Dictionary<uint, Peer>  _peers       = [];
    private readonly Dictionary<PeerId, uint> _connections = [];
    private readonly List<uint>              _pollList    = [];
    private readonly Queue<(PeerId Peer, DisconnectReason Reason)> _localDisconnects = new();
    private uint  _connectData;
    private ulong _nextPeerId = 1;
    private int   _connectedCount;
    private bool  _disposed;
    private bool  _steamLost; // Steam shut down while open: the handles died with it, even if Steam starts again

    private SteamSocketsTransport(ISteamSocketsBackend backend, int endpoint, bool isServer, int maxPeers)
    {
        _backend  = backend;
        _endpoint = endpoint;
        _maxPeers = maxPeers;
        IsServer  = isServer;
    }

    /// <summary>True while Steam is running (<see cref="Steam.Valid"/>): relay sockets can be opened.</summary>
    public static bool IsAvailable => Steam.Valid;

    /// <inheritdoc />
    public bool IsServer { get; }

    /// <summary>Peers currently connected (on a host: past the hello).</summary>
    public int ConnectedPeerCount => _connectedCount;

    /// <summary>
    /// Opens a relay listen socket on <paramref name="virtualPort"/> for up to <paramref name="maxPeers"/> clients, who
    /// connect with this user's SteamID (<c>steam:&lt;id&gt;</c>, <see cref="NetworkAddress.Steam"/>). False when Steam is
    /// not running or refuses the socket.
    /// </summary>
    public static bool TryListen(int virtualPort, int maxPeers, [NotNullWhen(true)] out ITransport? transport) =>
        TryListen(SteamSocketsApi.Instance, virtualPort, maxPeers, out transport);

    /// <summary>
    /// Starts a relay connection to the host <paramref name="steamId"/>. Completion is reported by later polls (connected,
    /// or <see cref="DisconnectReason.ConnectFailed"/>). False when Steam is not running, the id is this user's own, or
    /// Steam refuses the connection.
    /// </summary>
    /// <param name="steamId">The host's SteamID.</param>
    /// <param name="virtualPort">The host's virtual port.</param>
    /// <param name="connectData">Value the host receives with the connection (the message registry fingerprint).</param>
    /// <param name="transport">The connecting transport.</param>
    public static bool TryConnect(ulong steamId, int virtualPort, uint connectData, [NotNullWhen(true)] out ITransport? transport) =>
        TryConnect(SteamSocketsApi.Instance, steamId, virtualPort, connectData, out transport, out _);

    /// <summary>
    /// Steam's built-in network conditioner (global: every Steam connection of the process): fake <paramref name="lagMs"/>
    /// of lag and <paramref name="lossPercent"/> % loss, each on sends and on receives; zeros turn it off. For testing
    /// only. False when Steam is not running.
    /// </summary>
    public static bool SimulateNetworkConditions(float lagMs, float lossPercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lagMs);
        ArgumentOutOfRangeException.ThrowIfNegative(lossPercent);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lossPercent, 100f);
        if (!Steam.Valid)
            return false;
        SteamSocketsApi.SetFakeConditions(lagMs, lossPercent);
        return true;
    }

    internal static bool TryListen(ISteamSocketsBackend backend, int virtualPort, int maxPeers, [NotNullWhen(true)] out ITransport? transport)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(virtualPort);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPeers, 1);
        transport = null;
        if (!backend.IsRunning)
            return false;

        var endpoint = backend.Listen(virtualPort);
        if (endpoint == 0)
            return false;
        Log.Info($"[Net] Steam relay host listening on virtual port {virtualPort} (max {maxPeers} peers)");
        transport = new SteamSocketsTransport(backend, endpoint, isServer: true, maxPeers);
        return true;
    }

    internal static bool TryConnect(ISteamSocketsBackend backend, ulong steamId, int virtualPort, uint connectData,
        [NotNullWhen(true)] out ITransport? transport, out string? failure)
    {
        ArgumentOutOfRangeException.ThrowIfZero(steamId);
        ArgumentOutOfRangeException.ThrowIfNegative(virtualPort);
        transport = null;
        if (!backend.IsRunning)
        {
            failure = "Steam is not running";
            return false;
        }

        if (steamId == backend.LocalSteamId)
        {
            failure = "it is this Steam account (one account cannot relay-connect to itself)";
            return false;
        }

        var endpoint = backend.Connect(steamId, virtualPort, out var connection);
        if (endpoint == 0)
        {
            failure = "Steam refused the connection";
            return false;
        }

        var client = new SteamSocketsTransport(backend, endpoint, isServer: false, maxPeers: 1) { _connectData = connectData };
        var id = client.NextPeerId();
        client._peers[connection] = new Peer { Id = id, SteamId = steamId, State = PeerState.Pending };
        client._connections[id] = connection;
        Log.Info($"[Net] Steam relay client connecting to {steamId}");
        transport = client;
        failure = null;
        return true;
    }

    /// <summary>The SteamID of a peer (pending or connected), or 0 if it is unknown.</summary>
    public ulong GetRemoteSteamId(PeerId peer) =>
        !_disposed && _connections.TryGetValue(peer, out var connection) ? _peers[connection].SteamId : 0;

    /// <inheritdoc />
    public void Poll(ITransportListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ObjectDisposedException.ThrowIf(_disposed, this);

        while (!_disposed && _localDisconnects.TryDequeue(out var local))
            listener.OnPeerDisconnected(local.Peer, local.Reason);

        if (!SteamAlive)
        {
            _steamLost = true;
            DropEverything(listener);
            return;
        }

        while (!_disposed && _backend.TryDequeueEvent(_endpoint, out var e))
            OnEvent(listener, in e);

        // A handler may disconnect peers or dispose the transport mid-poll: iterate a snapshot, re-checked per message.
        _pollList.Clear();
        foreach (var (connection, peer) in _peers)
        {
            if (IsReceiving(peer.State))
                _pollList.Add(connection);
        }

        foreach (var connection in _pollList)
            ReceiveAll(listener, connection);
    }

    // Connected peers, and a host's peers that still owe the hello.
    private bool IsReceiving(PeerState state) => state == PeerState.Connected || (IsServer && state == PeerState.Pending);

    private void ReceiveAll(ITransportListener listener, uint connection)
    {
        Span<nint> batch = stackalloc nint[ReceiveBatchSize];
        while (!_disposed && _peers.ContainsKey(connection))
        {
            var count = _backend.Receive(connection, batch);
            if (count <= 0)
                break;
            Deliver(listener, connection, batch[..count]);
            if (count < batch.Length)
                break;
        }
    }

    // Hands every message of a batch to the listener and releases all of them, even if a handler throws.
    private void Deliver(ITransportListener listener, uint connection, Span<nint> messages)
    {
        try
        {
            for (var i = 0; i < messages.Length; i++)
            {
                var message = messages[i];
                messages[i] = 0;
                try
                {
                    if (!_disposed)
                        OnMessage(listener, connection, _backend.ReadMessage(message));
                }
                finally
                {
                    _backend.ReleaseMessage(message);
                }
            }
        }
        finally
        {
            foreach (var message in messages)
            {
                if (message != 0)
                    _backend.ReleaseMessage(message);
            }
        }
    }

    private unsafe void OnMessage(ITransportListener listener, uint connection, in SteamSocketMessage message)
    {
        ref var peer = ref CollectionsMarshal.GetValueRefOrNullRef(_peers, connection);
        if (Unsafe.IsNullRef(ref peer))
            return; // disconnected by an earlier handler
        var payload = new ReadOnlySpan<byte>((void*)message.Data, message.Size);
        var id = peer.Id;

        if (peer.State == PeerState.Pending) // a host waiting for the hello; other messages before it are dropped
        {
            if (!message.Reliable)
                return;
            if (!TryReadHello(payload, out var connectData))
            {
                Log.Warning($"[Net] Steam peer {peer.SteamId} sent no hello; closing");
                Remove(connection);
                _backend.Close(connection, AppEndReasonBase + (int)DisconnectReason.ProtocolMismatch, linger: false);
                return;
            }

            peer.State = PeerState.Connected;
            _connectedCount++;
            _backend.SetUserData(connection, connectData);
            Log.Debug($"[Net] Steam peer connected {id} ({peer.SteamId})");
            listener.OnPeerConnected(id, connectData);
            return;
        }

        if (peer.State != PeerState.Connected)
            return;

        var channel = NetChannel.Reliable;
        if (!message.Reliable)
        {
            if (message.Number <= peer.LastUnreliable)
                return; // late: a newer unreliable message was delivered already
            peer.LastUnreliable = message.Number;
            channel = NetChannel.Unreliable;
        }

        listener.OnReceive(id, channel, payload);
    }

    private void OnEvent(ITransportListener listener, in SteamSocketEvent e)
    {
        switch (e.Kind)
        {
            case SteamSocketEventKind.Connecting when IsServer:
                if (_peers.ContainsKey(e.Connection))
                    return;
                if (_peers.Count >= _maxPeers)
                {
                    Log.Info($"[Net] Steam host full, refusing {e.SteamId}");
                    _backend.Close(e.Connection, AppEndReasonBase + (int)DisconnectReason.Kicked, linger: false);
                    return;
                }

                if (!_backend.Accept(e.Connection))
                {
                    _backend.Close(e.Connection, 0, linger: false);
                    return;
                }

                _peers[e.Connection] = new Peer { SteamId = e.SteamId, State = PeerState.Accepting };
                return;

            case SteamSocketEventKind.Connected:
                {
                    ref var peer = ref CollectionsMarshal.GetValueRefOrNullRef(_peers, e.Connection);
                    if (Unsafe.IsNullRef(ref peer))
                        return;
                    if (IsServer)
                    {
                        if (peer.State != PeerState.Accepting)
                            return;
                        peer.Id = NextPeerId();
                        peer.State = PeerState.Pending;
                        _connections[peer.Id] = e.Connection;
                        return;
                    }

                    if (peer.State != PeerState.Pending)
                        return;
                    Span<byte> hello = stackalloc byte[HelloSize];
                    WriteHello(hello, _connectData);
                    if (!_backend.Send(e.Connection, hello, reliable: true))
                    {
                        Close(listener, e.Connection, DisconnectReason.ConnectFailed);
                        return;
                    }

                    peer.State = PeerState.Connected;
                    _connectedCount++;
                    var id = peer.Id;
                    Log.Debug($"[Net] Steam relay connected to {peer.SteamId} as {id}");
                    listener.OnPeerConnected(id, 0);
                    return;
                }

            case SteamSocketEventKind.ClosedByPeer:
                Close(listener, e.Connection, ToDisconnectReason(e.EndReason));
                return;

            case SteamSocketEventKind.ProblemDetectedLocally:
                Close(listener, e.Connection, DisconnectReason.Timeout);
                return;

            case SteamSocketEventKind.Connecting:
            case SteamSocketEventKind.Other:
            default:
                return;
        }
    }

    // The connection ended (closed remotely, or a problem detected locally): hand over what it received before (a
    // closed connection keeps its unread messages until its handle is freed, so data sent before a kick still arrives
    // first, as with ENet), free its handle, then tell the listener if it knew the peer.
    private void Close(ITransportListener listener, uint connection, DisconnectReason reason)
    {
        if (!_peers.TryGetValue(connection, out var peer))
            return;
        if (IsReceiving(peer.State))
        {
            ReceiveAll(listener, connection);
            if (_disposed || !_peers.TryGetValue(connection, out peer))
                return; // a handler disconnected it (already reported) or disposed the transport
        }

        Remove(connection);
        _backend.Close(connection, 0, linger: false);
        if (peer.State == PeerState.Connected)
        {
            Log.Debug($"[Net] Steam peer disconnected {peer.Id}: {reason}");
            listener.OnPeerDisconnected(peer.Id, reason);
        }
        else if (!IsServer)
        {
            Log.Debug($"[Net] Steam relay connection to {peer.SteamId} failed ({reason})");
            listener.OnPeerDisconnected(peer.Id, DisconnectReason.ConnectFailed);
        }
    }

    private void Remove(uint connection)
    {
        if (!_peers.Remove(connection, out var peer))
            return;
        if (peer.State != PeerState.Accepting)
            _connections.Remove(peer.Id);
        if (peer.State == PeerState.Connected)
            _connectedCount--;
    }

    // Steam stopped (Steam.Shutdown) while the transport was open: every handle is gone with it.
    private void DropEverything(ITransportListener listener)
    {
        while (_peers.Count > 0 && !_disposed)
        {
            var (connection, peer) = _peers.First();
            Remove(connection);
            if (peer.State == PeerState.Connected)
                listener.OnPeerDisconnected(peer.Id, DisconnectReason.Shutdown);
            else if (!IsServer)
                listener.OnPeerDisconnected(peer.Id, DisconnectReason.ConnectFailed);
        }
    }

    internal static DisconnectReason ToDisconnectReason(int endReason)
    {
        var reason = endReason - AppEndReasonBase;
        return reason is >= 0 and <= (int)DisconnectReason.ConnectFailed && Enum.IsDefined((DisconnectReason)reason)
            ? (DisconnectReason)reason
            : DisconnectReason.Closed;
    }

    internal static void WriteHello(Span<byte> destination, uint connectData)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination, HelloMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[4..], connectData);
    }

    internal static bool TryReadHello(ReadOnlySpan<byte> payload, out uint connectData)
    {
        if (payload.Length != HelloSize || BinaryPrimitives.ReadUInt32LittleEndian(payload) != HelloMagic)
        {
            connectData = 0;
            return false;
        }

        connectData = BinaryPrimitives.ReadUInt32LittleEndian(payload[4..]);
        return true;
    }

    /// <inheritdoc />
    public bool Send(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        if ((byte)channel > (byte)NetChannel.Unreliable)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown channel.");
        if (_disposed || !SteamAlive || !TryGetConnected(peer, out var connection))
            return false;
        return _backend.Send(connection, payload, channel == NetChannel.Reliable);
    }

    /// <inheritdoc />
    /// <remarks>Reliable data still queued is sent before the connection closes (Steam's linger).</remarks>
    public void Disconnect(PeerId peer, DisconnectReason reason)
    {
        if (_disposed || !_connections.TryGetValue(peer, out var connection))
            return;
        var state = _peers[connection].State;
        Remove(connection);
        if (SteamAlive)
            _backend.Close(connection, AppEndReasonBase + (int)reason, linger: true);
        if (state == PeerState.Connected)
            _localDisconnects.Enqueue((peer, reason));
        else if (!IsServer)
            _localDisconnects.Enqueue((peer, DisconnectReason.ConnectFailed));
    }

    /// <inheritdoc />
    public void Flush()
    {
        if (_disposed || !SteamAlive)
            return;
        foreach (var (connection, peer) in _peers)
        {
            if (peer.State == PeerState.Connected)
                _backend.Flush(connection);
        }
    }

    /// <summary>Closes every connection (peers are told <see cref="DisconnectReason.Shutdown"/>) and the listen socket.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (SteamAlive)
        {
            foreach (var connection in _peers.Keys)
                _backend.Close(connection, AppEndReasonBase + (int)DisconnectReason.Shutdown, linger: false);
            _backend.CloseEndpoint(_endpoint);
        }

        _peers.Clear();
        _connections.Clear();
        _localDisconnects.Clear();
        _connectedCount = 0;
    }

    private bool SteamAlive => !_steamLost && _backend.IsRunning;

    private PeerId NextPeerId() => new(_nextPeerId++);

    private bool TryGetConnected(PeerId peer, out uint connection) =>
        _connections.TryGetValue(peer, out connection) && _peers[connection].State == PeerState.Connected;
}

/// <summary>What happened to a Steam connection (<c>ESteamNetworkingConnectionState</c>, reduced).</summary>
internal enum SteamSocketEventKind : byte
{
    /// <summary>Finding a route, or none (after a local close): nothing to do.</summary>
    Other,

    /// <summary>Host: a client wants in (accept or close it).</summary>
    Connecting,

    Connected,

    /// <summary>The remote side closed it; <see cref="SteamSocketEvent.EndReason"/> says why.</summary>
    ClosedByPeer,

    /// <summary>Timed out or failed locally.</summary>
    ProblemDetectedLocally,
}

/// <summary>A connection state change of a Steam sockets endpoint.</summary>
internal readonly record struct SteamSocketEvent(uint Connection, SteamSocketEventKind Kind, ulong SteamId, int EndReason);

/// <summary>A received Steam message, read in place: <see cref="Data"/> is valid until the message is released.</summary>
internal readonly record struct SteamSocketMessage(nint Data, int Size, bool Reliable, long Number);

/// <summary>
/// The Steam Networking Sockets calls <see cref="SteamSocketsTransport"/> makes: <see cref="SteamSocketsApi"/> (the
/// shim) in games, an in-memory fake in tests. Engine types only.
/// </summary>
internal interface ISteamSocketsBackend
{
    /// <summary>Steam is running; false makes every other call off limits.</summary>
    bool IsRunning { get; }

    ulong LocalSteamId { get; }

    /// <summary>Opens a relay listen socket: an endpoint id, or 0.</summary>
    int Listen(int virtualPort);

    /// <summary>Starts a relay connection: an endpoint id (0 on failure) and its connection handle.</summary>
    int Connect(ulong steamId, int virtualPort, out uint connection);

    void CloseEndpoint(int endpoint);

    bool TryDequeueEvent(int endpoint, out SteamSocketEvent socketEvent);

    bool Accept(uint connection);

    void Close(uint connection, int reasonCode, bool linger);

    void SetUserData(uint connection, long userData);

    bool Send(uint connection, ReadOnlySpan<byte> payload, bool reliable);

    void Flush(uint connection);

    /// <summary>Fills <paramref name="messages"/> with received message handles; returns how many.</summary>
    int Receive(uint connection, Span<nint> messages);

    SteamSocketMessage ReadMessage(nint message);

    void ReleaseMessage(nint message);
}
