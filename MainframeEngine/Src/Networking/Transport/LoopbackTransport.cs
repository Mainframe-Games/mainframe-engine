using System.Buffers;

namespace MainframeEngine.Networking;

/// <summary>
/// In-process transport: two linked endpoints (<see cref="CreatePair"/>) that exchange packets through queues, with
/// no sockets and no native code. For single-player "listen server" setups, tests and benchmarks.
/// </summary>
/// <remarks>
/// Both channels are lossless and ordered. Packets sent during a poll are delivered on the next poll. Payloads are
/// copied into arrays rented from <see cref="ArrayPool{T}.Shared"/>, so steady-state traffic does not allocate.
/// Not thread-safe: drive both ends from one thread.
/// </remarks>
public sealed class LoopbackTransport : ITransport
{
    /// <summary>The id each endpoint uses for its single remote peer.</summary>
    public static readonly PeerId RemotePeerId = new(1);

    private enum Kind : byte
    {
        Connect,
        Disconnect,
        Receive,
    }

    private readonly record struct Item(Kind Kind, NetChannel Channel, byte[]? Buffer, int Length, uint Data);

    private readonly Queue<Item>        _inbox = new();
    private          LoopbackTransport? _remote;
    private          bool               _disposed;

    private LoopbackTransport(bool isServer) => IsServer = isServer;

    /// <inheritdoc />
    public bool IsServer { get; }

    /// <summary>True while the two endpoints are linked (neither has disconnected or been disposed).</summary>
    public bool IsLinked => _remote is not null;

    /// <summary>Creates a linked server/client pair. Each side sees the other connect on its first poll.</summary>
    /// <param name="connectData">Value the server receives with the connection (as <see cref="EnetTransport.Connect"/> sends).</param>
    public static (LoopbackTransport Server, LoopbackTransport Client) CreatePair(uint connectData = 0)
    {
        var server = new LoopbackTransport(isServer: true);
        var client = new LoopbackTransport(isServer: false);
        server._remote = client;
        client._remote = server;
        server._inbox.Enqueue(new Item(Kind.Connect, default, null, 0, connectData));
        client._inbox.Enqueue(new Item(Kind.Connect, default, null, 0, 0));
        return (server, client);
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
                    listener.OnPeerConnected(RemotePeerId, item.Data);
                    break;
                case Kind.Disconnect:
                    listener.OnPeerDisconnected(RemotePeerId, (DisconnectReason)item.Data);
                    break;
                case Kind.Receive:
                    var buffer = item.Buffer ?? [];
                    try
                    {
                        listener.OnReceive(RemotePeerId, item.Channel, buffer.AsSpan(0, item.Length));
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
        if (_disposed || _remote is null || peer != RemotePeerId)
            return false;

        var buffer = payload.Length == 0 ? null : ArrayPool<byte>.Shared.Rent(payload.Length);
        payload.CopyTo(buffer);
        _remote._inbox.Enqueue(new Item(Kind.Receive, channel, buffer, payload.Length, 0));
        return true;
    }

    /// <inheritdoc />
    public void Disconnect(PeerId peer, DisconnectReason reason)
    {
        if (_disposed || _remote is null || peer != RemotePeerId)
            return;
        Unlink(reason);
    }

    /// <inheritdoc />
    public void Flush()
    {
    }

    /// <summary>Unlinks (the other side sees <see cref="DisconnectReason.Shutdown"/>) and drops undelivered packets.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        if (_remote is not null)
            Unlink(DisconnectReason.Shutdown);
        _disposed = true;
        while (_inbox.TryDequeue(out var item))
        {
            if (item.Buffer is { Length: > 0 } buffer)
                ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private void Unlink(DisconnectReason reason)
    {
        var remote = _remote;
        if (remote is null)
            return;
        _remote        = null;
        remote._remote = null;
        _inbox.Enqueue(new Item(Kind.Disconnect, default, null, 0, (uint)reason));
        if (!remote._disposed)
            remote._inbox.Enqueue(new Item(Kind.Disconnect, default, null, 0, (uint)reason));
    }
}
