using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

/// <summary>Why a received packet was dropped instead of dispatched.</summary>
public enum MessageDropReason : byte
{
    /// <summary>Shorter than a <see cref="MessageHeader"/>.</summary>
    Truncated,

    /// <summary>The header's protocol version differs from <see cref="MessageRegistry.ProtocolVersion"/>.</summary>
    ProtocolMismatch,

    /// <summary>The message id is not registered.</summary>
    UnknownMessage,

    /// <summary>The payload could not be decoded (too short, or invalid lengths).</summary>
    Malformed,
}

/// <summary>Running totals of a <see cref="MessageBus"/>.</summary>
[StructLayout(LayoutKind.Auto)]
public record struct NetStats(
    long MessagesSent,
    long BytesSent,
    long MessagesReceived,
    long BytesReceived,
    long MessagesDropped,
    long SendFailures);

/// <summary>
/// Typed messages over an <see cref="ITransport"/>: frames each message with a <see cref="MessageHeader"/>,
/// dispatches received ones to the handlers registered with <see cref="Subscribe{T}"/>, and tracks connected peers.
/// </summary>
/// <remarks>
/// <para>
/// Messages are structs implementing <see cref="INetworkTransferable"/>, registered in a <see cref="MessageRegistry"/>
/// shared by both ends. Send, receive and dispatch reuse one encode buffer and one decode buffer, so steady-state
/// traffic allocates nothing (beyond what a message's own <c>NetworkRead</c> allocates, e.g. strings).
/// </para>
/// <para>
/// Single-threaded: call everything, including <see cref="Poll"/>, from the game loop; events and handlers run
/// inside <see cref="Poll"/>. A server bus refuses clients whose connect data is not this registry's
/// <see cref="MessageRegistry.Fingerprint"/> (<see cref="DisconnectReason.ProtocolMismatch"/>).
/// </para>
/// </remarks>
public sealed class MessageBus : ITransportListener, IDisposable
{
    private readonly MessageSlot?[]  _slots;
    private readonly List<PeerId>    _peers = [];
    private readonly NetBufferWriter _writer = new();
    private readonly NetBufferReader _reader = new();
    private readonly bool            _ownsTransport;
    private NetStats _stats;
    private bool     _disposed;

    /// <param name="transport">Connection to use. Disposed with the bus when <paramref name="ownsTransport"/> is true.</param>
    /// <param name="registry">Message types; frozen by this call.</param>
    /// <param name="ownsTransport">Whether <see cref="Dispose"/> also disposes <paramref name="transport"/>.</param>
    public MessageBus(ITransport transport, MessageRegistry registry, bool ownsTransport = true)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(registry);

        Transport      = transport;
        Registry       = registry;
        _ownsTransport = ownsTransport;
        registry.Freeze();

        _slots = new MessageSlot?[registry.SlotCount];
        for (var id = 0; id < _slots.Length; id++)
            _slots[id] = registry.CreateSlot((ushort)id);
    }

    /// <summary>A peer connected (and, on a server, passed the fingerprint check).</summary>
    public event Action<PeerId>? PeerConnected;

    /// <summary>A connected peer left. Not raised for peers refused at connect.</summary>
    public event Action<PeerId, DisconnectReason>? PeerDisconnected;

    /// <summary>A received packet was dropped.</summary>
    public event Action<PeerId, MessageDropReason>? MessageDropped;

    /// <summary>The transport refused a message for a peer (unknown or disconnecting peer, transport error).</summary>
    public event Action<PeerId, ushort>? SendFailed;

    public ITransport Transport { get; }

    public MessageRegistry Registry { get; }

    /// <summary>Written into the header of every message sent from now on (e.g. the server tick).</summary>
    public uint Tick { get; set; }

    /// <summary>When true, every sent and received message is logged at debug level (allocates; off by default).</summary>
    public bool LogPackets { get; set; }

    /// <summary>Connected peers, in connection order. Valid until the next <see cref="Poll"/>.</summary>
    public ReadOnlySpan<PeerId> Peers => CollectionsMarshal.AsSpan(_peers);

    /// <summary>Totals since creation.</summary>
    public NetStats Stats => _stats;

    /// <summary>Adds a handler for <typeparamref name="T"/>; several handlers may share a type.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not registered.</exception>
    public void Subscribe<T>(MessageHandler<T> handler) where T : struct, INetworkTransferable
    {
        ArgumentNullException.ThrowIfNull(handler);
        var slot = GetSlot<T>();
        slot.Handlers += handler;
    }

    /// <summary>Removes a handler added with <see cref="Subscribe{T}"/>.</summary>
    public void Unsubscribe<T>(MessageHandler<T> handler) where T : struct, INetworkTransferable
    {
        ArgumentNullException.ThrowIfNull(handler);
        var slot = GetSlot<T>();
        slot.Handlers -= handler;
    }

    /// <summary>Sends <paramref name="message"/> to one peer. Returns false (logged, <see cref="SendFailed"/>) instead of throwing.</summary>
    public bool Send<T>(PeerId peer, in T message, NetChannel channel = NetChannel.Reliable) where T : struct, INetworkTransferable
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var payload = Encode(in message, out var id);
        return SendEncoded(peer, channel, payload, id);
    }

    /// <summary>Sends <paramref name="message"/> to every connected peer. Returns how many accepted it; failures are reported, not thrown.</summary>
    public int Broadcast<T>(in T message, NetChannel channel = NetChannel.Reliable) where T : struct, INetworkTransferable =>
        Broadcast(in message, channel, default, excludeAny: false);

    /// <summary>Sends <paramref name="message"/> to every connected peer except <paramref name="except"/>.</summary>
    public int Broadcast<T>(in T message, NetChannel channel, PeerId except) where T : struct, INetworkTransferable =>
        Broadcast(in message, channel, except, excludeAny: true);

    private int Broadcast<T>(in T message, NetChannel channel, PeerId except, bool excludeAny) where T : struct, INetworkTransferable
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_peers.Count == 0)
            return 0;

        var payload = Encode(in message, out var id);
        var sent    = 0;
        foreach (var peer in CollectionsMarshal.AsSpan(_peers))
        {
            if (excludeAny && peer == except)
                continue;
            if (SendEncoded(peer, channel, payload, id))
                sent++;
        }

        return sent;
    }

    /// <summary>Gracefully disconnects a peer; <see cref="PeerDisconnected"/> follows on a later poll.</summary>
    public void Disconnect(PeerId peer, DisconnectReason reason = DisconnectReason.Kicked)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Transport.Disconnect(peer, reason);
    }

    /// <summary>Receives pending packets and connection events, dispatching them to handlers and events.</summary>
    public void Poll()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Transport.Poll(this);
    }

    /// <summary>Sends queued packets now rather than on the next <see cref="Poll"/>.</summary>
    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Transport.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _peers.Clear();
        if (_ownsTransport)
            Transport.Dispose();
        _writer.ReleaseStorage();
        _reader.ReleaseStorage();
    }

    /// <summary>Encodes header + payload into the bus's buffer. The span is valid until the next encode.</summary>
    internal ReadOnlySpan<byte> Encode<T>(in T message, out ushort id) where T : struct, INetworkTransferable
    {
        id = Registry.GetId<T>();
        _writer.Reset();
        new MessageHeader(Registry.ProtocolVersion, id, Tick).Write(_writer);
        var copy = message;
        copy.NetworkWrite(_writer);
        return _writer.WrittenSpan;
    }

    private bool SendEncoded(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload, ushort id)
    {
        if (Transport.Send(peer, channel, payload))
        {
            _stats.MessagesSent++;
            _stats.BytesSent += payload.Length;
            if (LogPackets)
                Log.Debug($"[Net] sent {Registry.GetMessageType(id)?.Name} ({payload.Length} B) to {peer} on {channel}");
            return true;
        }

        _stats.SendFailures++;
        Log.Warning($"[Net] failed to send {Registry.GetMessageType(id)?.Name} to {peer} on {channel}");
        SendFailed?.Invoke(peer, id);
        return false;
    }

    private MessageSlot<T> GetSlot<T>() where T : struct, INetworkTransferable
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Registry.GetId<T>();
        return (MessageSlot<T>)_slots[id]!;
    }

    void ITransportListener.OnPeerConnected(PeerId peer, uint connectData)
    {
        if (Transport.IsServer && connectData != Registry.Fingerprint)
        {
            Log.Warning($"[Net] refused {peer}: protocol fingerprint {connectData:X8} != {Registry.Fingerprint:X8}");
            Transport.Disconnect(peer, DisconnectReason.ProtocolMismatch);
            return;
        }

        if (_peers.Contains(peer))
            return;
        _peers.Add(peer);
        Log.Info($"[Net] {(Transport.IsServer ? "client" : "server")} connected: {peer}");
        PeerConnected?.Invoke(peer);
    }

    void ITransportListener.OnPeerDisconnected(PeerId peer, DisconnectReason reason)
    {
        // Unknown peers: a client refused at connect (server), or a duplicate notification. A client still hears
        // about its own failed connection attempt.
        var known = _peers.Remove(peer);
        if (!known && (Transport.IsServer || reason != DisconnectReason.ConnectFailed))
            return;

        Log.Info($"[Net] {(Transport.IsServer ? "client" : "server")} disconnected: {peer} ({reason})");
        PeerDisconnected?.Invoke(peer, reason);
    }

    void ITransportListener.OnReceive(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        if (!MessageHeader.TryRead(payload, out var header))
        {
            Drop(peer, MessageDropReason.Truncated);
            return;
        }

        if (header.ProtocolVersion != Registry.ProtocolVersion)
        {
            Drop(peer, MessageDropReason.ProtocolMismatch);
            return;
        }

        var slot = header.MessageId < _slots.Length ? _slots[header.MessageId] : null;
        if (slot is null)
        {
            Drop(peer, MessageDropReason.UnknownMessage);
            return;
        }

        _stats.MessagesReceived++;
        _stats.BytesReceived += payload.Length;
        if (LogPackets)
            Log.Debug($"[Net] received {Registry.GetMessageType(header.MessageId)?.Name} ({payload.Length} B) from {peer} on {channel}, tick {header.Tick}");

        if (!slot.HasHandlers)
            return;

        _reader.SetData(payload[MessageHeader.Size..]);
        var context = new MessageContext(peer, channel, header);
        try
        {
            slot.Dispatch(in context, _reader);
        }
        catch (Exception e) when (e is EndOfStreamException or InvalidDataException)
        {
            _stats.MessagesReceived--;
            Drop(peer, MessageDropReason.Malformed);
        }
    }

    private void Drop(PeerId peer, MessageDropReason reason)
    {
        _stats.MessagesDropped++;
        Log.Warning($"[Net] dropped packet from {peer}: {reason}");
        MessageDropped?.Invoke(peer, reason);
    }
}
