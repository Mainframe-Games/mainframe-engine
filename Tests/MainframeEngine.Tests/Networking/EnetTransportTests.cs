using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Runtime.InteropServices;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// Real ENet over UDP loopback, using the native library shipped from MainframeEngine/runtimes/&lt;rid&gt;/native.
/// Skipped only when that native is genuinely absent for this OS (win/linux until natives.yml artifacts land); a
/// native that is present but fails to load fails the tests.
/// </summary>
[Collection(nameof(SerialEnet))]
public sealed class EnetTransportTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    internal static void SkipUnlessNativePresent()
    {
        var path = Path.Combine(AppContext.BaseDirectory, EnetTransport.NativeLibraryFileName);
        if (!File.Exists(path))
        {
            Assert.Skip($"ENet native '{EnetTransport.NativeLibraryFileName}' is not shipped for {RuntimeInformation.RuntimeIdentifier} yet " +
                        "(MainframeEngine/runtimes/<rid>/native comes from natives.yml).");
        }
    }

    internal static ushort FreeUdpPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return (ushort)((IPEndPoint)socket.LocalEndPoint!).Port;
    }

    internal static void PumpUntil(Func<bool> condition, params MessageBus[] buses)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > Deadline)
                Assert.Fail($"Condition not met within {Deadline.TotalSeconds} s.");
            foreach (var bus in buses)
            {
                bus.Poll();
                bus.Flush();
            }
            Thread.Sleep(1);
        }
    }

    private sealed class Session : IDisposable
    {
        public readonly MessageBus Server;
        public readonly MessageBus Client;
        public readonly BusRecorder ServerEvents;
        public readonly BusRecorder ClientEvents;

        public Session(MessageRegistry? clientRegistry = null)
        {
            var registry = TestRegistry.Create();
            clientRegistry ??= TestRegistry.Create();
            var port = FreeUdpPort();
            Server       = new MessageBus(EnetTransport.Listen(port, maxPeers: 4), registry);
            Client       = new MessageBus(EnetTransport.Connect("127.0.0.1", port, clientRegistry.Fingerprint), clientRegistry);
            ServerEvents = new BusRecorder(Server);
            ClientEvents = new BusRecorder(Client);
        }

        public void Pump(Func<bool> condition) => PumpUntil(condition, Server, Client);

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }

    // Known issue (docs/design/networking.md): on macOS a host bound to an IPv4 literal never sees the connection,
    // with SetHost or SetIP (the native uses dual-stack IPv6 sockets). Kept as a reproduction.
    [Fact(Skip = "Known issue: explicit IPv4 bind address does not accept connections (dual-stack ENet native)")]
    public void AServerBoundToAnExplicitIPv4AddressAcceptsClients()
    {
        SkipUnlessNativePresent();
        var registry = TestRegistry.Create();
        var port = FreeUdpPort();
        using var server = new MessageBus(EnetTransport.Listen(port, maxPeers: 2, bindAddress: "127.0.0.1"), registry);
        using var client = new MessageBus(EnetTransport.Connect("127.0.0.1", port, registry.Fingerprint), registry);
        PumpUntil(() => server.Peers.Length == 1 && client.Peers.Length == 1, server, client);
    }

    [Fact]
    public void LoopbackConnectAndReliableAndUnreliableRoundTrip()
    {
        SkipUnlessNativePresent();
        using var session = new Session();

        session.Pump(() => session.ServerEvents.Connected.Count == 1 && session.ClientEvents.Connected.Count == 1);
        var client = session.Server.Peers[0];
        var server = session.Client.Peers[0];

        // Reliable: client → server → client.
        session.Server.Subscribe((in MessageContext context, in ChatMessage message) =>
            session.Server.Send(context.Sender, new ChatMessage { Sequence = message.Sequence + 1, Text = message.Text + " back" }));
        Assert.True(session.Client.Send(server, new ChatMessage { Sequence = 1, Text = "ping" }));
        session.Pump(() => session.ClientEvents.Chats.Count == 1);

        var (replyContext, reply) = session.ClientEvents.Chats[0];
        Assert.Equal(2, reply.Sequence);
        Assert.Equal("ping back", reply.Text);
        Assert.Equal(NetChannel.Reliable, replyContext.Channel);
        Assert.Equal(server, replyContext.Sender);
        Assert.Equal(client, session.ServerEvents.Chats[0].Context.Sender);

        // Unreliable: loopback does not lose packets in practice, but resend until one arrives to stay robust.
        var transform = new TransformMessage { NodeId = 9, Position = new Vector3(1, -2, 3), Rotation = Quaternion.Identity };
        session.Server.Tick = 1234;
        session.Pump(() =>
        {
            if (session.ClientEvents.Transforms.Count > 0)
                return true;
            session.Server.Send(client, transform, NetChannel.Unreliable);
            return false;
        });

        var (snapshotContext, snapshot) = session.ClientEvents.Transforms[0];
        Assert.Equal(NetChannel.Unreliable, snapshotContext.Channel);
        Assert.Equal(1234u, snapshotContext.Header.Tick);
        Assert.Equal(transform.NodeId, snapshot.NodeId);
        Assert.Equal(transform.Position, snapshot.Position);
        Assert.Empty(session.ServerEvents.Dropped);
        Assert.Empty(session.ClientEvents.Dropped);
    }

    [Fact]
    public void DisconnectedPeersAreRemovedAndStaleIdsFailQuietly()
    {
        SkipUnlessNativePresent();
        using var session = new Session();
        session.Pump(() => session.Server.Peers.Length == 1 && session.Client.Peers.Length == 1);
        var client = session.Server.Peers[0];

        session.Client.Disconnect(session.Client.Peers[0], DisconnectReason.Closed);
        session.Pump(() => session.ServerEvents.Disconnected.Count == 1);

        Assert.Equal((client, DisconnectReason.Closed), session.ServerEvents.Disconnected[0]);
        Assert.Equal(0, session.Server.Peers.Length);
        Assert.Equal(0, ((EnetTransport)session.Server.Transport).ConnectedPeerCount);

        // The old SendToPeers threw here; now a send to a gone peer is reported, and a broadcast reaches no one.
        Assert.False(session.Server.Send(client, new ChatMessage { Text = "late" }));
        Assert.Equal(0, session.Server.Broadcast(new ChatMessage { Text = "late" }));
        Assert.Single(session.ServerEvents.SendFailures);
    }

    [Fact]
    public void ReconnectingClientGetsAFreshPeerId()
    {
        SkipUnlessNativePresent();
        var registry = TestRegistry.Create();
        var port = FreeUdpPort();
        using var server = new MessageBus(EnetTransport.Listen(port, maxPeers: 1), registry);
        var serverEvents = new BusRecorder(server);

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            using var client = new MessageBus(EnetTransport.Connect("localhost", port, registry.Fingerprint), registry);
            PumpUntil(() => serverEvents.Connected.Count == attempt && client.Peers.Length == 1, server, client);
            client.Disconnect(client.Peers[0], DisconnectReason.Closed);
            PumpUntil(() => serverEvents.Disconnected.Count == attempt, server, client);
        }

        // Same ENet slot (maxPeers 1), different generation.
        Assert.NotEqual(serverEvents.Connected[0], serverEvents.Connected[1]);
        Assert.Equal((uint)(ulong)serverEvents.Connected[0], (uint)(ulong)serverEvents.Connected[1]);
    }

    [Fact]
    public void ServerRefusesAClientWithAnotherProtocol()
    {
        SkipUnlessNativePresent();
        using var session = new Session(TestRegistry.Create(protocolVersion: 7));

        session.Pump(() => session.ClientEvents.Disconnected.Count == 1);

        Assert.Equal(DisconnectReason.ProtocolMismatch, session.ClientEvents.Disconnected[0].Reason);
        Assert.Empty(session.ServerEvents.Connected);
        Assert.Equal(0, session.Server.Peers.Length);
    }

    [Fact]
    public void ConnectingToNothingReportsConnectFailed()
    {
        SkipUnlessNativePresent();
        var registry = TestRegistry.Create();
        using var client = new MessageBus(EnetTransport.Connect("127.0.0.1", FreeUdpPort(), registry.Fingerprint, timeoutMs: 1000), registry);
        var events = new BusRecorder(client);

        PumpUntil(() => events.Disconnected.Count == 1, client);

        Assert.Equal(DisconnectReason.ConnectFailed, events.Disconnected[0].Reason);
        Assert.Empty(events.Connected);
    }

    [Fact]
    public void SteadyStateTrafficDoesNotAllocate()
    {
        SkipUnlessNativePresent();
        using var session = new Session();
        session.Pump(() => session.Server.Peers.Length == 1 && session.Client.Peers.Length == 1);
        var client   = session.Server.Peers[0];
        var server   = session.Client.Peers[0];
        var received = 0;
        session.Client.Subscribe((in MessageContext _, in TransformMessage _) => received++);
        session.Server.Subscribe((in MessageContext _, in TransformMessage _) => received++);
        session.ServerEvents.Transforms.Capacity = 8192;
        session.ClientEvents.Transforms.Capacity = 8192;
        var message = new TransformMessage { NodeId = 1, Position = Vector3.One, Rotation = Quaternion.Identity };

        // Reliable both ways, waiting for each round so nothing is in flight when measuring stops.
        void Round(ref int expected)
        {
            session.Server.Send(client, message, NetChannel.Reliable);
            session.Client.Send(server, message, NetChannel.Reliable);
            expected += 2;
            var start = Stopwatch.GetTimestamp();
            while (received < expected)
            {
                if (Stopwatch.GetElapsedTime(start) > Deadline)
                    Assert.Fail("Reliable messages did not arrive.");
                session.Server.Poll();
                session.Client.Poll();
                session.Server.Flush();
                session.Client.Flush();
            }
        }

        var target = 0;
        for (var i = 0; i < 100; i++)
            Round(ref target);

        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 500; i++)
                Round(ref target);
        });

        Assert.Equal(0, allocated);
        Assert.Equal(target, received);
        Assert.True(received >= 1200);
    }

    [Fact]
    public void LibraryIsReferenceCounted()
    {
        SkipUnlessNativePresent();
        var before = EnetLibrary.References;
        var port = FreeUdpPort();
        var first = EnetTransport.Listen(port, 1);
        var second = EnetTransport.Connect("127.0.0.1", port);
        Assert.Equal(before + 2, EnetLibrary.References);

        first.Dispose();
        first.Dispose(); // idempotent
        Assert.Equal(before + 1, EnetLibrary.References);
        second.Dispose();
        Assert.Equal(before, EnetLibrary.References);
    }

    [Fact]
    public void InvalidArgumentsAreRejectedBeforeTouchingTheNetwork()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => EnetTransport.Listen(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => EnetTransport.Listen(1, 5000));
        Assert.Throws<ArgumentException>(() => EnetTransport.Connect(" ", 1));
    }
}

[CollectionDefinition(nameof(SerialEnet))]
public sealed class SerialEnet;
