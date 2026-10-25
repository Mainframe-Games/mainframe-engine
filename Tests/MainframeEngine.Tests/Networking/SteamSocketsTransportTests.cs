using System.Reflection;
using System.Runtime.InteropServices;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// <see cref="SteamSocketsTransport"/>'s own logic over an in-memory Steam (<see cref="FakeSteamNetwork"/>): the hello
/// handshake carrying the connect data, the lanes, unreliable sequencing, disconnect reasons, refusals, message release
/// and the allocation-free receive path. A real relay join needs two Steam accounts: Tests/QA/steam-relay.md.
/// </summary>
public sealed class SteamSocketsTransportTests
{
    private const ulong HostId = 76561197960287930;
    private const ulong ClientId = 76561197960287931;

    private sealed class Recorder : ITransportListener
    {
        public readonly List<string> Events = [];
        public readonly List<(PeerId Peer, NetChannel Channel, byte Value)> Packets = [];
        public Action<PeerId, byte>? OnPacket;

        public void OnPeerConnected(PeerId peer, uint connectData) => Events.Add($"connect {(ulong)peer} {connectData}");

        public void OnPeerDisconnected(PeerId peer, DisconnectReason reason) => Events.Add($"disconnect {(ulong)peer} {reason}");

        public void OnReceive(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
            Packets.Add((peer, channel, payload[0]));
            OnPacket?.Invoke(peer, payload[0]);
        }
    }

    /// <summary>Counts only: the listener of the allocation gate.</summary>
    private sealed class CountingListener : ITransportListener
    {
        public int Received;

        public void OnPeerConnected(PeerId peer, uint connectData)
        {
        }

        public void OnPeerDisconnected(PeerId peer, DisconnectReason reason)
        {
        }

        public void OnReceive(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload) => Received++;
    }

    private sealed class Link : IDisposable
    {
        public readonly FakeSteamNetwork Network = new();
        public readonly FakeSteamSockets HostSteam;
        public readonly FakeSteamSockets ClientSteam;
        public readonly ITransport Host;
        public readonly ITransport Client;
        public readonly Recorder HostEvents = new();
        public readonly Recorder ClientEvents = new();

        public Link(uint connectData = 0xC0FFEE, int maxPeers = 4)
        {
            HostSteam = Network.Account(HostId);
            ClientSteam = Network.Account(ClientId);
            Assert.True(SteamSocketsTransport.TryListen(HostSteam, 0, maxPeers, out var host));
            Host = host;
            Assert.True(SteamSocketsTransport.TryConnect(ClientSteam, HostId, 0, connectData, out var client, out var failure), failure);
            Client = client;
        }

        public void Pump(int rounds = 3)
        {
            for (var i = 0; i < rounds; i++)
            {
                Host.Poll(HostEvents);
                Client.Poll(ClientEvents);
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            Host.Dispose();
            Network.Dispose();
        }
    }

    [Fact]
    public void TheHostReportsTheClientOnceItsHelloCarriesTheConnectData()
    {
        using var link = new Link(connectData: 0xABCD);
        Assert.True(link.Host.IsServer);
        Assert.False(link.Client.IsServer);

        link.Host.Poll(link.HostEvents); // Connecting → accepted → Connected: waits for the hello
        Assert.Empty(link.HostEvents.Events);
        link.Client.Poll(link.ClientEvents); // Connected → sends the hello
        Assert.Equal(["connect 1 0"], link.ClientEvents.Events);
        link.Host.Poll(link.HostEvents);
        Assert.Equal([$"connect 1 {0xABCD}"], link.HostEvents.Events);

        var host = (SteamSocketsTransport)link.Host;
        Assert.Equal(1, host.ConnectedPeerCount);
        Assert.Equal(ClientId, host.GetRemoteSteamId(new PeerId(1)));
        Assert.Equal(HostId, ((SteamSocketsTransport)link.Client).GetRemoteSteamId(new PeerId(1)));
        Assert.Equal(0ul, host.GetRemoteSteamId(new PeerId(2)));
        // The connect data is kept as the connection's Steam user data.
        Assert.Contains(link.Network.Connections.Values, c => c.Owner == link.HostSteam && c.UserData == 0xABCD);
    }

    [Fact]
    public void ChannelsMapToSteamsReliableAndUnreliableSends()
    {
        using var link = new Link();
        link.Pump();

        Assert.True(link.Client.Send(new PeerId(1), NetChannel.Reliable, [1]));
        Assert.True(link.Client.Send(new PeerId(1), NetChannel.Unreliable, [2]));
        Assert.True(link.Host.Send(new PeerId(1), NetChannel.Unreliable, [3]));
        Assert.True(link.Host.Send(new PeerId(1), NetChannel.Reliable, [4]));
        Assert.False(link.Host.Send(new PeerId(2), NetChannel.Reliable, [0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => link.Host.Send(new PeerId(1), (NetChannel)2, [0]));
        link.Pump(1);

        Assert.Equal([(new PeerId(1), NetChannel.Reliable, (byte)1), (new PeerId(1), NetChannel.Unreliable, (byte)2)], link.HostEvents.Packets);
        Assert.Equal([(new PeerId(1), NetChannel.Unreliable, (byte)3), (new PeerId(1), NetChannel.Reliable, (byte)4)], link.ClientEvents.Packets);

        link.Host.Flush();
        link.Client.Flush();
        Assert.Equal(1, link.HostSteam.Flushes);
        Assert.Equal(1, link.ClientSteam.Flushes);
        Assert.Equal(0, link.Network.LiveMessages);
    }

    [Fact]
    public void LateUnreliableMessagesAreDropped()
    {
        using var link = new Link();
        link.Pump();
        var toHost = link.Network.Connections.Single(c => c.Value.Owner == link.HostSteam).Key;

        for (byte i = 1; i <= 3; i++)
            Assert.True(link.Client.Send(new PeerId(1), NetChannel.Unreliable, [i]));
        Assert.True(link.Client.Send(new PeerId(1), NetChannel.Reliable, [9]));
        link.Network.Reverse(toHost); // arrives as R9, U3, U2, U1
        link.Host.Poll(link.HostEvents);

        Assert.Equal([(byte)9, (byte)3], link.HostEvents.Packets.Select(p => p.Value));
        Assert.Equal(0, link.Network.LiveMessages); // dropped messages are released too
    }

    [Fact]
    public void DisconnectReasonsTravelAsSteamAppEndReasons()
    {
        using var link = new Link();
        link.Pump();
        link.Host.Send(new PeerId(1), NetChannel.Reliable, [7]); // sent before the kick: still delivered first

        link.Host.Disconnect(new PeerId(1), DisconnectReason.Kicked);
        Assert.False(link.Host.Send(new PeerId(1), NetChannel.Reliable, [0]));
        Assert.DoesNotContain(link.HostEvents.Events, e => e.StartsWith("disconnect", StringComparison.Ordinal)); // on a later poll
        link.Pump(1);

        Assert.Equal("disconnect 1 Kicked", link.HostEvents.Events[^1]);
        Assert.Equal("disconnect 1 Kicked", link.ClientEvents.Events[^1]);
        Assert.Equal((byte)7, Assert.Single(link.ClientEvents.Packets).Value);
        Assert.Contains(link.Network.Connections.Values, c => c.CloseReason == SteamSocketsTransport.AppEndReasonBase + (int)DisconnectReason.Kicked);
        Assert.Equal(0, ((SteamSocketsTransport)link.Client).ConnectedPeerCount);

        // Ids are never reused: the same account connecting again is a new peer.
        Assert.True(SteamSocketsTransport.TryConnect(link.ClientSteam, HostId, 0, 1, out var again, out _));
        using (again)
        {
            var events = new Recorder();
            for (var i = 0; i < 3; i++)
            {
                link.Host.Poll(link.HostEvents);
                again.Poll(events);
            }

            Assert.Equal("connect 2 1", link.HostEvents.Events[^1]);
        }
    }

    [Fact]
    public void DisposingTellsThePeersShutdown()
    {
        using var link = new Link();
        link.Pump();
        link.Client.Dispose();
        link.Host.Poll(link.HostEvents);
        Assert.Equal("disconnect 1 Shutdown", link.HostEvents.Events[^1]);
        Assert.Throws<ObjectDisposedException>(() => link.Client.Poll(link.ClientEvents));
        Assert.False(link.Client.Send(new PeerId(1), NetChannel.Reliable, [1]));
    }

    [Fact]
    public void ATimeoutIsReported()
    {
        using var link = new Link();
        link.Pump();
        var toClient = link.Network.Connections.Single(c => c.Value.Owner == link.ClientSteam).Key;
        link.Network.Connections[toClient].Open = false;
        link.Network.Events[link.Network.Connections[toClient].Endpoint].Enqueue(new SteamSocketEvent(toClient, SteamSocketEventKind.ProblemDetectedLocally, HostId, 4001));
        link.Client.Poll(link.ClientEvents);
        Assert.Equal("disconnect 1 Timeout", link.ClientEvents.Events[^1]);
    }

    [Fact]
    public void ConnectingToNobodyFails()
    {
        using var network = new FakeSteamNetwork();
        Assert.True(SteamSocketsTransport.TryConnect(network.Account(ClientId), HostId, 0, 0, out var client, out _));
        using (client)
        {
            var events = new Recorder();
            client.Poll(events);
            Assert.Equal(["disconnect 1 ConnectFailed"], events.Events);
        }
    }

    [Fact]
    public void AFullHostRefusesMoreClients()
    {
        using var link = new Link(maxPeers: 1);
        link.Pump();
        Assert.True(SteamSocketsTransport.TryConnect(link.Network.Account(ClientId + 1), HostId, 0, 0, out var second, out _));
        using (second)
        {
            var events = new Recorder();
            for (var i = 0; i < 3; i++)
            {
                link.Host.Poll(link.HostEvents);
                second.Poll(events);
            }

            Assert.Equal(["disconnect 1 ConnectFailed"], events.Events);
            Assert.Single(link.HostEvents.Events);
        }
    }

    [Fact]
    public void AConnectionWithoutAHelloIsClosed()
    {
        using var network = new FakeSteamNetwork();
        var hostSteam = network.Account(HostId);
        Assert.True(SteamSocketsTransport.TryListen(hostSteam, 0, 4, out var host));
        using (host)
        {
            var events = new Recorder();
            var stranger = network.Account(ClientId);
            var endpoint = stranger.Connect(HostId, 0, out var connection);
            host.Poll(events); // accepted
            Assert.True(stranger.TryDequeueEvent(endpoint, out var connected));
            Assert.Equal(SteamSocketEventKind.Connected, connected.Kind);
            Assert.True(stranger.Send(connection, [1], reliable: false)); // ignored: no hello yet
            Assert.True(stranger.Send(connection, [1, 2, 3], reliable: true)); // not a hello
            host.Poll(events);

            Assert.Empty(events.Events);
            Assert.Empty(events.Packets);
            Assert.Equal(0, ((SteamSocketsTransport)host).ConnectedPeerCount);
            Assert.Contains(network.Connections.Values, c => c.CloseReason == SteamSocketsTransport.AppEndReasonBase + (int)DisconnectReason.ProtocolMismatch);
            Assert.Equal(0, network.LiveMessages);
        }
    }

    [Fact]
    public void NoSteamOrOurOwnIdRefusesAtOnce()
    {
        using var network = new FakeSteamNetwork();
        var steam = network.Account(HostId);
        Assert.False(SteamSocketsTransport.TryConnect(steam, HostId, 0, 0, out _, out var self));
        Assert.Contains("this Steam account", self, StringComparison.Ordinal);

        steam.Running = false;
        Assert.False(SteamSocketsTransport.TryListen(steam, 0, 4, out _));
        Assert.False(SteamSocketsTransport.TryConnect(steam, ClientId, 0, 0, out _, out var failure));
        Assert.Equal("Steam is not running", failure);
        Assert.Throws<ArgumentOutOfRangeException>(() => SteamSocketsTransport.TryListen(steam, 0, 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SteamSocketsTransport.TryConnect(steam, 0, 0, 0, out _, out _));
    }

    [Fact]
    public void SteamShuttingDownDropsEveryPeer()
    {
        using var link = new Link();
        link.Pump();
        link.HostSteam.Running = false;
        link.ClientSteam.Running = false;
        Assert.False(link.Host.Send(new PeerId(1), NetChannel.Reliable, [1]));
        link.Pump(1);
        Assert.Equal("disconnect 1 Shutdown", link.HostEvents.Events[^1]);
        Assert.Equal("disconnect 1 Shutdown", link.ClientEvents.Events[^1]);

        // Steam starting again does not revive the old handles.
        link.HostSteam.Running = true;
        link.Host.Poll(link.HostEvents);
        Assert.False(link.Host.Send(new PeerId(1), NetChannel.Reliable, [1]));
    }

    [Fact]
    public void EveryReceivedMessageIsReleasedEvenWhenAHandlerThrows()
    {
        using var link = new Link();
        link.Pump();
        for (byte i = 1; i <= 5; i++)
            link.Client.Send(new PeerId(1), NetChannel.Reliable, [i]);
        link.HostEvents.OnPacket = (_, value) =>
        {
            if (value == 2)
                throw new InvalidOperationException("handler bug");
        };

        Assert.Throws<InvalidOperationException>(() => link.Host.Poll(link.HostEvents));
        Assert.Equal(0, link.Network.LiveMessages);
        Assert.Equal(5 + 1, link.Network.Released); // the batch and the hello
    }

    [Fact]
    public void AHandlerMayDisposeTheTransportMidPoll()
    {
        using var link = new Link();
        link.Pump();
        for (byte i = 1; i <= 3; i++)
            link.Client.Send(new PeerId(1), NetChannel.Reliable, [i]);
        link.HostEvents.OnPacket = (_, _) => link.Host.Dispose();
        link.Host.Poll(link.HostEvents);
        Assert.Single(link.HostEvents.Packets);
        Assert.Equal(0, link.Network.LiveMessages);
    }

    [Fact]
    public void SteadyStateSendingAndPollingDoNotAllocate()
    {
        using var link = new Link();
        link.Pump();
        var listener = new CountingListener();
        var payload = new byte[64];

        void Round()
        {
            for (var i = 0; i < 40; i++) // more than one receive batch
            {
                link.Client.Send(new PeerId(1), i % 2 == 0 ? NetChannel.Reliable : NetChannel.Unreliable, payload);
                link.Host.Send(new PeerId(1), NetChannel.Unreliable, payload);
            }

            link.Client.Flush();
            link.Host.Flush();
            link.Host.Poll(listener);
            link.Client.Poll(listener);
        }

        for (var i = 0; i < 20; i++)
            Round(); // warm up the fake's queues and dictionaries

        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 100; i++)
                Round();
        });
        Assert.Equal(0, allocated);
        Assert.True(listener.Received > 0);
    }

    [Theory]
    [InlineData(1000, DisconnectReason.Closed)]
    [InlineData(1003, DisconnectReason.Kicked)]
    [InlineData(1004, DisconnectReason.Shutdown)]
    [InlineData(1002, DisconnectReason.ProtocolMismatch)]
    [InlineData(1999, DisconnectReason.Closed)]
    [InlineData(0, DisconnectReason.Closed)]
    [InlineData(2001, DisconnectReason.Closed)]
    public void SteamEndReasonsMapToDisconnectReasons(int endReason, DisconnectReason expected) =>
        Assert.Equal(expected, SteamSocketsTransport.ToDisconnectReason(endReason));

    [Fact]
    public void TheHelloRoundTrips()
    {
        Span<byte> hello = stackalloc byte[SteamSocketsTransport.HelloSize];
        SteamSocketsTransport.WriteHello(hello, 0xDEADBEEF);
        Assert.True(SteamSocketsTransport.TryReadHello(hello, out var data));
        Assert.Equal(0xDEADBEEFu, data);
        Assert.False(SteamSocketsTransport.TryReadHello(hello[..7], out _));
        hello[0] ^= 1;
        Assert.False(SteamSocketsTransport.TryReadHello(hello, out _));
    }

    [Fact]
    public void TheSelectorUsesSteamWhileItRunsAndFallsBackOtherwise()
    {
        using var network = new FakeSteamNetwork();
        var hostSteam = network.Account(HostId);
        var memberSteam = network.Account(ClientId);
        Assert.True(SteamSocketsTransport.TryListen(hostSteam, 0, 4, out var steamHost));
        using var listening = steamHost;
        var loopback = new LoopbackTransportFactory();
        using var loopbackHost = LoopbackTransport.CreateServer();
        var connect = NetworkAddress.FormatList(NetworkAddress.Steam(HostId), loopback.Add("host", loopbackHost));

        var selector = new TransportSelector();
        selector.Register(new SteamSocketsTransportFactory(memberSteam));
        selector.Register(loopback);
        Assert.True(selector.TryConnect(connect, 0, out var viaSteam, out var address));
        Assert.Equal(NetworkAddress.SteamScheme, address.Scheme);
        Assert.IsType<SteamSocketsTransport>(viaSteam);
        viaSteam.Dispose();

        // The host's own account (one account cannot relay to itself) and a machine without Steam fall through.
        var hostSelector = new TransportSelector();
        hostSelector.Register(new SteamSocketsTransportFactory(hostSteam));
        hostSelector.Register(loopback);
        Assert.True(hostSelector.TryConnect(connect, 0, out var own, out address));
        Assert.Equal(NetworkAddress.LoopbackScheme, address.Scheme);
        own.Dispose();

        memberSteam.Running = false;
        Assert.True(selector.TryConnect(connect, 0, out var withoutSteam, out address));
        Assert.Equal(NetworkAddress.LoopbackScheme, address.Scheme);
        withoutSteam.Dispose();
    }

    [Fact]
    public void ALobbyJoinReplicatesOverSteamSockets()
    {
        using var network = new FakeSteamNetwork();
        var serverTree = NetHarness.CreateTree();
        using var server = MultiplayerApi.Attach(serverTree);
        server.RegisterScene(NetHarness.BoxScene, NetHarness.BoxUid);
        Assert.True(SteamSocketsTransport.TryListen(network.Account(HostId), 0, 8, out var host));
        server.StartServer(host);

        var selector = new TransportSelector();
        selector.Register(new SteamSocketsTransportFactory(network.Account(ClientId)));
        var clientTree = NetHarness.CreateTree();
        using var client = MultiplayerApi.Attach(clientTree);
        client.RegisterScene(NetHarness.BoxScene, NetHarness.BoxUid);
        Assert.True(client.TryConnect($"steam:{HostId};enet:203.0.113.5:7777", selector));

        var time = new GameTime { DeltaTime = NetHarness.FrameTime };
        for (var frame = 0; frame < 20 && !client.IsConnected; frame++)
        {
            serverTree.Tick(time);
            clientTree.Tick(time);
        }

        Assert.True(client.IsConnected);
        var box = server.Spawn<NetBox>(NetHarness.BoxScene);
        serverTree.Tick(time);
        clientTree.Tick(time);
        Assert.NotNull(client.FindNode(box.NetworkId));

        client.Stop();
        server.Stop();
        clientTree.Shutdown();
        serverTree.Shutdown();
        Assert.Equal(0, network.LiveMessages);
    }

    /// <summary>
    /// The shim reads <c>SteamNetworkingMessage_t</c> fields in place; their offsets must match Facepunch's own
    /// declaration of the struct (the same in its Win64 and Posix builds), which mirrors steam_api's.
    /// </summary>
    [Fact]
    public void TheShimReadsSteamMessagesAtFacepunchsOffsets()
    {
        SteamworksLoader.Register();
        var facepunch = Assembly.Load(OperatingSystem.IsWindows() ? "Facepunch.Steamworks.Win64" : "Facepunch.Steamworks.Posix");
        var netMsg = facepunch.GetType("Steamworks.Data.NetMsg", throwOnError: true)!;
        var offsets = SteamSocketsApi.MessageFieldOffsets();

        Assert.Equal((int)Marshal.OffsetOf(netMsg, "DataPtr"), offsets.Data);
        Assert.Equal((int)Marshal.OffsetOf(netMsg, "DataSize"), offsets.Size);
        Assert.Equal((int)Marshal.OffsetOf(netMsg, "Connection"), offsets.Connection);
        Assert.Equal((int)Marshal.OffsetOf(netMsg, "MessageNumber"), offsets.MessageNumber);
        Assert.Equal((int)Marshal.OffsetOf(netMsg, "Flags"), offsets.Flags);
    }
}
