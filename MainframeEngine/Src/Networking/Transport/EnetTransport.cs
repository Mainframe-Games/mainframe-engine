using ENet;

namespace MainframeEngine.Networking;

/// <summary>
/// UDP transport over ENet (<see href="https://github.com/nxrighthere/ENet-CSharp">ENet-CSharp</see>), with two
/// channels: <see cref="NetChannel.Reliable"/> (reliable, ordered) and <see cref="NetChannel.Unreliable"/>
/// (unreliable, sequenced).
/// </summary>
/// <remarks>
/// <para>
/// Peer ids pack the ENet peer slot (low 32 bits) with a per-slot generation (high 32 bits): ENet reuses slots,
/// but a <see cref="PeerId"/> is never reused, so a stale id can never reach a newer connection. Disconnected and
/// timed-out peers are removed before the listener hears about them.
/// </para>
/// <para>
/// Sending copies the payload into a native ENet packet; polling hands the listener a span over the native packet.
/// Neither allocates managed memory. Not thread-safe: use one transport from one thread.
/// </para>
/// </remarks>
public sealed class EnetTransport : ITransport
{
    /// <summary>Channels opened on every host and connection.</summary>
    public const int ChannelCount = 2;

    private enum SlotState : byte
    {
        Free,
        Pending,
        Connected,
    }

    private readonly Host        _host;
    private readonly Peer[]      _peers;
    private readonly uint[]      _generations;
    private readonly SlotState[] _states;
    private int  _connectedCount;
    private bool _disposed;

    private EnetTransport(Host host, int slots, bool isServer)
    {
        _host        = host;
        _peers       = new Peer[slots];
        _generations = new uint[slots];
        _states      = new SlotState[slots];
        IsServer     = isServer;
    }

    /// <summary>The native library file ENet loads on this OS (<c>enet.dll</c>, <c>libenet.so</c>, <c>libenet.dylib</c>).</summary>
    public static string NativeLibraryFileName => EnetLibrary.NativeFileName;

    /// <inheritdoc />
    public bool IsServer { get; }

    /// <summary>Peers currently connected.</summary>
    public int ConnectedPeerCount => _connectedCount;

    /// <summary>Starts a server listening on <paramref name="port"/> for up to <paramref name="maxPeers"/> clients.</summary>
    /// <param name="port">UDP port to bind.</param>
    /// <param name="maxPeers">Connection limit, 1 to 4095.</param>
    /// <param name="bindAddress">Address to bind; null binds every interface.</param>
    /// <exception cref="DllNotFoundException">The ENet native library is missing for this platform.</exception>
    /// <exception cref="InvalidOperationException">The host could not be created (e.g. the port is in use).</exception>
    public static EnetTransport Listen(ushort port, int maxPeers, string? bindAddress = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPeers, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPeers, (int)Library.maxPeers);

        EnetLibrary.Acquire();
        var host = new Host();
        try
        {
            var address = new Address { Port = port };
            if (bindAddress is not null && !address.SetHost(bindAddress))
                throw new ArgumentException($"Cannot resolve bind address '{bindAddress}'.", nameof(bindAddress));

            host.Create(address, maxPeers, ChannelCount);
            Log.Info($"[Net] ENet server listening on port {port} (max {maxPeers} peers)");
            return new EnetTransport(host, maxPeers, isServer: true);
        }
        catch
        {
            host.Dispose();
            EnetLibrary.Release();
            throw;
        }
    }

    /// <summary>Starts connecting to a server. Completion is reported by the next polls (connected or <see cref="DisconnectReason.ConnectFailed"/>).</summary>
    /// <param name="host">Server host name or IP address.</param>
    /// <param name="port">Server UDP port.</param>
    /// <param name="connectData">Value the server receives with the connection (the message registry fingerprint).</param>
    /// <param name="timeoutMs">
    /// How long an unanswered connection (or, later, a silent server) may last before it fails, in milliseconds;
    /// 0 keeps ENet's adaptive default (5 to 30 s).
    /// </param>
    /// <exception cref="DllNotFoundException">The ENet native library is missing for this platform.</exception>
    public static EnetTransport Connect(string host, ushort port, uint connectData = 0, uint timeoutMs = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);

        EnetLibrary.Acquire();
        var enetHost = new Host();
        try
        {
            var address = new Address { Port = port };
            if (!address.SetHost(host))
                throw new ArgumentException($"Cannot resolve host '{host}'.", nameof(host));

            enetHost.Create(null, 1, ChannelCount);
            var transport = new EnetTransport(enetHost, 1, isServer: false);
            var peer      = enetHost.Connect(address, ChannelCount, connectData);
            var slot      = peer.ID;
            if (slot >= (uint)transport._peers.Length)
                throw new InvalidOperationException($"ENet returned peer slot {slot} for a single-peer host.");

            if (timeoutMs > 0)
                peer.Timeout(Library.timeoutLimit, timeoutMs, timeoutMs);
            transport._peers[slot] = peer;
            transport._generations[slot]++;
            transport._states[slot] = SlotState.Pending;
            Log.Info($"[Net] ENet client connecting to {host}:{port}");
            return transport;
        }
        catch
        {
            enetHost.Dispose();
            EnetLibrary.Release();
            throw;
        }
    }

    /// <inheritdoc />
    public unsafe void Poll(ITransportListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Drain already-received events first, then service the socket at most once (non-blocking).
        var serviced = false;
        while (!_disposed) // a handler may dispose the transport mid-poll
        {
            if (_host.CheckEvents(out var netEvent) <= 0)
            {
                if (serviced || _host.Service(0, out netEvent) <= 0)
                    break;
                serviced = true;
            }

            switch (netEvent.Type)
            {
                case EventType.Connect:
                    OnConnect(listener, netEvent.Peer, netEvent.Data);
                    break;

                case EventType.Disconnect:
                    OnDisconnect(listener, netEvent.Peer, (DisconnectReason)netEvent.Data);
                    break;

                case EventType.Timeout:
                    OnDisconnect(listener, netEvent.Peer, DisconnectReason.Timeout);
                    break;

                case EventType.Receive:
                    var packet = netEvent.Packet;
                    try
                    {
                        var slot = netEvent.Peer.ID;
                        if (slot < (uint)_states.Length && _states[slot] == SlotState.Connected && netEvent.ChannelID < ChannelCount)
                        {
                            var payload = new ReadOnlySpan<byte>((void*)packet.Data, packet.Length);
                            listener.OnReceive(MakeId(slot), (NetChannel)netEvent.ChannelID, payload);
                        }
                    }
                    finally
                    {
                        packet.Dispose();
                    }
                    break;

                case EventType.None:
                default:
                    break;
            }
        }
    }

    private void OnConnect(ITransportListener listener, Peer peer, uint connectData)
    {
        var slot = peer.ID;
        if (slot >= (uint)_states.Length)
            return;

        if (_states[slot] == SlotState.Connected)
        {
            // Defensive: ENet reused a slot without reporting its disconnect. Retire the old id first.
            _states[slot] = SlotState.Free;
            _connectedCount--;
            listener.OnPeerDisconnected(MakeId(slot), DisconnectReason.Closed);
        }

        if (_states[slot] != SlotState.Pending)
            _generations[slot]++;
        _states[slot] = SlotState.Connected;
        _peers[slot]  = peer;
        _connectedCount++;
        var id = MakeId(slot);
        Log.Debug($"[Net] ENet peer connected {id} ({peer.IP}:{peer.Port})");
        listener.OnPeerConnected(id, connectData);
    }

    private void OnDisconnect(ITransportListener listener, Peer peer, DisconnectReason reason)
    {
        var slot = peer.ID;
        if (slot >= (uint)_states.Length)
            return;

        var state = _states[slot];
        if (state == SlotState.Free)
            return;

        // Remove first: the listener may send, and must not reach this peer any more.
        var id = MakeId(slot);
        _states[slot] = SlotState.Free;
        _peers[slot]  = default;
        if (state == SlotState.Connected)
            _connectedCount--;
        else
            reason = DisconnectReason.ConnectFailed;

        Log.Debug($"[Net] ENet peer disconnected {id}: {reason}");
        listener.OnPeerDisconnected(id, reason);
    }

    /// <inheritdoc />
    public unsafe bool Send(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        if ((byte)channel >= ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown channel.");
        if (_disposed || !TryGetSlot(peer, out var slot))
            return false;

        var flags = channel == NetChannel.Reliable ? PacketFlags.Reliable : PacketFlags.None;
        fixed (byte* data = payload)
        {
            var packet = default(Packet);
            packet.Create((IntPtr)data, payload.Length, flags);
            var sent = _peers[slot].Send((byte)channel, ref packet);
            // Frees the packet only if no peer queued it (enet_packet_dispose checks the reference count).
            packet.Dispose();
            return sent;
        }
    }

    /// <inheritdoc />
    public void Disconnect(PeerId peer, DisconnectReason reason)
    {
        if (!_disposed && TryGetSlot(peer, out var slot))
            _peers[slot].Disconnect((uint)reason);
    }

    /// <inheritdoc />
    public void Flush()
    {
        if (!_disposed)
            _host.Flush();
    }

    /// <summary>Round-trip time to a connected peer in milliseconds, or -1 if the peer is unknown.</summary>
    public long GetRoundTripTime(PeerId peer) => !_disposed && TryGetSlot(peer, out var slot) ? _peers[slot].RoundTripTime : -1;

    /// <summary>Disconnects every peer immediately (they are told <see cref="DisconnectReason.Shutdown"/>) and destroys the host.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        for (var slot = 0; slot < _states.Length; slot++)
        {
            if (_states[slot] != SlotState.Free)
                _peers[slot].DisconnectNow((uint)DisconnectReason.Shutdown);
            _states[slot] = SlotState.Free;
            _peers[slot]  = default;
        }

        _connectedCount = 0;
        _host.Flush();
        _host.Dispose();
        EnetLibrary.Release();
    }

    private PeerId MakeId(uint slot) => new(((ulong)_generations[slot] << 32) | slot);

    private bool TryGetSlot(PeerId peer, out uint slot)
    {
        var raw = (ulong)peer;
        slot = (uint)raw;
        return slot < (uint)_states.Length
               && _states[slot] == SlotState.Connected
               && _generations[slot] == (uint)(raw >> 32);
    }
}
