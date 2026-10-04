using System.Diagnostics;
using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// Replication over real ENet on UDP loopback: a server and two clients in this process, each with its own scene tree,
/// ticked on wall-clock time. Skipped only when the ENet native is not shipped for this OS.
/// </summary>
[Collection(nameof(SerialEnet))]
public sealed class EnetReplicationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    private sealed class Peer : IDisposable
    {
        public readonly SceneTree Tree = NetHarness.CreateTree();
        public readonly MultiplayerApi Api;

        public readonly List<DisconnectReason> Reasons = [];

        public Peer()
        {
            Api = MultiplayerApi.Attach(Tree);
            Api.Disconnected += Reasons.Add;
            Api.RegisterScene(NetHarness.PlayerScene, NetHarness.PlayerUid);
            Api.RegisterScene(NetHarness.BoxScene, NetHarness.BoxUid);
        }

        public void Dispose()
        {
            Api.Stop();
            Tree.Shutdown();
            Tree.Servers.Dispose();
        }
    }

    private static void Pump(Func<bool> condition, params Peer[] peers)
    {
        var clock = Stopwatch.StartNew();
        var last = clock.Elapsed;
        while (!condition())
        {
            if (clock.Elapsed > Deadline)
                Assert.Fail($"Condition not met within {Deadline.TotalSeconds} s: " + string.Join(" | ", peers.Select(p =>
                    $"{p.Api.Mode} connected={p.Api.IsConnected} id={p.Api.LocalPeerId} peers={p.Api.ConnectedPeers.Length} nodes={p.Api.Stats.NetworkedNodes} reasons={string.Join(',', p.Reasons)} rx={p.Api.Stats.BytesReceived} tx={p.Api.Stats.BytesSent}")));
            var now = clock.Elapsed;
            var time = new GameTime { DeltaTime = (float)(now - last).TotalSeconds };
            last = now;
            foreach (var peer in peers)
                peer.Tree.Tick(time);
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void AServerAndTwoClientsReplicateOverEnet()
    {
        EnetTransportTests.SkipUnlessNativePresent();
        var port = EnetTransportTests.FreeUdpPort();
        using var server = new Peer();
        using var a = new Peer();
        using var b = new Peer();
        server.Api.Host(port, maxClients: 4);
        a.Api.Connect("127.0.0.1", port);
        b.Api.Connect("127.0.0.1", port);
        Pump(() => a.Api.IsConnected && b.Api.IsConnected && server.Api.ConnectedPeers.Length == 2, server, a, b);
        Assert.NotEqual(a.Api.LocalPeerId, b.Api.LocalPeerId);

        // Spawn, owned by client A.
        var player = server.Api.Spawn<NetPlayer>(NetHarness.PlayerScene, authority: a.Api.LocalPeerId);
        player.PlayerName = "Ada";
        var box = server.Api.Spawn<NetBox>(NetHarness.BoxScene);
        Pump(() => a.Api.FindNode(box.NetworkId) is not null && b.Api.FindNode(box.NetworkId) is not null, server, a, b);
        var aPlayer = (NetPlayer)a.Api.FindNode(player.NetworkId)!;
        var bPlayer = (NetPlayer)b.Api.FindNode(player.NetworkId)!;
        Assert.Equal("Ada", bPlayer.PlayerName);
        Assert.True(aPlayer.IsNetworkAuthority);
        Assert.False(bPlayer.IsNetworkAuthority);

        // Client → server input from the authority; the result replicates to both clients.
        aPlayer.RpcMove(new Vector3(0, 0, 2));
        bPlayer.RpcMove(new Vector3(9, 9, 9)); // refused locally: B is not the authority
        Pump(() => bPlayer.Health == 100 && bPlayer.Position == new Vector3(0, 0, 2) && aPlayer.Position == new Vector3(0, 0, 2), server, a, b);
        Assert.Equal(new Vector3(0, 0, 2), player.Position);
        Assert.Equal(a.Api.LocalPeerId, Assert.Single(player.Calls).Sender);

        // Server → clients RPC and a property change.
        player.RpcFlash(2, NetTeam.Blue);
        box.Score = 7;
        Pump(() => aPlayer.Calls.Count == 1 && bPlayer.Calls.Count == 1 && ((NetBox)b.Api.FindNode(box.NetworkId)!).Score == 7, server, a, b);

        // A leaves: its player is despawned on B; B keeps the box.
        var left = new List<DisconnectReason>();
        server.Api.PeerLeft += (_, reason) => left.Add(reason);
        a.Api.Stop();
        Pump(() => left.Count == 1 && bPlayer.IsFreed, server, b);
        Assert.NotNull(b.Api.FindNode(box.NetworkId));
        Assert.True(server.Api.Stats.BytesSent > 0);
        Assert.True(b.Api.Stats.Snapshots > 0);

        // The server shuts down: B is told and cleans up.
        var reasons = new List<DisconnectReason>();
        b.Api.Disconnected += reasons.Add;
        server.Api.Stop();
        Pump(() => reasons.Count == 1, b);
        Assert.Equal(DisconnectReason.Shutdown, reasons[0]);
        Assert.Null(b.Api.FindNode(box.NetworkId));
    }
}
