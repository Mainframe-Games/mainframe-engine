using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace MainframeEngine.EditorLink;

/// <summary>
/// The game side of the editor link: connects to the editor's <see cref="EditorLinkServer"/> on <c>localhost:port</c>,
/// streams log entries and status, and receives commands. All socket work happens on background threads; the game
/// loop only enqueues (<see cref="TryEnqueueLog"/>, <see cref="ReportStatus"/>) and polls
/// (<see cref="TryReceiveCommand"/>), which never block.
/// </summary>
/// <remarks>
/// Robust to the editor going away: the client reconnects with back-off (100 ms → 2 s) and sends a fresh hello on every
/// connection. It counts a connection, and sends logs on it, only once the editor's
/// <see cref="EditorLinkMessageType.Welcome"/> arrives (within <see cref="WelcomeTimeoutMilliseconds"/>, else it reconnects):
/// a listener the editor closed still completes handshakes while a child process that inherited it lives (macOS sets
/// FD_CLOEXEC after creating a socket, so a concurrent fork can leak it), and logs written there would be lost.
/// Logs queue while disconnected; when more than <see cref="QueueCapacity"/> are waiting (the editor is not
/// reading, or is gone) new entries are dropped and counted, and the count is reported
/// (<see cref="EditorLinkMessageType.LogDropped"/>) once the link catches up.
/// </remarks>
public sealed class EditorLinkClient : IDisposable
{
    /// <summary>A connection the editor has not welcomed within this long is dropped and retried.</summary>
    public const int WelcomeTimeoutMilliseconds = 2000;

    private const int MaxBatchBytes = 64 * 1024;

    private readonly int _port;
    private readonly EditorLinkHello _hello;
    private readonly ConcurrentQueue<LogEntry> _logs = new();
    private readonly ConcurrentQueue<EditorLinkCommand> _commands = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly Lock _statusGate = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _goodbyeSent = new(false);
    private readonly ManualResetEventSlim _stopped = new(false);
    private int _connectAttempts;
    private EditorLinkStatus _status;
    private bool _statusPending;
    private EditorLinkTreeNode[]? _tree;
    private bool _treeTruncated;
    private int _queued;
    private long _droppedPending;
    private long _droppedTotal;
    private int _connectionCount;
    private volatile bool _connected;
    private volatile bool _stopping;
    private int _goodbyeExitCode;
    private volatile bool _goodbyeRequested;
    private FramedConnection? _connection;

    public EditorLinkClient(int port, EditorLinkHello hello, int queueCapacity = 4096)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(queueCapacity, 1);
        _port = port;
        _hello = hello;
        QueueCapacity = queueCapacity;
        _thread = new Thread(Run) { IsBackground = true, Name = "EditorLink client" };
        _thread.Start();
    }

    public int Port => _port;

    /// <summary>Most log entries held while the link is slow or down.</summary>
    public int QueueCapacity { get; }

    /// <summary>True while connected to the editor (welcomed).</summary>
    public bool IsConnected => _connected;

    /// <summary>Connections the editor welcomed so far (reconnects included).</summary>
    public int ConnectionCount => Volatile.Read(ref _connectionCount);

    /// <summary>Connection attempts so far, successful or not (back-off: 100 ms doubling to 2 s while the editor is away).</summary>
    public int ConnectAttempts => Volatile.Read(ref _connectAttempts);

    /// <summary>Log entries dropped because the queue was full (total).</summary>
    public long DroppedLogCount => Interlocked.Read(ref _droppedTotal);

    /// <summary>Queues a log entry; false (and counted) when the queue is full. Never blocks.</summary>
    public bool TryEnqueueLog(in LogEntry entry)
    {
        if (_stopping)
            return false;
        if (Interlocked.Increment(ref _queued) > QueueCapacity)
        {
            Interlocked.Decrement(ref _queued);
            Interlocked.Increment(ref _droppedPending);
            Interlocked.Increment(ref _droppedTotal);
            return false;
        }

        _logs.Enqueue(entry);
        Wake();
        return true;
    }

    /// <summary>Sets the status to send (the latest report wins). Never blocks.</summary>
    public void ReportStatus(in EditorLinkStatus status)
    {
        lock (_statusGate)
        {
            _status = status;
            _statusPending = true;
        }

        Wake();
    }

    /// <summary>The next command from the editor, if any (call from the game loop).</summary>
    public bool TryReceiveCommand(out EditorLinkCommand command) => _commands.TryDequeue(out command);

    /// <summary>Queues a scene tree snapshot (the reply to <see cref="EditorCommandKind.RequestTree"/>); a newer one replaces it.</summary>
    public void SendTree(EditorLinkTreeNode[] nodes, bool truncated)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        lock (_statusGate)
        {
            _tree = nodes;
            _treeTruncated = truncated;
        }

        _signal.Set();
    }

    /// <summary>
    /// Sends the queued logs and a goodbye with <paramref name="exitCode"/>, waiting at most <paramref name="timeout"/>
    /// (call when the game exits; returns false when not connected or timed out).
    /// </summary>
    public bool SendGoodbye(int exitCode, TimeSpan timeout)
    {
        if (!_connected)
            return false;
        _goodbyeExitCode = exitCode;
        _goodbyeRequested = true;
        Wake();
        return _goodbyeSent.Wait(timeout);
    }

    public void Dispose()
    {
        if (_stopping)
            return;
        _stopping = true;
        _stopped.Set();
        Wake();
        Volatile.Read(ref _connection)?.Dispose();
        // The thread only blocks in socket calls (unblocked by disposing the connection) or on the signal.
        if (_thread.Join(TimeSpan.FromSeconds(2)))
        {
            _signal.Dispose();
            _goodbyeSent.Dispose();
            _stopped.Dispose();
        }
    }

    private void Run()
    {
        var backoff = 100;
        var buffer = new ArrayBufferWriter<byte>(16 * 1024);
        while (!_stopping)
        {
            if (TryConnect() is { } connection && Serve(connection, buffer))
            {
                backoff = 100; // the editor dropped a working link: reconnect at once
                continue;
            }

            // Its own event: queued logs and status reports signal _signal and must not cut the back-off short
            // (that would attempt a connection per log line while the editor is away).
            _stopped.Wait(backoff);
            backoff = Math.Min(backoff * 2, 2000);
        }
    }

    // Says hello, waits for the welcome, then sends until the connection breaks or the client stops; false when the
    // editor never welcomed it.
    private bool Serve(FramedConnection connection, ArrayBufferWriter<byte> buffer)
    {
        Volatile.Write(ref _connection, connection);
        var welcomed = false;
        var reader = new Thread(() => ReadCommands(connection)) { IsBackground = true, Name = "EditorLink client reader" };
        try
        {
            buffer.Clear();
            EditorLinkProtocol.WriteHello(buffer, _hello);
            connection.Write(buffer.WrittenSpan);
            if (!ReadWelcome(connection))
                return false;
            welcomed = true;
            Interlocked.Increment(ref _connectionCount);
            _connected = true;
            reader.Start();
            Pump(connection, buffer);
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // The editor went away (or never answered): reconnect (logs keep queueing meanwhile).
        }
        finally
        {
            _connected = false;
            Volatile.Write(ref _connection, null);
            connection.Dispose();
            if (reader.IsAlive)
                reader.Join(TimeSpan.FromSeconds(1));
        }

        return welcomed;
    }

    // Throws IOException when nothing arrives within the timeout.
    private static bool ReadWelcome(FramedConnection connection)
    {
        connection.ReceiveTimeout = WelcomeTimeoutMilliseconds;
        if (!connection.ReadFrame(out var body) || !EditorLinkProtocol.TryDecode(body.Span, out var message)
            || message.Type != EditorLinkMessageType.Welcome)
            return false;
        connection.ReceiveTimeout = 0;
        return true;
    }

    private FramedConnection? TryConnect()
    {
        Interlocked.Increment(ref _connectAttempts);
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            socket.Connect(new IPEndPoint(IPAddress.Loopback, _port));
            return new FramedConnection(socket);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return null;
        }
    }

    // Sends until the connection breaks or the client stops.
    private void Pump(FramedConnection connection, ArrayBufferWriter<byte> buffer)
    {
        while (!_stopping && !connection.IsDisposed)
        {
            buffer.Clear();
            while (buffer.WrittenCount < MaxBatchBytes && _logs.TryDequeue(out var entry))
            {
                Interlocked.Decrement(ref _queued);
                EditorLinkProtocol.WriteLog(buffer, entry);
            }

            if (_logs.IsEmpty && Interlocked.Exchange(ref _droppedPending, 0) is var dropped and > 0)
                EditorLinkProtocol.WriteLogDropped(buffer, dropped);

            lock (_statusGate)
            {
                if (_statusPending)
                {
                    EditorLinkProtocol.WriteStatus(buffer, _status);
                    _statusPending = false;
                }

                if (_tree is { } tree)
                {
                    EditorLinkProtocol.WriteTree(buffer, tree, _treeTruncated);
                    _tree = null;
                }
            }

            var goodbye = _goodbyeRequested && _logs.IsEmpty;
            if (goodbye)
                EditorLinkProtocol.WriteGoodbye(buffer, _goodbyeExitCode);

            if (buffer.WrittenCount > 0)
                connection.Write(buffer.WrittenSpan);
            if (goodbye)
            {
                _goodbyeRequested = false;
                _goodbyeSent.Set();
            }

            if (!_logs.IsEmpty)
                continue; // more queued than one batch: keep going
            _signal.WaitOne(250);
        }
    }

    private void ReadCommands(FramedConnection connection)
    {
        try
        {
            while (connection.ReadFrame(out var body))
            {
                if (EditorLinkProtocol.TryDecode(body.Span, out var message) && message.Type == EditorLinkMessageType.Command)
                    _commands.Enqueue(message.Command);
                else
                    break; // the editor only sends commands: anything else means a broken peer
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
        }

        // Wake the pump so it notices the dead connection.
        connection.Dispose();
        Wake();
    }

    private void Wake()
    {
        try
        {
            _signal.Set();
        }
        catch (ObjectDisposedException)
        {
            // Disposed concurrently: nothing is waiting any more.
        }
    }
}

/// <summary>Forwards every log entry to an <see cref="EditorLinkClient"/> (the editor's Output panel).</summary>
public sealed class EditorLinkLogSink(EditorLinkClient client) : ILogSink
{
    private readonly EditorLinkClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public void Write(in LogEntry entry) => _client.TryEnqueueLog(entry);
}
