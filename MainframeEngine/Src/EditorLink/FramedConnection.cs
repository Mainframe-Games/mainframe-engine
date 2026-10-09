using System.Net.Sockets;

namespace MainframeEngine.EditorLink;

/// <summary>
/// A TCP connection carrying editor-link frames. One thread reads (<see cref="ReadFrame"/>), one writes
/// (<see cref="Write"/>); <see cref="Dispose"/> from any thread unblocks both.
/// </summary>
internal sealed class FramedConnection : IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly byte[] _header = new byte[EditorLinkProtocol.HeaderLength];
    private byte[] _body = new byte[4096];
    private int _disposed;

    /// <param name="socket">A connected socket (owned).</param>
    /// <param name="sendTimeoutMilliseconds">Fails a write that cannot complete in time (0 = wait forever): a peer that
    /// stops reading must not hang the writer.</param>
    public FramedConnection(Socket socket, int sendTimeoutMilliseconds = 0)
    {
        _socket = socket;
        _socket.NoDelay = true;
        _socket.SendTimeout = sendTimeoutMilliseconds;
        _stream = new NetworkStream(socket, ownsSocket: true);
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Serializes writers of this connection (the server's command senders).</summary>
    public Lock SendGate { get; } = new();

    /// <summary>A scratch buffer for frames written under <see cref="SendGate"/>.</summary>
    public System.Buffers.ArrayBufferWriter<byte> SendBuffer { get; } = new(256);

    /// <summary>Fails a <see cref="ReadFrame"/> that waits longer than this (0 = wait forever).</summary>
    public int ReceiveTimeout
    {
        set => _socket.ReceiveTimeout = value;
    }

    /// <summary>Writes whole frames (any number, back to back).</summary>
    public void Write(ReadOnlySpan<byte> frames) => _stream.Write(frames);

    /// <summary>
    /// Blocks for the next frame body (type + payload). False on a clean close between frames; throws
    /// <see cref="IOException"/> on errors, truncation or an invalid length.
    /// </summary>
    public bool ReadFrame(out ReadOnlyMemory<byte> body)
    {
        body = default;
        var first = _stream.Read(_header, 0, _header.Length);
        if (first == 0)
            return false;
        if (first < _header.Length)
            _stream.ReadExactly(_header, first, _header.Length - first);
        if (!EditorLinkProtocol.TryReadHeader(_header, out var length))
            throw new IOException("Invalid editor-link frame length.");
        if (_body.Length < length)
            _body = new byte[Math.Max(length, _body.Length * 2)];
        _stream.ReadExactly(_body, 0, length);
        body = _body.AsMemory(0, length);
        return true;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            _socket.Shutdown(SocketShutdown.Both);
        }
        catch (SocketException)
        {
        }
        catch (ObjectDisposedException)
        {
        }

        _stream.Dispose();
    }
}
