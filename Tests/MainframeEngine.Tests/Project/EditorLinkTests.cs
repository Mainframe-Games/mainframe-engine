using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using MainframeEngine.EditorLink;

namespace MainframeEngine.Tests.Project;

internal static class Wait
{
    /// <summary>Polls <paramref name="condition"/> until it holds or <paramref name="seconds"/> pass.</summary>
    public static bool Until(Func<bool> condition, double seconds = 10)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition())
        {
            if (stopwatch.Elapsed.TotalSeconds > seconds)
                return false;
            Thread.Sleep(5);
        }

        return true;
    }

    /// <summary>
    /// Like <see cref="For"/> but also matches messages already in <paramref name="seen"/> (messages of two connections
    /// interleave: a reconnect's hello may arrive before the old connection's Disconnected marker).
    /// </summary>
    public static EditorLinkMessage Any(EditorLinkServer server, List<EditorLinkMessage> seen, Func<EditorLinkMessage, bool> match, double seconds = 10)
    {
        foreach (var message in seen)
            if (match(message))
                return message;
        return For(server, seen, match, seconds);
    }

    /// <summary>Reads messages from <paramref name="server"/> into <paramref name="seen"/> until one matches.</summary>
    public static EditorLinkMessage For(EditorLinkServer server, List<EditorLinkMessage> seen, Func<EditorLinkMessage, bool> match, double seconds = 10)
    {
        EditorLinkMessage found = default;
        var ok = Until(() =>
        {
            while (server.TryRead(out var message))
            {
                seen.Add(message);
                if (match(message))
                {
                    found = message;
                    return true;
                }
            }

            return false;
        }, seconds);
        Assert.True(ok, $"No matching editor-link message; saw: {string.Join(", ", seen.Select(m => m.Type))}");
        return found;
    }
}

public sealed class EditorLinkProtocolTests
{
    private static EditorLinkMessage RoundTrip(Action<ArrayBufferWriter<byte>> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        write(buffer);
        var frame = buffer.WrittenSpan;
        Assert.True(EditorLinkProtocol.TryReadHeader(frame, out var length));
        Assert.Equal(frame.Length - EditorLinkProtocol.HeaderLength, length);
        Assert.True(EditorLinkProtocol.TryDecode(frame[EditorLinkProtocol.HeaderLength..], out var message));
        return message;
    }

    [Fact]
    public void EveryMessageRoundTrips()
    {
        var hello = new EditorLinkHello(EditorLinkProtocol.Version, 4242, "Space Game ✓", "1.2.3");
        Assert.Equal(hello, RoundTrip(b => EditorLinkProtocol.WriteHello(b, hello)).Hello);

        var entry = new LogEntry(Log.Level.Warning, new DateTime(2026, 10, 5, 1, 2, 3, DateTimeKind.Utc), "Audio", "naïve ✓ message", "OnReady", "/src/Player.cs", 77);
        var log = RoundTrip(b => EditorLinkProtocol.WriteLog(b, entry));
        Assert.Equal(EditorLinkMessageType.Log, log.Type);
        Assert.Equal(entry, log.Log);

        var status = new EditorLinkStatus(GameRunState.Paused, 123456789012UL, 59.5f, "Content/Scenes/Main.mscene");
        Assert.Equal(status, RoundTrip(b => EditorLinkProtocol.WriteStatus(b, status)).Status);
        Assert.Equal(17, RoundTrip(b => EditorLinkProtocol.WriteLogDropped(b, 17)).DroppedCount);
        Assert.Equal(-3, RoundTrip(b => EditorLinkProtocol.WriteGoodbye(b, -3)).ExitCode);

        var command = new EditorLinkCommand(EditorCommandKind.ReloadScene, "scn_0123456789ab");
        Assert.Equal(command, RoundTrip(b => EditorLinkProtocol.WriteCommand(b, command)).Command);
        Assert.Equal(new EditorLinkCommand(EditorCommandKind.Stop), RoundTrip(b => EditorLinkProtocol.WriteCommand(b, new EditorLinkCommand(EditorCommandKind.Stop))).Command);
    }

    [Fact]
    public void FramesAppendBackToBack()
    {
        var buffer = new ArrayBufferWriter<byte>();
        EditorLinkProtocol.WriteLogDropped(buffer, 1);
        EditorLinkProtocol.WriteGoodbye(buffer, 2);

        var span = buffer.WrittenSpan;
        Assert.True(EditorLinkProtocol.TryReadHeader(span, out var first));
        Assert.True(EditorLinkProtocol.TryDecode(span.Slice(4, first), out var a));
        span = span[(4 + first)..];
        Assert.True(EditorLinkProtocol.TryReadHeader(span, out var second));
        Assert.True(EditorLinkProtocol.TryDecode(span.Slice(4, second), out var b));
        Assert.Equal((EditorLinkMessageType.LogDropped, EditorLinkMessageType.Goodbye), (a.Type, b.Type));
        Assert.Equal(4 + second, span.Length);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData((uint)EditorLinkProtocol.MaxFrameLength + 1)]
    [InlineData(uint.MaxValue)]
    public void BadLengthsAreRejected(uint length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, length);
        Assert.False(EditorLinkProtocol.TryReadHeader(header, out _));
        Assert.False(EditorLinkProtocol.TryReadHeader(header.AsSpan(0, 3), out _));
    }

    [Fact]
    public void MalformedBodiesAreRejected()
    {
        var buffer = new ArrayBufferWriter<byte>();
        EditorLinkProtocol.WriteHello(buffer, new EditorLinkHello(1, 2, "p", "v"));
        var body = buffer.WrittenSpan[4..].ToArray();

        Assert.False(EditorLinkProtocol.TryDecode([], out _));
        Assert.False(EditorLinkProtocol.TryDecode(body.AsSpan(0, body.Length - 1), out _)); // truncated
        Assert.False(EditorLinkProtocol.TryDecode([.. body, 0], out _)); // trailing bytes
        Assert.False(EditorLinkProtocol.TryDecode([200, .. body[1..]], out _)); // unknown type
        Assert.False(EditorLinkProtocol.TryDecode([(byte)EditorLinkMessageType.Connected], out _)); // local-only types
        Assert.False(EditorLinkProtocol.TryDecode([(byte)EditorLinkMessageType.Command, 99, 0, 0, 0, 0], out _)); // unknown command
        Assert.False(EditorLinkProtocol.TryDecode([(byte)EditorLinkMessageType.Command, 1, 2, 0, 0, 0, 0xC3, 0x28], out _)); // invalid UTF-8
        Assert.False(EditorLinkProtocol.TryDecode([(byte)EditorLinkMessageType.Command, 1, 255, 255, 255, 255], out _)); // string past the end

        buffer.Clear();
        EditorLinkProtocol.WriteLog(buffer, new LogEntry(Log.Level.Info, DateTime.UtcNow, "", "", "", "", 0));
        var log = buffer.WrittenSpan[4..].ToArray();
        log[1] = (byte)(Log.Level.Info | Log.Level.Error);
        Assert.False(EditorLinkProtocol.TryDecode(log, out _)); // not a single level
    }

    [Theory]
    [InlineData(Log.Level.Info | Log.Level.Error, Log.Level.Error)]
    [InlineData(Log.Level.Debug | Log.Level.Verbose, Log.Level.Debug)]
    [InlineData(Log.Level.None, Log.Level.Info)]
    [InlineData(Log.Level.Verbose, Log.Level.Info)]
    [InlineData(Log.Level.Fatal, Log.Level.Fatal)]
    public void HandBuiltEntriesWithOddLevelsAreSentAsOneSeverity(Log.Level level, Log.Level sent)
    {
        var buffer = new ArrayBufferWriter<byte>();
        EditorLinkProtocol.WriteLog(buffer, new LogEntry(level, DateTime.UtcNow, "", "odd", "", "", 0));

        Assert.True(EditorLinkProtocol.TryDecode(buffer.WrittenSpan[4..], out var message)); // the receiver keeps the link
        Assert.Equal(sent, message.Log.Level);
    }

    [Fact]
    public void HugeMessagesAreTruncatedToFitAFrame()
    {
        var huge = new string('é', EditorLinkProtocol.MaxFrameLength);
        var entry = new LogEntry(Log.Level.Error, DateTime.UtcNow, "", huge, "", "", 0);
        var buffer = new ArrayBufferWriter<byte>();
        EditorLinkProtocol.WriteLog(buffer, entry);

        Assert.True(buffer.WrittenCount <= EditorLinkProtocol.MaxFrameLength + 4);
        Assert.True(EditorLinkProtocol.TryReadHeader(buffer.WrittenSpan, out _));
        Assert.True(EditorLinkProtocol.TryDecode(buffer.WrittenSpan[4..], out var message));
        Assert.StartsWith("éééé", message.Log.Message, StringComparison.Ordinal);
        Assert.True(message.Log.Message.Length < huge.Length);
    }
}

public sealed class EditorLinkConnectionTests
{
    private static readonly EditorLinkHello Hello = new(EditorLinkProtocol.Version, Environment.ProcessId, "LinkTest", "0.0.0-dev");

    private static LogEntry Entry(string message) => new(Log.Level.Info, DateTime.UtcNow, "Test", message, "M", "F.cs", 1);

    [Fact]
    public void GameStreamsLogsAndStatusAndReceivesCommands()
    {
        using var server = new EditorLinkServer();
        using var client = new EditorLinkClient(server.Port, Hello);
        var seen = new List<EditorLinkMessage>();

        Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Connected);
        Assert.Equal(Hello, Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Hello).Hello);
        Assert.True(Wait.Until(() => client.IsConnected));

        for (var i = 0; i < 500; i++)
            Assert.True(client.TryEnqueueLog(Entry("line " + i)));
        Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Log && m.Log.Message == "line 499");
        Assert.Equal(Enumerable.Range(0, 500).Select(i => "line " + i),
            seen.Where(m => m.Type == EditorLinkMessageType.Log).Select(m => m.Log.Message)); // in order, none lost

        client.ReportStatus(new EditorLinkStatus(GameRunState.Running, 1, 60, "a"));
        client.ReportStatus(new EditorLinkStatus(GameRunState.Paused, 2, 60, "b"));
        var status = Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Status && m.Status.Frame == 2).Status;
        Assert.Equal(GameRunState.Paused, status.State);

        Assert.Equal(1, server.SendCommand(new EditorLinkCommand(EditorCommandKind.Pause)));
        Assert.Equal(1, server.SendCommand(new EditorLinkCommand(EditorCommandKind.ReloadScene, "Content/x.mscene")));
        var commands = new List<EditorLinkCommand>();
        Assert.True(Wait.Until(() =>
        {
            while (client.TryReceiveCommand(out var c))
                commands.Add(c);
            return commands.Count == 2;
        }));
        Assert.Equal([new EditorLinkCommand(EditorCommandKind.Pause), new EditorLinkCommand(EditorCommandKind.ReloadScene, "Content/x.mscene")], commands);

        Assert.True(client.SendGoodbye(3, TimeSpan.FromSeconds(5)));
        Assert.Equal(3, Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Goodbye).ExitCode);
        client.Dispose();
        Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Disconnected);
        Assert.Equal(0, server.SendCommand(new EditorLinkCommand(EditorCommandKind.Stop)));
    }

    [Fact]
    public void TheGameReconnectsAfterTheEditorRestartsAndSendsWhatQueuedMeanwhile()
    {
        int port;
        using var client = CreateClientWithServer(out var first, out port);
        var seen = new List<EditorLinkMessage>();
        Wait.For(first, seen, m => m.Type == EditorLinkMessageType.Hello);
        first.Dispose();
        Assert.True(Wait.Until(() => !client.IsConnected));

        for (var i = 0; i < 10; i++)
            Assert.True(client.TryEnqueueLog(Entry("while down " + i)));

        using var second = new EditorLinkServer(port);
        var again = new List<EditorLinkMessage>();
        Wait.For(second, again, m => m.Type == EditorLinkMessageType.Hello);
        Wait.For(second, again, m => m.Type == EditorLinkMessageType.Log && m.Log.Message == "while down 9");
        Assert.Equal(10, again.Count(m => m.Type == EditorLinkMessageType.Log));
        Assert.Equal(2, client.ConnectionCount);
    }

    private static EditorLinkClient CreateClientWithServer(out EditorLinkServer server, out int port)
    {
        server = new EditorLinkServer();
        port = server.Port;
        return new EditorLinkClient(port, Hello);
    }

    [Fact]
    public void TheGameReconnectsWhenTheEditorDropsTheConnection()
    {
        using var server = new EditorLinkServer();
        using var client = new EditorLinkClient(server.Port, Hello);
        var seen = new List<EditorLinkMessage>();
        var firstId = Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Hello).GameId;

        server.Disconnect();
        Wait.Any(server, seen, m => m.Type == EditorLinkMessageType.Disconnected && m.GameId == firstId);
        Wait.Any(server, seen, m => m.Type == EditorLinkMessageType.Hello && m.GameId != firstId); // a fresh hello on the new connection
        Assert.True(Wait.Until(() => client.ConnectionCount == 2), $"connections {client.ConnectionCount}; saw {string.Join(", ", seen.Select(m => $"{m.Type}#{m.GameId}:{m.Hello.ProjectName}"))}");
        Assert.True(client.TryEnqueueLog(Entry("after reconnect")));
        Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Log && m.Log.Message == "after reconnect");
    }

    [Fact]
    public void AStalledEditorNeverBlocksTheGameAndDropsAreReported()
    {
        // An "editor" that accepts but does not read: the socket buffers fill, then the client's queue.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new EditorLinkClient(port, Hello, queueCapacity: 64);
        using var socket = listener.AcceptSocket();
        Assert.True(Wait.Until(() => client.IsConnected));

        var message = new string('x', 4096);
        var stopwatch = Stopwatch.StartNew();
        var accepted = 0;
        for (var i = 0; i < 20_000; i++)
            if (client.TryEnqueueLog(Entry(message)))
                accepted++;
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Enqueueing took {stopwatch.Elapsed}");
        Assert.True(client.DroppedLogCount > 0);
        Assert.Equal(20_000, accepted + client.DroppedLogCount);

        // Start reading: every accepted entry arrives, followed by the drop report.
        using var stream = new NetworkStream(socket);
        var header = new byte[4];
        var logs = 0;
        long reported = 0;
        var deadline = Stopwatch.StartNew();
        while (reported < client.DroppedLogCount && deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            stream.ReadExactly(header);
            Assert.True(EditorLinkProtocol.TryReadHeader(header, out var length));
            var body = new byte[length];
            stream.ReadExactly(body);
            Assert.True(EditorLinkProtocol.TryDecode(body, out var frame));
            if (frame.Type == EditorLinkMessageType.Log)
                logs++;
            else if (frame.Type == EditorLinkMessageType.LogDropped)
                reported += frame.DroppedCount;
        }

        Assert.Equal(client.DroppedLogCount, reported);
        Assert.Equal(accepted, logs);
    }

    [Fact]
    public void LoggingWhileNoEditorListensNeverBlocks()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using var client = new EditorLinkClient(port, Hello, queueCapacity: 100);
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 10_000; i++)
            client.TryEnqueueLog(Entry("nobody listens"));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.False(client.IsConnected);
        Assert.Equal(10_000 - 100, client.DroppedLogCount);
        Assert.False(client.TryReceiveCommand(out _));
        Assert.False(client.SendGoodbye(0, TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void LoggingDoesNotCutTheReconnectBackOffShort()
    {
        int port;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
        }

        using var client = new EditorLinkClient(port, Hello, queueCapacity: 100);
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(1.5))
        {
            client.TryEnqueueLog(Entry("noise")); // each one signals the client thread
            client.ReportStatus(new EditorLinkStatus(GameRunState.Running, 1, 60, ""));
            Thread.Sleep(1);
        }

        // 100 ms doubling: ~5 attempts in 1.5 s (one per log line would be hundreds).
        Assert.InRange(client.ConnectAttempts, 1, 8);
    }

    [Fact]
    public void AGameThatStopsReadingDoesNotStallCommandsToOtherGames()
    {
        using var server = new EditorLinkServer();
        var seen = new List<EditorLinkMessage>();
        using var good = new EditorLinkClient(server.Port, Hello with { ProjectName = "Good" });
        var goodId = Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Hello && m.Hello.ProjectName == "Good").GameId;

        // A "game" that says hello and then never reads: its socket buffers fill up.
        using var stalled = new TcpClient();
        stalled.ReceiveBufferSize = 1024;
        stalled.Connect(IPAddress.Loopback, server.Port);
        var hello = new ArrayBufferWriter<byte>();
        EditorLinkProtocol.WriteHello(hello, Hello with { ProjectName = "Stalled" });
        stalled.GetStream().Write(hello.WrittenSpan);
        var stalledId = Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Hello && m.Hello.ProjectName == "Stalled").GameId;

        var flood = new Thread(() =>
        {
            var big = new EditorLinkCommand(EditorCommandKind.ReloadScene, new string('x', 60_000));
            while (server.SendCommand(stalledId, big))
            {
            }
        })
        { IsBackground = true };
        flood.Start();
        Thread.Sleep(200); // let the flood block in a write

        var stopwatch = Stopwatch.StartNew();
        Assert.True(server.SendCommand(goodId, new EditorLinkCommand(EditorCommandKind.Ping)));
        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(500), $"a command to another game took {stopwatch.Elapsed}");
        Assert.True(Wait.Until(() => good.TryReceiveCommand(out var c) && c.Kind == EditorCommandKind.Ping));

        // The stalled game's write times out and that game is dropped.
        Assert.True(flood.Join(TimeSpan.FromSeconds(EditorLinkServer.SendTimeoutMilliseconds / 1000.0 + 10)));
        Assert.Equal(stalledId, Wait.Any(server, seen, m => m.Type == EditorLinkMessageType.Disconnected && m.GameId == stalledId).GameId);
        Assert.Contains(goodId, server.ConnectedGames);
    }

    [Fact]
    public void AGarbagePeerIsDroppedAndTheNextGameConnects()
    {
        using var server = new EditorLinkServer();
        var seen = new List<EditorLinkMessage>();
        using (var garbage = new TcpClient())
        {
            garbage.Connect(IPAddress.Loopback, server.Port);
            garbage.GetStream().Write([0xFF, 0xFF, 0xFF, 0xFF, 1, 2, 3]);
            Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Disconnected);
        }

        using var client = new EditorLinkClient(server.Port, Hello);
        Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Hello);
        Assert.True(server.IsConnected);
    }

    [Fact]
    public void SeveralGamesConnectAtOnceAndAreAddressedById()
    {
        using var server = new EditorLinkServer();
        var seen = new List<EditorLinkMessage>();
        using var first = new EditorLinkClient(server.Port, Hello with { ProjectName = "Server" });
        var firstId = Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Hello && m.Hello.ProjectName == "Server").GameId;
        using var second = new EditorLinkClient(server.Port, Hello with { ProjectName = "Client" });
        var secondId = Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Hello && m.Hello.ProjectName == "Client").GameId;

        Assert.NotEqual(firstId, secondId);
        Assert.Equal(2, seen.Count(m => m.Type == EditorLinkMessageType.Connected));
        Assert.Equal([firstId, secondId], server.ConnectedGames.Order());

        Assert.True(first.TryEnqueueLog(Entry("from server")));
        Assert.Equal(firstId, Wait.For(server, seen, m => m.Type == EditorLinkMessageType.Log && m.Log.Message == "from server").GameId);

        Assert.True(server.SendCommand(secondId, new EditorLinkCommand(EditorCommandKind.Pause)));
        Assert.True(Wait.Until(() => second.TryReceiveCommand(out var c) && c.Kind == EditorCommandKind.Pause));
        Assert.False(first.TryReceiveCommand(out _));

        Assert.Equal(2, server.SendCommand(new EditorLinkCommand(EditorCommandKind.Ping)));
        Assert.True(Wait.Until(() => first.TryReceiveCommand(out var c) && c.Kind == EditorCommandKind.Ping));

        server.Disconnect(firstId);
        Wait.Any(server, seen, m => m.Type == EditorLinkMessageType.Disconnected && m.GameId == firstId);
        var reconnected = Wait.Any(server, seen, m => m.Type == EditorLinkMessageType.Hello && m.Hello.ProjectName == "Server" && m.GameId != firstId).GameId;
        Assert.True(reconnected > secondId); // a new connection gets a new id
        Assert.False(server.SendCommand(firstId, new EditorLinkCommand(EditorCommandKind.Stop)));
    }
}
