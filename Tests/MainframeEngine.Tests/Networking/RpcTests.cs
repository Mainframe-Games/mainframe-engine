using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>RPC routing (server → clients, client → server, targeted), authority checks, call-local and dispatch.</summary>
public sealed class RpcTests
{
    private static (NetHarness Net, NetHarness.Client Owner, NetHarness.Client Other, NetPlayer Player) Setup()
    {
        var net = new NetHarness();
        var owner = net.Join();
        var other = net.Join();
        var player = net.Server.Spawn<NetPlayer>(NetHarness.PlayerScene, authority: owner.Api.LocalPeerId);
        net.Step(3);
        return (net, owner, other, player);
    }

    [Fact]
    public void TheAuthorityCallsTheServer()
    {
        var (net, owner, _, player) = Setup();
        using var _ = net;
        var mine = owner.Find<NetPlayer>(player.NetworkId)!;

        mine.RpcMove(new Vector3(1, 0, 0));
        Assert.Empty(mine.Calls); // not CallLocal
        net.Step(2);

        var call = Assert.Single(player.Calls);
        Assert.Equal(nameof(NetPlayer.Move), call.Rpc);
        Assert.Equal(owner.Api.LocalPeerId, call.Sender);
        Assert.Equal(new Vector3(1, 0, 0), player.Position);
        Assert.Equal(1, owner.Api.Stats.RpcsSent);
        Assert.Equal(1, net.Server.Stats.RpcsReceived);

        // The server's resulting state replicates back to everyone.
        net.Step(5);
        Assert.Equal(new Vector3(1, 0, 0), mine.NetPosition);
    }

    [Fact]
    public void NonAuthorityClientsAreRefusedLocallyAndForgedCallsAreRejectedByTheServer()
    {
        var (net, _, other, player) = Setup();
        using var _ = net;
        var theirs = other.Find<NetPlayer>(player.NetworkId)!;
        var rejected = new List<(PeerId, Node, RpcInfo)>();
        net.Server.RpcRejected += (peer, node, rpc) => rejected.Add((peer, node, rpc));

        theirs.RpcMove(Vector3.One);
        Assert.Equal(1, other.Api.Stats.RpcsRejected);
        Assert.Equal(0, other.Api.Stats.RpcsSent);

        // A modified client could still send it: the server checks the sender against the authority.
        var move = NetPlayerInfo.Rpc(nameof(NetPlayer.Move));
        using var args = new NetBufferWriter();
        args.Write(Vector3.One);
        other.Api.Bus!.Send(LoopbackTransport.RemotePeerId, new RpcMessage { NetId = player.NetworkId, WireIndex = move.WireIndex, Arguments = args });
        net.Step(2);

        Assert.Empty(player.Calls);
        Assert.Equal(Vector3.Zero, player.Position);
        var (peer, node, rpc) = Assert.Single(rejected);
        Assert.Equal(other.Api.LocalPeerId, peer);
        Assert.Same(player, node);
        Assert.Same(move, rpc);
        Assert.Equal(1, net.Server.Stats.RpcsRejected);
    }

    [Fact]
    public void ServerOnlyRpcsGoToEveryClientAndClientsCannotCallThem()
    {
        var (net, owner, other, player) = Setup();
        using var _ = net;
        var mine = owner.Find<NetPlayer>(player.NetworkId)!;
        var theirs = other.Find<NetPlayer>(player.NetworkId)!;

        player.RpcFlash(3, NetTeam.Red);
        net.Step(2);
        Assert.Equal(("Flash", MultiplayerApi.ServerPeerId, "3:Red"), Assert.Single(mine.Calls));
        Assert.Equal(("Flash", MultiplayerApi.ServerPeerId, "3:Red"), Assert.Single(theirs.Calls));
        Assert.Empty(player.Calls);

        // Even the authority cannot call a server-only RPC.
        mine.RpcFlash(1, NetTeam.Blue);
        Assert.Equal(1, owner.Api.Stats.RpcsRejected);

        // A forged one is rejected by the server.
        var flash = NetPlayerInfo.Rpc(nameof(NetPlayer.Flash));
        using var args = new NetBufferWriter();
        args.Write(1);
        args.Write((byte)NetTeam.Blue);
        owner.Api.Bus!.Send(LoopbackTransport.RemotePeerId, new RpcMessage { NetId = player.NetworkId, WireIndex = flash.WireIndex, Arguments = args });
        net.Step(2);
        Assert.Empty(player.Calls);
        Assert.Equal(1, net.Server.Stats.RpcsRejected);
    }

    [Fact]
    public void TargetedCallsReachOnePeer()
    {
        var (net, owner, other, player) = Setup();
        using var _ = net;
        var mine = owner.Find<NetPlayer>(player.NetworkId)!;
        var theirs = other.Find<NetPlayer>(player.NetworkId)!;

        player.RpcFlashTo(other.Api.LocalPeerId, 2, NetTeam.Blue);
        net.Step(2);
        Assert.Empty(mine.Calls);
        Assert.Single(theirs.Calls);

        // A client may only target the server.
        mine.RpcMoveTo(other.Api.LocalPeerId, Vector3.One);
        Assert.Equal(1, owner.Api.Stats.RpcsRejected);
        mine.RpcMoveTo(MultiplayerApi.ServerPeerId, Vector3.One);
        net.Step(2);
        Assert.Single(player.Calls);

        // Targeting yourself runs it here.
        player.RpcFlashTo(MultiplayerApi.ServerPeerId, 9, NetTeam.None);
        Assert.Equal(2, player.Calls.Count);
        Assert.Equal(("Flash", MultiplayerApi.ServerPeerId, "9:None"), player.Calls[^1]);
    }

    [Fact]
    public void AnyPeerRpcsTravelUnreliablyFromAnyClient()
    {
        var (net, _, other, player) = Setup();
        using var _ = net;
        other.Find<NetPlayer>(player.NetworkId)!.RpcPing(77);
        net.Step(2);
        Assert.Equal(("Ping", other.Api.LocalPeerId, "77"), Assert.Single(player.Calls));
        Assert.False(NetPlayerInfo.Rpc(nameof(NetPlayer.Ping)).Reliable);
    }

    [Fact]
    public void CallLocalRunsOnTheCallerWithItsOwnIdAsSender()
    {
        var (net, owner, other, player) = Setup();
        using var _ = net;
        var mine = owner.Find<NetPlayer>(player.NetworkId)!;
        mine.RpcEmote("wave", new NetLoadout { Weapon = 3 });
        Assert.Equal(("Emote", owner.Api.LocalPeerId, "wave:3"), Assert.Single(mine.Calls));
        net.Step(2);
        Assert.Equal(("Emote", owner.Api.LocalPeerId, "wave:3"), Assert.Single(player.Calls));

        // From the server: every client and the server itself.
        player.RpcEmote("bow", default);
        Assert.Equal(("Emote", MultiplayerApi.ServerPeerId, "bow:0"), player.Calls[^1]);
        net.Step(2);
        Assert.Equal(("Emote", MultiplayerApi.ServerPeerId, "bow:0"), other.Find<NetPlayer>(player.NetworkId)!.Calls[^1]);
        Assert.Equal(MultiplayerApi.ServerPeerId, net.Server.RemoteSender); // restored after the call
    }

    [Fact]
    public void RpcsOnNodesThatAreNotNetworkedOnlyRunCallLocalMethods()
    {
        var player = new NetPlayer();
        player.RpcMove(Vector3.One);  // nowhere to send
        player.RpcEmote("solo", default);
        Assert.Equal(["Emote"], player.Calls.Select(c => c.Rpc));
        Assert.Equal(Vector3.Zero, player.Position);
        player.Free();
    }

    [Fact]
    public void DerivedTypesNumberTheirRpcsAfterTheBase()
    {
        var baseInfo = ReplicationRegistry.Get(typeof(NetPlayer))!;
        var bossInfo = ReplicationRegistry.Get(typeof(NetBoss))!;
        Assert.Equal(["Move", "Flash", "Ping", "Emote"], baseInfo.Rpcs.Select(r => r.Name));
        Assert.Equal([0, 1, 2, 3], baseInfo.Rpcs.Select(r => r.WireIndex));
        Assert.Equal(4, bossInfo.Rpcs[0].WireIndex);
        Assert.Same(baseInfo, bossInfo.Base);
        Assert.Equal(["Rage"], bossInfo.Properties.Select(p => p.Name));
        Assert.Equal(RpcMode.Server, bossInfo.Rpcs[0].Mode);

        using var net = new NetHarness();
        var client = net.Join();
        var boss = net.Server.Spawn<NetBoss>(NetHarness.BossScene);
        net.Step(3);
        boss.RpcRoar(0.5f);
        boss.RpcFlash(1, NetTeam.Red); // inherited
        net.Step(2);
        Assert.Equal(["Roar", "Flash"], client.Find<NetBoss>(boss.NetworkId)!.Calls.Select(c => c.Rpc));
    }

    [Fact]
    public void RpcsOnANodeSpawnedThisFrameArriveAfterItsSpawn()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var player = net.Server.Spawn<NetPlayer>(NetHarness.PlayerScene);
        player.RpcFlash(5, NetTeam.Blue);
        net.Step(2);
        Assert.Single(client.Find<NetPlayer>(player.NetworkId)!.Calls);
    }

    [Fact]
    public void RpcsForUnknownNodesAreIgnoredAndBadIndicesAreMalformed()
    {
        var (net, owner, _, player) = Setup();
        using var _ = net;
        var dropped = new List<MessageDropReason>();
        net.Server.Bus!.MessageDropped += (_, reason) => dropped.Add(reason);
        using var args = new NetBufferWriter();
        owner.Api.Bus!.Send(LoopbackTransport.RemotePeerId, new RpcMessage { NetId = 9999, WireIndex = 0, Arguments = args });
        owner.Api.Bus!.Send(LoopbackTransport.RemotePeerId, new RpcMessage { NetId = player.NetworkId, WireIndex = 99, Arguments = args });
        net.Step(2);
        Assert.Equal([MessageDropReason.Malformed], dropped);
        Assert.Empty(player.Calls);
    }

    private static class NetPlayerInfo
    {
        public static RpcInfo Rpc(string name) => ReplicationRegistry.Get(typeof(NetPlayer))!.Rpcs.Single(r => r.Name == name);
    }
}
