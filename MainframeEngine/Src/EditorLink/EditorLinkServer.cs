using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace MainframeEngine.EditorLink;

/// <summary>
/// The editor side of the editor link: listens on <c>localhost</c> (<see cref="Port"/>; pass it to games as
/// <c>--editor-port</c>), welcomes each game, receives its hello, logs and status, and sends commands. Several games may be
/// connected at once (e.g. a server and a client instance); every message carries the <see cref="EditorLinkMessage.GameId"/>
/// of its connection.
/// </summary>
/// <remarks>
/// Sockets are serviced on background threads; the editor polls <see cref="TryRead"/> from its main thread (never
/// blocks). <see cref="EditorLinkMessageType.Connected"/> and <see cref="EditorLinkMessageType.Disconnected"/> are
/// queued locally around each game's messages. Received log messages beyond <see cref="MaxQueuedMessages"/> (an editor
/// that stopped polling) are dropped and counted in <see cref="DroppedMessageCount"/>. A connection that sends anything
/// malformed is closed.
/// </remarks>
public sealed class EditorLinkServer : IDisposable
{
    /// <summary>Most received messages held for <see cref="TryRead"/>.</summary>
    public const int MaxQueuedMessages = 100_000;

    /// <summary>Most games connected at once; further connections are closed.</summary>
    public const int MaxGames = 16;

    /// <summary>A command write to a game that stopped reading fails (and drops that game) after this long.</summary>
    public const int SendTimeoutMilliseconds = 2000;

    private static readonly byte[] WelcomeFrame = CreateWelcomeFrame();

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stopAccepting = new();
    private readonly Task _acceptLoop;
    private readonly ConcurrentQueue<EditorLinkMessage> _incoming = new();
    private readonly ConcurrentDictionary<int, FramedConnection> _games = new();
    private int _nextGameId;
    private int _queued;
    private long _dropped;
    private volatile bool _disposed;

    /// <summary>Listens on loopback <paramref name="port"/> (0 picks a free port).</summary>
    public EditorLinkServer(int port = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        _listener = new TcpListener(IPAddress.Loopback, port);
        if (!OperatingSystem.IsWindows())
            _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true); // quick restarts
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptAsync);
    }

    /// <summary>The port games connect to.</summary>
    public int Port { get; }

    /// <summary>True while at least one game is connected.</summary>
    public bool IsConnected => !_games.IsEmpty;

    /// <summary>Ids of the connected games.</summary>
    public IReadOnlyCollection<int> ConnectedGames => [.. _games.Keys];

    /// <summary>The listening socket (tests share it with a child process).</summary>
    internal Socket ListenerSocket => _listener.Server;

    /// <summary>Received messages dropped because <see cref="TryRead"/> was not keeping up.</summary>
    public long DroppedMessageCount => Interlocked.Read(ref _dropped);

    /// <summary>The next received message (or a local Connected/Disconnected marker), if any.</summary>
    public bool TryRead(out EditorLinkMessage message)
    {
        if (!_incoming.TryDequeue(out message))
            return false;
        Interlocked.Decrement(ref _queued);
        return true;
    }

    /// <summary>Sends <paramref name="command"/> to every connected game; returns how many it reached.</summary>
    public int SendCommand(in EditorLinkCommand command)
    {
        var sent = 0;
        foreach (var id in _games.Keys)
            if (SendCommand(id, command))
                sent++;
        return sent;
    }

    /// <summary>Sends <paramref name="command"/> to game <paramref name="gameId"/>; false when it is not connected or the send failed.</summary>
    public bool SendCommand(int gameId, in EditorLinkCommand command)
    {
        if (!_games.TryGetValue(gameId, out var connection) || connection.IsDisposed)
            return false;
        // Per connection: a game that stopped reading times out (SendTimeout) without stalling commands to the others.
        lock (connection.SendGate)
        {
            try
            {
                connection.SendBuffer.Clear();
                EditorLinkProtocol.WriteCommand(connection.SendBuffer, command);
                connection.Write(connection.SendBuffer.WrittenSpan);
                return true;
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                connection.Dispose();
                return false;
            }
        }
    }

    /// <summary>Closes the connection of <paramref name="gameId"/>, or of every game (a running game reconnects).</summary>
    public void Disconnect(int? gameId = null)
    {
        // A snapshot: a game reconnecting while this runs must not be dropped too (a live enumeration would see it).
        foreach (var (id, connection) in _games.ToArray())
            if (gameId is null || gameId == id)
                connection.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        // Cancel the pending accept before closing: a blocking accept on a listener whose descriptor a child process also
        // holds without FD_CLOEXEC is never unblocked by the close, which then waits for it forever.
        _stopAccepting.Cancel();
        _listener.Stop();
        Disconnect();
        if (_acceptLoop.Wait(TimeSpan.FromSeconds(2)))
            _stopAccepting.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_disposed)
        {
            Socket socket;
            try
            {
                socket = await _listener.AcceptSocketAsync(_stopAccepting.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                if (_disposed)
                    return;
                continue;
            }

            if (_disposed || _games.Count >= MaxGames)
            {
                socket.Dispose();
                continue;
            }

            FramedConnection? connection = null;
            try
            {
                connection = new FramedConnection(socket, SendTimeoutMilliseconds);
                connection.Write(WelcomeFrame); // before the game is listed: no command can be written ahead of it
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                // Reset before it was served.
                if (connection is null)
                    socket.Dispose();
                else
                    connection.Dispose();
                continue;
            }

            var id = Interlocked.Increment(ref _nextGameId);
            _games[id] = connection;
            if (_disposed)
            {
                // Dispose ran between the check above and the insert: its Disconnect snapshot missed this one.
                _games.TryRemove(id, out _);
                connection.Dispose();
                return;
            }

            Enqueue(new EditorLinkMessage { Type = EditorLinkMessageType.Connected, GameId = id }, force: true);
            new Thread(() => Read(id, connection)) { IsBackground = true, Name = $"EditorLink server reader {id}" }.Start();
        }
    }

    private void Read(int id, FramedConnection connection)
    {
        try
        {
            while (connection.ReadFrame(out var body))
            {
                if (!EditorLinkProtocol.TryDecode(body.Span, out var message)
                    || message.Type is EditorLinkMessageType.Command or EditorLinkMessageType.Welcome)
                    break; // malformed, or a peer that is not a game: drop it
                Enqueue(message with { GameId = id }, force: message.Type != EditorLinkMessageType.Log);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
        }

        connection.Dispose();
        _games.TryRemove(id, out _);
        Enqueue(new EditorLinkMessage { Type = EditorLinkMessageType.Disconnected, GameId = id }, force: true);
    }

    private static byte[] CreateWelcomeFrame()
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>(16);
        EditorLinkProtocol.WriteWelcome(buffer, EditorLinkProtocol.Version);
        return buffer.WrittenSpan.ToArray();
    }

    private void Enqueue(in EditorLinkMessage message, bool force)
    {
        if (Interlocked.Increment(ref _queued) > MaxQueuedMessages && !force)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _dropped);
            return;
        }

        _incoming.Enqueue(message);
    }
}
