namespace MainframeEngine.Networking;

/// <summary>
/// One server over several accepting transports at once, e.g. Steam relay sockets and ENet, so a host is reachable at
/// every address its lobby's connect string advertises (<c>steam:…;enet:…</c>). Peers of all of them get ids of this
/// transport (counting up from 1, never reused); sends and disconnects go to the transport the peer came from.
/// </summary>
/// <example>
/// <code>
/// ITransport server = EnetTransport.Listen(7777, 8);
/// if (SteamSocketsTransport.TryListen(0, 8, out var steam))
///     server = new MultiServerTransport(steam, server);
/// mp.StartServer(server);
/// </code>
/// </example>
public sealed class MultiServerTransport : ITransport
{
    private readonly ITransport[] _transports;
    private readonly Relay[]      _relays;
    private readonly Dictionary<PeerId, (int Transport, PeerId Inner)> _peers = [];
    private ulong _nextPeerId = 1;
    private bool  _disposed;

    /// <summary>Owns <paramref name="transports"/> (accepting transports, at least one) and disposes them with itself.</summary>
    public MultiServerTransport(params ITransport[] transports)
    {
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentOutOfRangeException.ThrowIfZero(transports.Length);
        _transports = [.. transports];
        _relays = new Relay[_transports.Length];
        for (var i = 0; i < _transports.Length; i++)
        {
            ArgumentNullException.ThrowIfNull(_transports[i], nameof(transports));
            if (!_transports[i].IsServer)
                throw new ArgumentException("Every transport must accept connections.", nameof(transports));
            _relays[i] = new Relay(this, i);
        }
    }

    /// <inheritdoc />
    public bool IsServer => true;

    /// <summary>The transports this server listens on.</summary>
    public IReadOnlyList<ITransport> Transports => _transports;

    /// <summary>Connected peers over all transports.</summary>
    public int PeerCount => _peers.Count;

    /// <summary>The transport a peer is connected through and its id there; false if the peer is unknown.</summary>
    public bool TryGetInnerPeer(PeerId peer, out ITransport transport, out PeerId innerPeer)
    {
        if (!_disposed && _peers.TryGetValue(peer, out var entry))
        {
            transport = _transports[entry.Transport];
            innerPeer = entry.Inner;
            return true;
        }

        transport = null!;
        innerPeer = default;
        return false;
    }

    /// <inheritdoc />
    public void Poll(ITransportListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ObjectDisposedException.ThrowIf(_disposed, this);
        for (var i = 0; i < _transports.Length && !_disposed; i++)
        {
            var relay = _relays[i];
            relay.Listener = listener;
            try
            {
                _transports[i].Poll(relay);
            }
            finally
            {
                relay.Listener = null;
            }
        }
    }

    /// <inheritdoc />
    public bool Send(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload) =>
        !_disposed && _peers.TryGetValue(peer, out var entry) && _transports[entry.Transport].Send(entry.Inner, channel, payload);

    /// <inheritdoc />
    public void Disconnect(PeerId peer, DisconnectReason reason)
    {
        if (!_disposed && _peers.TryGetValue(peer, out var entry))
            _transports[entry.Transport].Disconnect(entry.Inner, reason);
    }

    /// <inheritdoc />
    public void Flush()
    {
        if (_disposed)
            return;
        foreach (var transport in _transports)
            transport.Flush();
    }

    /// <summary>Disposes every transport (their peers are told <see cref="DisconnectReason.Shutdown"/>).</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        foreach (var transport in _transports)
            transport.Dispose();
        _peers.Clear();
        foreach (var relay in _relays)
            relay.Clear();
    }

    // Translates one inner transport's peer ids to ours while it is polled.
    private sealed class Relay(MultiServerTransport owner, int index) : ITransportListener
    {
        private readonly Dictionary<PeerId, PeerId> _outer = [];

        public ITransportListener? Listener { get; set; }

        public void Clear() => _outer.Clear();

        public void OnPeerConnected(PeerId peer, uint connectData)
        {
            if (owner._disposed || _outer.ContainsKey(peer))
                return;
            var id = new PeerId(owner._nextPeerId++);
            _outer[peer] = id;
            owner._peers[id] = (index, peer);
            Listener!.OnPeerConnected(id, connectData);
        }

        public void OnPeerDisconnected(PeerId peer, DisconnectReason reason)
        {
            if (!_outer.Remove(peer, out var id))
                return;
            owner._peers.Remove(id);
            Listener!.OnPeerDisconnected(id, reason);
        }

        public void OnReceive(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            if (!owner._disposed && _outer.TryGetValue(peer, out var id))
                Listener!.OnReceive(id, channel, payload);
        }
    }
}
