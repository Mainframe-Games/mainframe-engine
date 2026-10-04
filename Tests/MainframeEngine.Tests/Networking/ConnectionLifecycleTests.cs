using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>Disconnect cleanup, version/fingerprint rejection, timeouts and kicks.</summary>
public sealed class ConnectionLifecycleTests
{
    [Fact]
    public void ALeavingClientsNodesAreDespawnedForEveryoneElse()
    {
        using var net = new NetHarness();
        var leaving = net.Join();
        var staying = net.Join();
        var left = new List<(PeerId, DisconnectReason)>();
        net.Server.PeerLeft += (peer, reason) => left.Add((peer, reason));

        var owned = net.Server.Spawn<NetPlayer>(NetHarness.PlayerScene, authority: leaving.Api.LocalPeerId);
        var shared = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);
        var ownedCopy = staying.Find<NetPlayer>(owned.NetworkId)!;
        var leavingId = leaving.Api.LocalPeerId;

        leaving.Api.Stop();
        Assert.Equal(MultiplayerMode.Offline, leaving.Api.Mode);
        net.Step(3);

        Assert.Equal((leavingId, DisconnectReason.Shutdown), Assert.Single(left));
        Assert.Equal([staying.Api.LocalPeerId], net.Server.ConnectedPeers.ToArray());
        Assert.True(owned.IsFreed);
        Assert.True(ownedCopy.IsFreed);
        Assert.NotNull(staying.Api.FindNode(shared.NetworkId));
        Assert.False(net.Server.GetPeerStats(leavingId, out _));

        // The client that left freed everything the server had spawned there.
        Assert.Empty(leaving.Tree.CurrentScene!.Children);
    }

    [Fact]
    public void OwnedNodesCanBeHandedBackToTheServerInstead()
    {
        using var net = new NetHarness(configure: api => api.DespawnOwnedOnDisconnect = false);
        var leaving = net.Join();
        var staying = net.Join();
        var owned = net.Server.Spawn<NetPlayer>(NetHarness.PlayerScene, authority: leaving.Api.LocalPeerId);
        net.Step(3);

        leaving.Api.Stop();
        net.Step(3);
        Assert.False(owned.IsFreed);
        Assert.True(owned.IsNetworkAuthority);
        Assert.Equal(MultiplayerApi.ServerPeerId, staying.Find<NetPlayer>(owned.NetworkId)!.NetworkAuthority);
    }

    [Fact]
    public void ClientsSeeTheServerShutDownAndFreeReplicatedNodes()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var reasons = new List<DisconnectReason>();
        client.Api.Disconnected += reasons.Add;
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);
        var copy = client.Find<NetBox>(box.NetworkId)!;

        net.Server.Stop();
        Assert.False(box.IsFreed);          // the server keeps its nodes…
        Assert.False(box.IsNetworked);      // …as plain nodes
        net.Step(2);

        Assert.Equal([DisconnectReason.Shutdown], reasons);
        Assert.Equal(MultiplayerMode.Offline, client.Api.Mode);
        Assert.True(copy.IsFreed);
        Assert.Equal(0, client.Api.Stats.NetworkedNodes);
    }

    [Fact]
    public void KickedClientsAreToldWhy()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var reasons = new List<DisconnectReason>();
        client.Api.Disconnected += reasons.Add;
        var left = new List<DisconnectReason>();
        net.Server.PeerLeft += (_, reason) => left.Add(reason);

        net.Server.Kick(client.Api.LocalPeerId);
        net.Server.Kick(client.Api.LocalPeerId); // once is enough
        net.Step(2);
        Assert.Equal([DisconnectReason.Kicked], reasons);
        Assert.Equal([DisconnectReason.Kicked], left);
        Assert.Empty(net.Server.ConnectedPeers.ToArray());
        net.Server.Kick(new PeerId(12345)); // unknown: ignored
    }

    [Fact]
    public void AMismatchedMessageRegistryIsRefusedAtConnect()
    {
        using var net = new NetHarness();
        var joined = 0;
        net.Server.PeerJoined += _ => joined++;
        var reasons = new List<DisconnectReason>();
        // Protocol version 2 on the client: its registry fingerprint differs.
        var client = net.AddClient(api => api.Disconnected += reasons.Add, messages: new MessageRegistry(protocolVersion: 2));
        net.Step(5);

        Assert.Equal([DisconnectReason.ProtocolMismatch], reasons);
        Assert.Equal(0, joined);
        Assert.False(client.Api.IsConnected);
        Assert.Equal(MultiplayerMode.Offline, client.Api.Mode);
    }

    [Fact]
    public void AMismatchedReplicationFingerprintIsRefusedInTheHandshake()
    {
        using var net = new NetHarness();
        var joined = 0;
        net.Server.PeerJoined += _ => joined++;
        var reasons = new List<DisconnectReason>();
        // Same messages, but one more spawnable scene: spawn indices would disagree.
        var client = net.AddClient(api =>
        {
            api.RegisterScene(PackedScene.Pack(new Node { Name = "Extra" }), "scn_00000000beef");
            api.Disconnected += reasons.Add;
        });
        Assert.NotEqual(net.Server.ReplicationFingerprint, client.Api.ReplicationFingerprint);
        net.Step(5);

        Assert.Equal([DisconnectReason.ProtocolMismatch], reasons);
        Assert.Equal(0, joined);
        Assert.Empty(net.Server.ConnectedPeers.ToArray());
    }

    [Fact]
    public void ConnectionsThatNeverSayHelloTimeOut()
    {
        using var net = new NetHarness(configure: api => api.HandshakeTimeout = 0.5f);
        // A raw transport that connects with the right registry fingerprint but speaks no multiplayer protocol.
        var registry = new MessageRegistry();
        var api = new MultiplayerApi(new SceneTree(), registry); // registers the same engine messages
        var endpoint = (LoopbackTransport)net.Server.Bus!.Transport;
        using var silent = new MessageBus(endpoint.ConnectClient(registry.Fingerprint), registry);
        var reasons = new List<DisconnectReason>();
        silent.PeerDisconnected += (_, reason) => reasons.Add(reason);

        for (var frame = 0; frame < 60 && reasons.Count == 0; frame++)
        {
            net.Step();
            silent.Poll();
        }

        Assert.Equal([DisconnectReason.Timeout], reasons);
        api.Dispose();
    }

    [Fact]
    public void SilentClientsTimeOutOnTheServer()
    {
        using var net = new NetHarness(configure: api => api.PeerTimeout = 1f);
        var frozen = net.Join();
        var alive = net.Join();
        var left = new List<(PeerId, DisconnectReason)>();
        net.Server.PeerLeft += (peer, reason) => left.Add((peer, reason));

        frozen.Ticking = false; // stops acknowledging
        net.Step(90);
        Assert.Equal((frozen.Api.LocalPeerId, DisconnectReason.Timeout), Assert.Single(left));
        Assert.Equal([alive.Api.LocalPeerId], net.Server.ConnectedPeers.ToArray());
    }

    [Fact]
    public void ClientsTimeOutWhenTheServerGoesSilent()
    {
        using var net = new NetHarness(conditions: NetworkConditions.None, configure: api => api.PeerTimeout = 1f);
        var client = net.Join();
        var reasons = new List<DisconnectReason>();
        client.Api.Disconnected += reasons.Add;
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);

        // The server's packets stop arriving (its snapshots all lost; it is still running).
        ((SimulatedTransport)net.Server.Bus!.Transport).Conditions = new NetworkConditions { Loss = 1 };
        net.Step(90);
        Assert.Equal([DisconnectReason.Timeout], reasons);
        Assert.Null(client.Api.FindNode(box.NetworkId));
    }

    [Fact]
    public void ClientsGiveUpWhenTheHandshakeNeverCompletes()
    {
        // A server that accepts the connection but never answers the hello.
        var serverRegistry = new MessageRegistry();
        using var serverApi = new MultiplayerApi(new SceneTree(), serverRegistry);
        var endpoint = LoopbackTransport.CreateServer();
        using var mute = new MessageBus(endpoint, serverRegistry);

        var tree = NetHarness.CreateTree();
        using var api = MultiplayerApi.Attach(tree);
        api.HandshakeTimeout = 0.5f;
        var reasons = new List<DisconnectReason>();
        api.Disconnected += reasons.Add;
        api.StartClient(endpoint.ConnectClient(api.Messages.Fingerprint));

        var time = new GameTime { DeltaTime = NetHarness.FrameTime };
        for (var frame = 0; frame < 60; frame++)
        {
            mute.Poll();
            tree.Tick(time);
        }

        Assert.Equal([DisconnectReason.Timeout], reasons);
        Assert.Equal(MultiplayerMode.Offline, api.Mode);
        mute.Poll();
        Assert.Empty(mute.Peers.ToArray()); // the client hung up on the server
        tree.Shutdown();
    }

    [Fact]
    public void StartingTwiceOrOnTheWrongTransportThrows()
    {
        var tree = NetHarness.CreateTree();
        using var api = MultiplayerApi.Attach(tree);
        var (server, client) = LoopbackTransport.CreatePair();
        Assert.Throws<ArgumentException>(() => api.StartServer(client));
        Assert.Throws<ArgumentException>(() => api.StartClient(server));
        api.StartServer(server);
        Assert.Throws<InvalidOperationException>(() => api.StartServer(LoopbackTransport.CreateServer()));
        Assert.Throws<InvalidOperationException>(() => api.RegisterScene(NetHarness.BoxScene, "scn_000000000001"));
        Assert.Throws<ArgumentException>(() => new MultiplayerApi(tree, api.Messages)); // frozen registry
        api.Stop();
        api.Stop();
        client.Dispose();
        tree.Shutdown();
    }
}
