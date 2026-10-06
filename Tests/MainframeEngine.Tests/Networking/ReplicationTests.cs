using System.Drawing;
using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

using Color = System.Drawing.Color;

/// <summary>
/// Node replication over <see cref="LoopbackTransport"/>: handshake, spawn/despawn ordering, late join, property
/// replication (including networked descendants), authority and connection lifecycle. RPCs, interpolation and bad
/// networks have their own suites.
/// </summary>
public sealed class ReplicationTests
{
    private static Node Level(SceneTree tree) => tree.CurrentScene!;

    [Fact]
    public void ClientsCompleteTheHandshakeAndGetPeerIds()
    {
        using var net = new NetHarness();
        var joined = new List<PeerId>();
        net.Server.PeerJoined += joined.Add;
        var connected = 0;

        var a = net.AddClient(api => api.ConnectedToServer += () => connected++);
        var b = net.AddClient(api => api.ConnectedToServer += () => connected++);
        Assert.False(a.Api.IsConnected);
        Assert.Equal(MultiplayerApi.UnassignedPeerId, a.Api.LocalPeerId);
        net.StepUntil(() => a.Api.IsConnected && b.Api.IsConnected);

        Assert.Equal(2, connected);
        Assert.Equal([a.Api.LocalPeerId, b.Api.LocalPeerId], joined);
        Assert.NotEqual(a.Api.LocalPeerId, b.Api.LocalPeerId);
        Assert.NotEqual(MultiplayerApi.ServerPeerId, a.Api.LocalPeerId);
        Assert.Equal(joined, net.Server.ConnectedPeers.ToArray());
        Assert.True(net.Server.IsServer);
        Assert.True(a.Api.IsClient);
        Assert.False(a.Api.IsServer);
        Assert.Equal(MultiplayerApi.DefaultTickRate, a.Api.ServerTickRate);
        Assert.Equal(net.Server.ReplicationFingerprint, a.Api.ReplicationFingerprint);
    }

    [Fact]
    public void SpawnReplicatesTheSceneWithItsInitialState()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var spawned = new List<Node>();
        client.Api.NodeSpawned += spawned.Add;

        var player = net.Server.Spawn<NetPlayer>(NetHarness.PlayerScene);
        player.Name = "Hero";
        player.Position = new Vector3(1, 2, 3);
        player.Health = 75;
        player.PlayerName = "Ada";
        player.Team = NetTeam.Blue;
        player.Loadout = new NetLoadout { Weapon = 4, Ammo = 30 };
        player.LocalOnly = 9;
        player.GetNode<NetPart>("Body/Part").Tint = Color.FromArgb(255, 10, 20, 30);
        Assert.True(player.IsNetworked);
        Assert.NotEqual(0u, player.NetworkId);

        net.StepUntil(() => spawned.Count == 1);
        var copy = Assert.IsType<NetPlayer>(spawned[0]);
        Assert.Same(copy, client.Find<NetPlayer>(player.NetworkId));
        Assert.Equal("Hero", copy.Name);
        Assert.Same(Level(client.Tree), copy.Parent);
        Assert.Equal(new Vector3(1, 2, 3), copy.Position);
        Assert.Equal(75, copy.Health);
        Assert.Equal("Ada", copy.PlayerName);
        Assert.Equal(NetTeam.Blue, copy.Team);
        Assert.Equal(new NetLoadout { Weapon = 4, Ammo = 30 }, copy.Loadout);
        Assert.Equal(0, copy.LocalOnly);
        Assert.True(copy.IsNetworked);

        // The networked descendant has the next id on both sides.
        var part = copy.GetNode<NetPart>("Body/Part");
        Assert.Equal(player.NetworkId + 1, part.NetworkId);
        Assert.Equal(Color.FromArgb(255, 10, 20, 30).ToArgb(), part.Tint.ToArgb());
        Assert.False(copy.GetNode("Body").IsNetworked); // plain nodes are not networked
        Assert.Equal(2, client.Api.Stats.NetworkedNodes);
        Assert.Equal(1, client.Api.Stats.Spawns);
    }

    [Fact]
    public void PropertyChangesReplicateAndOnlyChangedMembersAreSent()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var all = net.Server.Spawn<NetAllTypes>(NetHarness.AllTypesScene);
        net.Step(5);
        var copy = client.Find<NetAllTypes>(all.NetworkId)!;

        all.Bool = true;
        all.Byte = 200;
        all.SByte = -100;
        all.Short = -30000;
        all.UShort = 60000;
        all.Int = -123456;
        all.UInt = 4000000000;
        all.Long = long.MinValue + 1;
        all.ULong = ulong.MaxValue - 1;
        all.Float = 1.5f;
        all.Double = Math.PI;
        all.Decimal = 12.345m;
        all.Char = 'λ';
        all.Text = "héllo";
        all.V2 = new Vector2(1, 2);
        all.V4 = new Vector4(1, 2, 3, 4);
        all.T2 = new Transform2D(new Vector2(0, 1), new Vector2(-1, 0), new Vector2(5, 6));
        net.Step(5);

        Assert.True(copy.Bool);
        Assert.Equal(200, copy.Byte);
        Assert.Equal(-100, copy.SByte);
        Assert.Equal(-30000, copy.Short);
        Assert.Equal(60000, copy.UShort);
        Assert.Equal(-123456, copy.Int);
        Assert.Equal(4000000000, copy.UInt);
        Assert.Equal(long.MinValue + 1, copy.Long);
        Assert.Equal(ulong.MaxValue - 1, copy.ULong);
        Assert.Equal(1.5f, copy.Float);
        Assert.Equal(Math.PI, copy.Double);
        Assert.Equal(12.345m, copy.Decimal);
        Assert.Equal('λ', copy.Char);
        Assert.Equal("héllo", copy.Text);
        Assert.Equal(new Vector2(1, 2), copy.V2);
        Assert.Equal(new Vector4(1, 2, 3, 4), copy.V4);
        Assert.Equal(new Transform2D(new Vector2(0, 1), new Vector2(-1, 0), new Vector2(5, 6)), copy.T2);

        // Nothing changed: snapshots carry no entries (header + flags + terminator only).
        net.Step(10);
        Assert.Equal(MessageHeader.Size + 2, net.Server.Stats.LastSnapshotBytes);

        // One member changed: one small entry.
        all.Int = 7;
        net.Step(8);
        Assert.Equal(7, copy.Int);
        Assert.True(net.Server.Stats.LastSnapshotBytes <= MessageHeader.Size + 2, "settled again after the ack");
    }

    [Fact]
    public void ASnapshotCarriesOnlyTheChangedMember()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var all = net.Server.Spawn<NetAllTypes>(NetHarness.AllTypesScene);
        net.Step(10);

        // Freeze the client's acks so the next snapshot can be measured before it is acknowledged.
        client.Ticking = false;
        all.Int = 7;
        net.Step(3);
        // header + flags(1) + id(1) + length(1) + mask(1) + int(4) + terminator(1); repeated until acknowledged.
        Assert.Equal(MessageHeader.Size + 1 + 1 + 1 + 1 + 4 + 1, net.Server.Stats.LastSnapshotBytes);
    }

    [Fact]
    public void NetworkedDescendantsReplicateTheirOwnMembersAndRpcs()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var player = net.Server.Spawn<NetPlayer>(NetHarness.PlayerScene);
        net.Step(3);
        var part = player.GetNode<NetPart>("Body/Part");
        part.Tint = Color.Red;
        part.Pose = new Transform3D(Basis.Identity, new Vector3(0, 9, 0));
        part.Spin = 2;
        net.Step(30);

        var copy = client.Find<NetPart>(part.NetworkId)!;
        Assert.Equal(Color.Red.ToArgb(), copy.Tint.ToArgb());
        Assert.Equal(new Vector3(0, 9, 0), copy.Pose.Origin);
        Assert.Equal(2, copy.Spin, 3);

        part.RpcPulse(3);
        net.Step(2);
        Assert.Equal([(byte)3], copy.Pulses);
    }

    [Fact]
    public void DerivedTypesReplicateBaseAndOwnMembers()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var boss = net.Server.Spawn<NetBoss>(NetHarness.BossScene);
        boss.Health = 500;
        boss.Rage = 0.5f;
        net.Step(3);
        var copy = client.Find<NetBoss>(boss.NetworkId)!;
        Assert.Equal(500, copy.Health);
        Assert.Equal(0.5f, copy.Rage);

        boss.Rage = 1;
        boss.Health = 1;
        net.Step(3);
        Assert.Equal(1f, copy.Rage);
        Assert.Equal(1, copy.Health);
    }

    [Fact]
    public void SpawnsUnderNetworkedParentsAndDespawnsCascade()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var despawned = new List<string>();
        client.Api.NodeDespawned += node => despawned.Add(node.Name);

        var player = net.Server.Spawn<NetPlayer>(NetHarness.PlayerScene);
        var marker = net.Server.Spawn(NetHarness.MarkerScene, parent: player); // nested spawn under a networked node
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        Assert.True(player.NetworkId < marker.NetworkId && marker.NetworkId < box.NetworkId);
        net.Step(3);

        var playerCopy = client.Find<NetPlayer>(player.NetworkId)!;
        var markerCopy = client.Api.FindNode(marker.NetworkId)!;
        Assert.Same(playerCopy, markerCopy.Parent);
        Assert.True(markerCopy.IsNetworked); // roots are networked even without replicated members
        Assert.Equal(["Player", "Box"], Level(client.Tree).Children.Select(c => c.Name));

        // Freeing the player despawns it and everything under it; the box stays.
        player.QueueFree();
        net.Step(3);
        Assert.True(playerCopy.IsFreed);
        Assert.True(markerCopy.IsFreed);
        Assert.Null(client.Api.FindNode(player.NetworkId));
        Assert.Null(client.Api.FindNode(marker.NetworkId));
        Assert.NotNull(client.Api.FindNode(box.NetworkId));
        Assert.Contains("Player", despawned);
        Assert.Equal(["Box"], Level(client.Tree).Children.Select(c => c.Name));
    }

    [Fact]
    public void SpawnAndFreeInTheSameFrameSendsNothing()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var spawns = 0;
        client.Api.NodeSpawned += _ => spawns++;

        var box = net.Server.Spawn(NetHarness.BoxScene);
        box.QueueFree();
        net.Step(5);
        Assert.Equal(0, spawns);
        Assert.Empty(Level(client.Tree).Children);
        Assert.Equal(0, net.Server.Stats.NetworkedNodes);
    }

    [Fact]
    public void FreeingANetworkedDescendantDespawnsOnlyIt()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var player = net.Server.Spawn<NetPlayer>(NetHarness.PlayerScene);
        net.Step(3);
        var part = player.GetNode<NetPart>("Body/Part");
        var partCopy = client.Find<NetPart>(part.NetworkId)!;

        part.QueueFree();
        net.Step(3);
        Assert.True(partCopy.IsFreed);
        Assert.NotNull(client.Find<NetPlayer>(player.NetworkId));

        // A client joining afterwards does not get the despawned part.
        var late = net.Join();
        net.Step(3);
        var lateCopy = late.Find<NetPlayer>(player.NetworkId)!;
        Assert.Null(lateCopy.GetNodeOrNull("Body/Part"));
        Assert.NotNull(lateCopy.GetNodeOrNull("Body"));
    }

    [Fact]
    public void LateJoinersReceiveTheFullCurrentState()
    {
        using var net = new NetHarness();
        var early = net.Join();
        var boxes = new List<NetBox>();
        for (var i = 0; i < 5; i++)
        {
            var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
            box.Score = i;
            boxes.Add(box);
        }

        net.Step(10);
        boxes[1].QueueFree();
        boxes[3].NetPosition = new Vector3(3, 3, 3);
        boxes[4].Score = 44;
        net.Server.SetAuthority(boxes[2], early.Api.LocalPeerId);
        net.Step(10);

        var late = net.Join();
        net.Step(2);
        Assert.Null(late.Api.FindNode(boxes[1].NetworkId));
        Assert.Equal(4, Level(late.Tree).ChildCount);
        Assert.Equal(new Vector3(3, 3, 3), late.Find<NetBox>(boxes[3].NetworkId)!.Position);
        Assert.Equal(44, late.Find<NetBox>(boxes[4].NetworkId)!.Score);
        Assert.Equal(0, late.Find<NetBox>(boxes[0].NetworkId)!.Score);
        Assert.Equal(early.Api.LocalPeerId, late.Find<NetBox>(boxes[2].NetworkId)!.NetworkAuthority);

        // And keeps up afterwards.
        boxes[0].Score = 100;
        net.Step(3);
        Assert.Equal(100, late.Find<NetBox>(boxes[0].NetworkId)!.Score);
        Assert.Equal(100, early.Find<NetBox>(boxes[0].NetworkId)!.Score);
    }

    [Fact]
    public void AuthorityIsReplicatedAndDecidesIsNetworkAuthority()
    {
        using var net = new NetHarness();
        var owner = net.Join();
        var other = net.Join();
        var player = net.Server.Spawn<NetPlayer>(NetHarness.PlayerScene, authority: owner.Api.LocalPeerId);
        net.Step(3);

        Assert.False(player.IsNetworkAuthority); // the owner, not the server
        var mine = owner.Find<NetPlayer>(player.NetworkId)!;
        var theirs = other.Find<NetPlayer>(player.NetworkId)!;
        Assert.True(mine.IsNetworkAuthority);
        Assert.False(theirs.IsNetworkAuthority);
        Assert.Equal(owner.Api.LocalPeerId, theirs.NetworkAuthority);
        Assert.Equal(owner.Api.LocalPeerId, owner.Find<NetPart>(player.NetworkId + 1)!.NetworkAuthority); // descendants too

        net.Server.SetAuthority(player, other.Api.LocalPeerId);
        net.Step(2);
        Assert.False(mine.IsNetworkAuthority);
        Assert.True(theirs.IsNetworkAuthority);

        // Not networked: always the authority (single player).
        Assert.True(new Node().IsNetworkAuthority);
        Assert.Throws<ArgumentException>(() => net.Server.SetAuthority(new Node(), owner.Api.LocalPeerId));
        Assert.Throws<InvalidOperationException>(() => owner.Api.SetAuthority(mine, owner.Api.LocalPeerId));
    }

    [Fact]
    public void SpawningRequiresARunningServerAndARegisteredScene()
    {
        using var net = new NetHarness();
        var client = net.Join();
        Assert.Throws<InvalidOperationException>(() => client.Api.Spawn(NetHarness.BoxScene));
        Assert.Throws<InvalidOperationException>(() => net.Server.Spawn(PackedScene.Pack(new Node { Name = "Unregistered" })));
        Assert.Throws<InvalidOperationException>(() => net.Server.RegisterScene(NetHarness.BoxScene, "scn_00000000ffff"));
        var outside = new Node();
        Assert.Throws<ArgumentException>(() => net.Server.Spawn(NetHarness.BoxScene, outside));
        outside.Free();
    }

    [Fact]
    public void SpawnsAtAPathUnderNonNetworkedParents()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var serverRoom = new Node3D { Name = "Room" };
        Level(net.ServerTree).AddChild(serverRoom);
        Level(client.Tree).AddChild(new Node3D { Name = "Room" }); // the client's level has the same layout

        var box = net.Server.Spawn(NetHarness.BoxScene, serverRoom);
        net.Step(3);
        Assert.Equal("/root/Level/Room/Box", client.Api.FindNode(box.NetworkId)!.GetPath().Path);
    }

    [Fact]
    public void ClientFreeingAReplicatedNodeLocallyDoesNotStallAcknowledgements()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var a = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        var b = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);
        client.Find<NetBox>(a.NetworkId)!.QueueFree();
        net.Step(2);
        Assert.Null(client.Api.FindNode(a.NetworkId));

        a.Score = 5;
        b.Score = 6;
        net.Step(5);
        Assert.Equal(6, client.Find<NetBox>(b.NetworkId)!.Score);
        Assert.True(net.Server.GetPeerStats(client.Api.LocalPeerId, out var stats));
        Assert.True(net.Server.Tick - stats.AckedTick <= 2, $"acked {stats.AckedTick}, server at {net.Server.Tick}");
    }

    [Fact]
    public void BandwidthStatsAreReported()
    {
        using var net = new NetHarness();
        var client = net.Join();
        for (var i = 0; i < 10; i++)
            net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        var boxes = Level(net.ServerTree).Children.OfType<NetBox>().ToArray();
        for (var frame = 0; frame < 120; frame++)
        {
            foreach (var box in boxes)
                box.NetPosition += Vector3.UnitX * 0.01f;
            net.Step();
        }

        var server = net.Server.Stats;
        var clientStats = client.Api.Stats;
        Assert.True(server.BytesSent > 0);
        Assert.True(server.SendBytesPerSecond > 0);
        Assert.True(clientStats.ReceiveBytesPerSecond > 0);
        Assert.True(clientStats.SendBytesPerSecond > 0); // acks
        Assert.InRange(server.Snapshots, 55, 65); // ~30 Hz for 2 s
        Assert.Equal(10, server.NetworkedNodes);
        Assert.Equal(10, server.Spawns);
        Assert.True(net.Server.GetPeerStats(client.Api.LocalPeerId, out var peer));
        Assert.True(peer.Ready);
        Assert.True(peer.BytesSent > server.LastSnapshotBytes);
        Assert.True(peer.SnapshotsSent > 0);
        Assert.True(peer.RoundTripTime > 0);
        Assert.False(net.Server.GetPeerStats(new PeerId(999), out _));
    }

    [Fact]
    public void TickRateIsConfigurable()
    {
        using var net = new NetHarness(configure: api => api.TickRate = 10);
        var client = net.Join();
        var start = net.Server.Stats.Snapshots;
        net.Step(60);
        Assert.InRange(net.Server.Stats.Snapshots - start, 9, 11);
        Assert.Equal(10, client.Api.ServerTickRate);
        Assert.Throws<ArgumentOutOfRangeException>(() => net.Server.TickRate = 0);
    }
}
