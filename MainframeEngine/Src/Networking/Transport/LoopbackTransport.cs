using System.Buffers;

namespace MainframeEngine.Networking;

/// <summary>
/// In-process transport: a server endpoint and any number of client endpoints linked to it, exchanging packets through
/// queues, with no sockets and no native code. For single-player "listen server" setups, tests and benchmarks.
/// </summary>
/// <remarks>
/// <see cref="CreatePair"/> links one client; <see cref="CreateServer"/> + <see cref="ConnectClient"/> links several.
/// A client sees the server as <see cref="RemotePeerId"/>; the server sees its clients as 1, 2, 3… in connection order
/// (never reused). Both channels are lossless and ordered (wrap an endpoint in a <see cref="SimulatedTransport"/> for
/// loss, latency and reordering). Packets sent during a poll are delivered on the next poll. Payloads are copied into
/// arrays rented from <see cref="ArrayPool{T}.Shared"/>, so steady-state traffic does not allocate. Not thread-safe:
/// drive every endpoint from one thread.
/// </remarks>
public sealed class LoopbackTransport : ITransport
{
    /// <summary>The id a client uses for the server (and the server for its first client).</summary>
    public static readonly PeerId RemotePeerId = new(1);

    private enum Kind : byte
    {
        Connect,
        Disconnect,
        Receive,
    }

    private readonly record struct Item(Kind Kind, PeerId Peer, NetChannel Channel, byte[]? Buffer, int Length, uint Data);

    private readonly Queue<Item> _inbox = new();
    private readonly Dictionary<PeerId, LoopbackTransport>? _clients; // server endpoints
    private ulong _nextClientId = 1;
    private LoopbackTransport? _server; // client endpoints, while linked
    private PeerId _idOnServer;
    private bool _disposed;

    private LoopbackTransport(bool isServer)
    {
        IsServer = isServer;
        if (isServer)
            _clients = [];
    }

    /// <inheritdoc />
    public bool IsServer { get; }

    /// <summary>True while linked: a client to its server, a server to at least one client.</summary>
    public bool IsLinked => IsServer ? _clients!.Count > 0 : _server is not null;

    /// <summary>Clients currently linked to this server endpoint.</summary>
    public int ClientCount => _clients?.Count ?? 0;

    public bool IsDisposed => _disposed;

    /// <summary>Creates a linked server/client pair. Each side sees the other connect on its first poll.</summary>
    /// <param name="connectData">Value the server receives with the connection (as <see cref="EnetTransport.Connect"/> sends).</param>
    public static (LoopbackTransport Server, LoopbackTransport Client) CreatePair(uint connectData = 0)
    {
        var server = CreateServer();
        return (server, server.ConnectClient(connectData));
    }

    /// <summary>Creates a server endpoint; link clients with <see cref="ConnectClient"/>.</summary>
    public static LoopbackTransport CreateServer() => new(isServer: true);

    /// <summary>Creates a client endpoint linked to this server. Each side sees the other connect on its next poll.</summary>
    /// <param name="connectData">Value the server receives with the connection.</param>
    public LoopbackTransport ConnectClient(uint connectData = 0)
    {
        if (!IsServer)
            throw new InvalidOperationException("Only a server endpoint accepts clients.");
        ObjectDisposedException.ThrowIf(_disposed, this);

        var client = new LoopbackTransport(isServer: false);
        var id = new PeerId(_nextClientId++);
        client._server = this;
        client._idOnServer = id;
        _clients!.Add(id, client);
        _inbox.Enqueue(new Item(Kind.Connect, id, default, null, 0, connectData));
        client._inbox.Enqueue(new Item(Kind.Connect, RemotePeerId, default, null, 0, 0));
        return client;
    }

    /// <inheritdoc />
    public void Poll(ITransportListener listener)
    {
        ArgumentNullException.ThrowIfNull(listener);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Only what was queued before this poll: packets sent by handlers wait for the next one.
        for (var count = _inbox.Count; count > 0 && _inbox.TryDequeue(out var item); count--)
        {
            switch (item.Kind)
            {
                case Kind.Connect:
                    listener.OnPeerConnected(item.Peer, item.Data);
                    break;
                case Kind.Disconnect:
                    listener.OnPeerDisconnected(item.Peer, (DisconnectReason)item.Data);
                    break;
                case Kind.Receive:
                    var buffer = item.Buffer ?? [];
                    try
                    {
                        listener.OnReceive(item.Peer, item.Channel, buffer.AsSpan(0, item.Length));
                    }
                    finally
                    {
                        if (buffer.Length > 0)
                            ArrayPool<byte>.Shared.Return(buffer);
                    }
                    break;
            }
        }
    }

    /// <inheritdoc />
    public bool Send(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload)
    {
        if ((byte)channel >= EnetTransport.ChannelCount)
            throw new ArgumentOutOfRangeException(nameof(channel), channel, "Unknown channel.");
        if (_disposed)
            return false;

        LoopbackTransport? remote;
        PeerId from;
        if (IsServer)
        {
            if (!_clients!.TryGetValue(peer, out remote))
                return false;
            from = RemotePeerId;
        }
        else
        {
            remote = _server;
            if (remote is null || peer != RemotePeerId)
                return false;
            from = _idOnServer;
        }

        var buffer = payload.Length == 0 ? null : ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);
        remote._inbox.Enqueue(new Item(Kind.Receive, from, channel, buffer, payload.Length, 0));
        return true;
    }

    /// <inheritdoc />
    public void Disconnect(PeerId peer, DisconnectReason reason)
    {
        if (_disposed)
            return;
        if (IsServer)
        {
            if (_clients!.TryGetValue(peer, out var client))
                Unlink(client, reason);
        }
        else if (_server is not null && peer == RemotePeerId)
        {
            Unlink(this, reason);
        }
    }

    /// <inheritdoc />
    public void Flush()
    {
    }

    /// <summary>Unlinks every remote endpoint (they see <see cref="DisconnectReason.Shutdown"/>) and drops undelivered packets.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        if (IsServer)
        {
            foreach (var client in _clients!.Values.ToArray())
                Unlink(client, DisconnectReason.Shutdown);
        }
        else if (_server is not null)
        {
            Unlink(this, DisconnectReason.Shutdown);
        }

        _disposed = true;
        while (_inbox.TryDequeue(out var item))
        {
            if (item.Buffer is { Length: > 0 } buffer)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Both ends hear about it (the side that disconnected too, as ENet reports).
    private static void Unlink(LoopbackTransport client, DisconnectReason reason)
    {
        var server = client._server;
        if (server is null)
            return;
        server._clients!.Remove(client._idOnServer);
        client._server = null;
        if (!server._disposed)
            server._inbox.Enqueue(new Item(Kind.Disconnect, client._idOnServer, default, null, 0, (uint)reason));
        if (!client._disposed)
            client._inbox.Enqueue(new Item(Kind.Disconnect, RemotePeerId, default, null, 0, (uint)reason));
    }
}
