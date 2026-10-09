using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using MainframeEngine.EditorLink;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// The editor link when a child process holds one of its sockets. macOS has no <c>SOCK_CLOEXEC</c>/<c>accept4</c>: .NET
/// creates a socket and sets <c>FD_CLOEXEC</c> in a second call, and <c>Process.Start</c> forks, so a child started in
/// between inherits the socket for its whole life (the editor runs <c>dotnet build</c>, whose build servers outlive it,
/// and games while its link listens). Only macOS leaks this way, but the kernel behaviour reproduced here is the same on
/// Linux.
/// </summary>
public sealed class EditorLinkInheritedSocketTests
{
    private static readonly EditorLinkHello Hello = new(EditorLinkProtocol.Version, Environment.ProcessId, "LinkTest", "0.0.0-dev");

    [Fact]
    public void DisposeReturnsWhileAChildSharesTheListenersInheritableDescriptor()
    {
        SkipOnWindows();
        var server = new EditorLinkServer();
        using var child = InheritingChild.Start(server.ListenerSocket, leaveParentInheritable: true);

        // .NET only unblocks a blocking call on a descriptor with FD_CLOEXEC before closing it (one without may be shared):
        // a blocking accept would make Dispose spin forever, even after the child exits. A pending async accept is
        // cancelled instead.
        DisposeWithin(server, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void LogsWaitForTheEditorWhileAClosedListenerOnlyAChildHoldsAcceptsConnections()
    {
        SkipOnWindows();
        var first = new EditorLinkServer();
        var port = first.Port;
        using var child = InheritingChild.Start(first.ListenerSocket, leaveParentInheritable: false); // as a fork leaks it
        DisposeWithin(first, TimeSpan.FromSeconds(5)); // the editor closed its listener; the child's copy keeps completing handshakes

        using var client = new EditorLinkClient(port, Hello);
        for (var i = 0; i < 10; i++)
            Assert.True(client.TryEnqueueLog(new LogEntry(Log.Level.Info, DateTime.UtcNow, "Test", "queued " + i, "M", "F.cs", 1)));

        // A handshake is not an editor: nothing welcomes the game, so it keeps its logs instead of writing them to nobody.
        Assert.False(Wait.Until(() => client.IsConnected, seconds: 1));
        Assert.Equal(0, client.ConnectionCount);

        child.Dispose(); // its exit resets the connections waiting in the listener's backlog
        using var second = ListenOn(port);
        var seen = new List<EditorLinkMessage>();
        Wait.For(second, seen, m => m.Type == EditorLinkMessageType.Log && m.Log.Message == "queued 9");
        Assert.Equal(Enumerable.Range(0, 10).Select(i => "queued " + i),
            seen.Where(m => m.Type == EditorLinkMessageType.Log).Select(m => m.Log.Message));
        Assert.Equal(1, client.ConnectionCount);
        Assert.Equal(0, client.DroppedLogCount);
        Assert.Equal(0, second.DroppedMessageCount);
    }

    private static void SkipOnWindows()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Sockets inherited through fork are a Unix behaviour.");
    }

    private static void DisposeWithin(EditorLinkServer server, TimeSpan timeout)
    {
        var dispose = new Thread(server.Dispose) { IsBackground = true, Name = "EditorLinkServer.Dispose" };
        dispose.Start();
        Assert.True(dispose.Join(timeout), $"EditorLinkServer.Dispose took over {timeout.TotalSeconds} s");
    }

    // Until the child's copy of the listener is closed, binding the port fails.
    private static EditorLinkServer ListenOn(int port)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new EditorLinkServer(port);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.AddressAlreadyInUse && stopwatch.Elapsed < TimeSpan.FromSeconds(10))
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>A child process holding a socket, as a fork in .NET's socket-then-FD_CLOEXEC window leaves it; killed on dispose.</summary>
    private sealed class InheritingChild : IDisposable
    {
        private Process? _process;

        private InheritingChild(Process process) => _process = process;

        /// <param name="leaveParentInheritable">Clear FD_CLOEXEC on the parent's descriptor itself and leave it cleared (a
        /// fork leak sets it on the parent's copy right after).</param>
        public static InheritingChild Start(Socket socket, bool leaveParentInheritable)
        {
            var fd = (int)socket.Handle;
            if (leaveParentInheritable)
            {
                // ioctl rather than fcntl(F_SETFD): fcntl's argument is variadic, which P/Invoke cannot pass on Apple arm64.
                Assert.True(Ioctl(fd, OperatingSystem.IsMacOS() ? 0x20006602u : 0x5450u) == 0, $"FIONCLEX failed: errno {Marshal.GetLastPInvokeError()}");
                return new InheritingChild(StartSleep());
            }

            // dup() returns a descriptor without FD_CLOEXEC for the same socket: the child inherits it, the parent closes it.
            var copy = Dup(fd);
            Assert.True(copy >= 0, $"dup failed: errno {Marshal.GetLastPInvokeError()}");
            try
            {
                return new InheritingChild(StartSleep());
            }
            finally
            {
                _ = Close(copy);
            }

            static Process StartSleep() => Process.Start(new ProcessStartInfo("/bin/sleep", "60") { UseShellExecute = false })!;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _process, null) is not { } process)
                return;
            if (!process.HasExited)
                process.Kill();
            process.WaitForExit();
            process.Dispose();
        }

        [DllImport("libc", EntryPoint = "dup", SetLastError = true)]
        private static extern int Dup(int fd);

        [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
        private static extern int Ioctl(int fd, nuint request);

        [DllImport("libc", EntryPoint = "close", SetLastError = true)]
        private static extern int Close(int fd);
    }
}
