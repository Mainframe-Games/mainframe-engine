using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// Runs the real <c>mfplughost</c> helper (ADR 0146): started lazily on the first request with <c>--serve
/// &lt;socket&gt;</c>, connected over a Unix domain socket (every OS, Windows 10 1803+ included), checked with a
/// handshake, then driven with synchronous request/reply frames (<see cref="PluginHostProtocol"/>), each with a timeout.
/// A helper that exits, drops the connection or misses a deadline is stopped (<see cref="Stopped"/>); the next request
/// starts a new one (<see cref="Restarted"/>), at most <see cref="MaxRestartsPerMinute"/> times a minute. The helper
/// exits by itself when the connection closes, so a crashed editor never leaves one behind.
/// </summary>
public sealed class PluginHostClient : IPluginHost
{
    /// <summary>Environment variable naming a helper binary to use instead of the one next to the editor.</summary>
    public const string PathVariable = "MAINFRAME_PLUGIN_HOST";

    public const int MaxRestartsPerMinute = 3;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan EncodeTimeout = TimeSpan.FromMinutes(10);

    private static int s_socketCounter;

    private readonly Lock _gate = new();
    private readonly Queue<DateTime> _restarts = new();
    private readonly Queue<string> _stderr = new();
    private Process? _process;
    private Socket? _socket;
    private string? _socketPath;
    private uint _nextId;
    private bool _stoppedUnexpectedly;
    private bool _disposed;

    public PluginHostClient(string helperPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        HelperPath = Path.GetFullPath(helperPath);
    }

    /// <summary>The helper binary this client starts.</summary>
    public string HelperPath { get; }

    /// <summary>The helper's file name on this OS.</summary>
    public static string ExecutableName => OperatingSystem.IsWindows() ? "mfplughost.exe" : "mfplughost";

    /// <summary>
    /// The helper binary for this editor, or null when there is none for this platform: <see cref="PathVariable"/>, else
    /// next to the editor (the build copies <c>runtimes/&lt;rid&gt;/native/mfplughost</c> there), else under
    /// <c>runtimes/&lt;rid&gt;/native/</c>.
    /// </summary>
    public static string? Locate()
    {
        if (Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } overridden)
            return File.Exists(overridden) ? Path.GetFullPath(overridden) : null;
        var beside = Path.Combine(AppContext.BaseDirectory, ExecutableName);
        if (File.Exists(beside))
            return beside;
        var underRuntimes = Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier, "native", ExecutableName);
        return File.Exists(underRuntimes) ? underRuntimes : null;
    }

    /// <summary>Whether a helper binary exists for this platform (VST and Ogg features need it).</summary>
    public static bool IsAvailable => Locate() is not null;

    /// <summary>A client for the located helper, or null (<see cref="Locate"/>). Nothing starts until the first request.</summary>
    public static PluginHostClient? TryCreate() => Locate() is { } path ? new PluginHostClient(path) : null;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
                return Info is not null && _process is { HasExited: false };
        }
    }

    public PluginHostInfo? Info { get; private set; }

    public event Action<PluginHostStop>? Stopped;

    public event Action<PluginHostInfo>? Restarted;

    public PluginHostInfo Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            EnsureStarted();
            return Info!;
        }
    }

    public void Ping(TimeSpan? timeout = null)
    {
        byte[] probe = [0x6D, 0x66, 0x70, 0x68];
        var reply = Request(PluginHostMessage.Ping, probe, timeout ?? DefaultTimeout, "ping", default);
        if (!reply.AsSpan().SequenceEqual(probe))
            throw new PluginHostException("The plugin host answered a ping with the wrong bytes.");
    }

    public PluginHostEncodeResult Encode(string wavPath, string oggPath, int quality, CancellationToken cancellation = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(oggPath);
        var payload = new PluginHostPayloadWriter().Str(Path.GetFullPath(wavPath)).Str(Path.GetFullPath(oggPath)).F32(Math.Clamp(quality, 0, 10)).ToArray();
        var reply = Request(PluginHostMessage.Encode, payload, EncodeTimeout, "encode", cancellation);
        var reader = new PluginHostPayloadReader(reply);
        return new PluginHostEncodeResult(checked((long)reader.U64()), (int)reader.U32(), reader.U16());
    }

    /// <summary>Asks the helper to wait <paramref name="duration"/> before answering (timeout tests).</summary>
    internal void Sleep(TimeSpan duration, TimeSpan timeout) =>
        Request(PluginHostMessage.Sleep, new PluginHostPayloadWriter().U32((uint)duration.TotalMilliseconds).ToArray(), timeout, "sleep", default);

    /// <summary>Kills the helper as a crash would (tests); the client notices on its next request.</summary>
    internal void KillForTesting()
    {
        lock (_gate)
        {
            if (_process is { HasExited: false } process)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (Info is not null && _process is { HasExited: false })
            {
                try
                {
                    Exchange(PluginHostMessage.Shutdown, [], TimeSpan.FromSeconds(1), "shutdown", default);
                    _process.WaitForExit(2000);
                }
                catch (Exception e) when (e is PluginHostException or OperationCanceledException)
                {
                    // Exchange already stopped it.
                }
            }

            Teardown();
        }
    }

    // ── Requests ─────────────────────────────────────────────────────────────────────────────────────────────────

    private byte[] Request(PluginHostMessage type, byte[] payload, TimeSpan timeout, string what, CancellationToken cancellation)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellation.ThrowIfCancellationRequested();
            EnsureStarted();
            return Exchange(type, payload, timeout, what, cancellation);
        }
    }

    // One request/reply on the connection. Any failure but a helper-side error reply stops the helper: the stream may
    // be out of step (a late reply would answer the next request).
    private byte[] Exchange(PluginHostMessage type, byte[] payload, TimeSpan timeout, string what, CancellationToken cancellation)
    {
        var socket = _socket ?? throw new PluginHostException("The plugin host is not connected.");
        var id = ++_nextId;
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        int length;
        ushort replyType;
        byte[] reply;
        try
        {
            var frame = new byte[PluginHostProtocol.HeaderSize + payload.Length];
            PluginHostProtocol.WriteHeader(frame, payload.Length, (ushort)type, id);
            payload.CopyTo(frame, PluginHostProtocol.HeaderSize);
            socket.Send(frame);

            Span<byte> header = stackalloc byte[PluginHostProtocol.HeaderSize];
            ReceiveExact(socket, header, deadline, what, timeout, cancellation);
            (length, replyType, var replyId) = PluginHostProtocol.ReadHeader(header);
            if (replyId != id || length > PluginHostProtocol.MaxPayload)
                throw new IOException($"reply {replyId} ({length} bytes) does not match request {id}");
            reply = new byte[length];
            ReceiveExact(socket, reply, deadline, what, timeout, cancellation);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"[Music] Plugin host stopped: {what} cancelled.");
            Teardown(); // cancelled on purpose: not a crash, no Stopped event
            throw;
        }
        catch (PluginHostTimeoutException)
        {
            Fail($"Plugin host did not answer {what} within {timeout.TotalSeconds:0.#} s", what);
            throw;
        }
        catch (Exception e) when (e is SocketException or IOException or ObjectDisposedException)
        {
            var reason = _process is { HasExited: true } exited
                ? $"Plugin host stopped (exit code {exited.ExitCode})"
                : $"Plugin host connection lost ({e.Message})";
            Fail(reason, what);
            throw new PluginHostException($"{reason} during {what}.", e);
        }

        if (replyType == (ushort)PluginHostMessage.Error)
        {
            var reader = new PluginHostPayloadReader(reply);
            var code = reader.U32();
            throw new PluginHostException($"Plugin host {what} failed: {reader.Str()} (error {code}).");
        }

        if (replyType != ((ushort)type | PluginHostProtocol.ReplyBit))
        {
            Fail($"Plugin host answered {what} with message 0x{replyType:X4}", what);
            throw new PluginHostException($"The plugin host answered {what} with an unexpected message.");
        }

        return reply;
    }

    private static void ReceiveExact(Socket socket, Span<byte> buffer, long deadline, string what, TimeSpan timeout, CancellationToken cancellation)
    {
        var done = 0;
        while (done < buffer.Length)
        {
            cancellation.ThrowIfCancellationRequested();
            var remaining = deadline - Stopwatch.GetTimestamp();
            if (remaining <= 0)
                throw new PluginHostTimeoutException($"The plugin host did not answer {what} within {timeout.TotalSeconds:0.#} s.");
            var micros = (int)Math.Clamp(remaining * 1_000_000 / Stopwatch.Frequency, 1, 50_000);
            if (!socket.Poll(micros, SelectMode.SelectRead))
                continue;
            var read = socket.Receive(buffer[done..]);
            if (read == 0)
                throw new IOException("the plugin host closed the connection");
            done += read;
        }
    }

    // ── Process lifetime ─────────────────────────────────────────────────────────────────────────────────────────

    private void EnsureStarted()
    {
        if (Info is not null && _process is { HasExited: false } && _socket is not null)
            return;
        if (Info is not null)
            Fail(_process is { HasExited: true } exited ? $"Plugin host stopped (exit code {exited.ExitCode})" : "Plugin host stopped", null);

        if (_stoppedUnexpectedly)
        {
            var now = DateTime.UtcNow;
            while (_restarts.Count > 0 && now - _restarts.Peek() > TimeSpan.FromMinutes(1))
                _restarts.Dequeue();
            if (_restarts.Count >= MaxRestartsPerMinute)
                throw new PluginHostException($"The plugin host stopped {_restarts.Count + 1} times in a minute; not restarting it yet.");
            _restarts.Enqueue(now);
        }

        Launch();
        PluginHostInfo info;
        try
        {
            var hello = new PluginHostPayloadWriter().U32(PluginHostProtocol.Version).Str("MainframeEngine.Editor").ToArray();
            var reply = Exchange(PluginHostMessage.Hello, hello, StartTimeout, "hello", default);
            var reader = new PluginHostPayloadReader(reply);
            info = new PluginHostInfo(reader.U32(), reader.Str(), (PluginHostCapabilities)reader.U32(), (int)reader.U32());
        }
        catch (PluginHostException)
        {
            Teardown();
            throw;
        }

        Info = info;
        Log.Info($"[Music] Plugin host {info.HelperVersion} started (pid {info.ProcessId}, protocol {info.ProtocolVersion}, {info.Capabilities}).");
        if (_stoppedUnexpectedly)
        {
            _stoppedUnexpectedly = false;
            Restarted?.Invoke(info);
        }
    }

    private void Launch()
    {
        if (!File.Exists(HelperPath))
            throw new PluginHostException($"The plugin host '{HelperPath}' does not exist.");
        if (!OperatingSystem.IsWindows())
            EnsureExecutable(HelperPath);

        _socketPath = NewSocketPath();
        var start = new ProcessStartInfo(HelperPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        start.ArgumentList.Add("--serve");
        start.ArgumentList.Add(_socketPath);
        _stderr.Clear();
        try
        {
            _process = Process.Start(start) ?? throw new PluginHostException($"Could not start '{HelperPath}'.");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new PluginHostException($"Could not start the plugin host '{HelperPath}': {e.Message}", e);
        }

        _process.ErrorDataReceived += OnHelperOutput;
        _process.OutputDataReceived += OnHelperOutput;
        _process.BeginErrorReadLine();
        _process.BeginOutputReadLine();

        var deadline = Stopwatch.GetTimestamp() + (long)(StartTimeout.TotalSeconds * Stopwatch.Frequency);
        while (true)
        {
            if (_process.HasExited)
            {
                var code = _process.ExitCode;
                Teardown();
                throw new PluginHostException($"The plugin host exited during start-up (exit code {code}){LastErrors()}.");
            }

            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(_socketPath));
                _socket = socket;
                return;
            }
            catch (SocketException)
            {
                socket.Dispose();
            }

            if (Stopwatch.GetTimestamp() > deadline)
            {
                Teardown();
                throw new PluginHostTimeoutException($"The plugin host did not accept a connection within {StartTimeout.TotalSeconds:0} s{LastErrors()}.");
            }

            Thread.Sleep(10);
        }
    }

    private void OnHelperOutput(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Data))
            return;
        lock (_stderr)
        {
            _stderr.Enqueue(e.Data);
            while (_stderr.Count > 8)
                _stderr.Dequeue();
        }

        Log.Warning($"[Music] {e.Data}");
    }

    private string LastErrors()
    {
        lock (_stderr)
            return _stderr.Count == 0 ? "" : ": " + string.Join(" / ", _stderr);
    }

    // An unexpected stop: tear down, remember it (the next start raises Restarted) and tell the owner.
    private void Fail(string reason, string? request)
    {
        Teardown();
        _stoppedUnexpectedly = true;
        Log.Warning($"[Music] {reason}{(request is null ? "" : $" (during {request})")}.");
        Stopped?.Invoke(new PluginHostStop(reason, request));
    }

    private void Teardown()
    {
        Info = null;
        _socket?.Dispose();
        _socket = null;
        if (_process is { } process)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(2000);
                }
            }
            catch (InvalidOperationException)
            {
                // Already gone.
            }

            process.ErrorDataReceived -= OnHelperOutput;
            process.OutputDataReceived -= OnHelperOutput;
            process.Dispose();
            _process = null;
        }

        if (_socketPath is not null)
        {
            try
            {
                File.Delete(_socketPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The helper removes it itself once connected; a leftover is harmless.
            }

            _socketPath = null;
        }
    }

    // sun_path holds 104 (macOS) / 108 bytes: keep the path short.
    private static string NewSocketPath()
    {
        var name = $"mfph-{Environment.ProcessId}-{Interlocked.Increment(ref s_socketCounter)}.sock";
        var path = Path.Combine(Path.GetTempPath(), name);
        if (path.Length > 100 && !OperatingSystem.IsWindows())
            path = Path.Combine("/tmp", name);
        return path;
    }

    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
            return;
        try
        {
            var mode = File.GetUnixFileMode(path);
            if ((mode & UnixFileMode.UserExecute) == 0)
                File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Start reports the real problem.
        }
    }
}
