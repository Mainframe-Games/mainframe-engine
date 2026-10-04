using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// Regressions from the M5 code review: hostile or failing clients, re-entrant despawn handlers, snapshot byte
/// budgets with per-node acknowledgements, stalled acknowledgements, malformed spawns, connect fallback and API
/// misuse.
/// </summary>
public sealed class ReplicationRobustnessTests
{
    [Fact]
    public void AnRpcThatThrowsOnTheServerIsContained()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);
        var failures = new List<(PeerId, string)>();
        net.Server.RpcFailed += (peer, _, rpc, e) => failures.Add((peer, $"{rpc.Name}:{e.Message}"));

        client.Find<NetBox>(box.NetworkId)!.RpcCrash(7);
        net.Step(3); // would throw out of SceneTree.Tick before the fix

        Assert.Equal((client.Api.LocalPeerId, "Crash:boom 7 on Box"), Assert.Single(failures));
        Assert.Equal(1, net.Server.Stats.RpcsFailed);
        Assert.True(client.Api.IsConnected);

        net.Server.KickOnRpcFailure = true;
        var reasons = new List<DisconnectReason>();
        client.Api.Disconnected += reasons.Add;
        client.Find<NetBox>(box.NetworkId)!.RpcCrash(8);
        net.Step(3);
        Assert.Equal([DisconnectReason.Kicked], reasons);
    }

    [Fact]
    public void DespawnHandlersMayFreeOtherNetworkedNodes()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var a = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        var b = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        var c = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);

        // Freeing one box frees the next from the despawn event, which frees the last.
        net.Server.NodeDespawned += node =>
        {
            if (ReferenceEquals(node, a))
                b.Free();
            else if (ReferenceEquals(node, b))
                c.Free();
        };
        a.QueueFree();
        net.Step(3);

        Assert.Equal(0, net.Server.Stats.NetworkedNodes);
        Assert.Equal(0, client.Api.Stats.NetworkedNodes);
        Assert.Empty(client.Tree.CurrentScene!.Children);
    }

    [Fact]
    public void ASnapshotBudgetSpreadsChangesOverTicksAndStillConverges()
    {
        using var net = new NetHarness(configure: api => api.MaxSnapshotBytes = 200);
        var client = net.Join();
        var boxes = new List<NetBox>();
        for (var i = 0; i < 40; i++)
            boxes.Add(net.Server.Spawn<NetBox>(NetHarness.BoxScene));
        net.Step(3);

        // Every box changes every frame: far more than 200 bytes per tick.
        var largest = 0;
        for (var frame = 0; frame < 120; frame++)
        {
            foreach (var box in boxes)
            {
                box.NetPosition += new Vector3(0.01f, 0, 0);
                box.Score++;
            }

            net.Step();
            largest = Math.Max(largest, net.Server.Stats.LastSnapshotBytes);
        }

        Assert.InRange(largest, 100, 200);
        net.Step(120); // changes stop: the remaining nodes go out over the next ticks
        foreach (var box in boxes)
        {
            var copy = client.Find<NetBox>(box.NetworkId)!;
            Assert.Equal(box.Score, copy.Score);
            Assert.Equal(box.Position, copy.Position);
        }

        // Acknowledgements kept progressing (no stall disconnect), and idle snapshots are empty again.
        Assert.True(client.Api.IsConnected);
        Assert.Equal(MessageHeader.Size + 2, net.Server.Stats.LastSnapshotBytes);
    }

    [Fact]
    public void BudgetedSnapshotsConvergeOverABadNetwork()
    {
        var conditions = new NetworkConditions { Loss = 0.2, Latency = 0.03, Jitter = 0.05, Duplication = 0.1 };
        using var net = new NetHarness(conditions, seed: 4, configure: api => api.MaxSnapshotBytes = 300);
        var client = net.Join();
        var boxes = new List<NetBox>();
        for (var i = 0; i < 30; i++)
            boxes.Add(net.Server.Spawn<NetBox>(NetHarness.BoxScene));
        var random = new Random(4);
        for (var frame = 0; frame < 200; frame++)
        {
            var box = boxes[random.Next(boxes.Count)];
            box.Score = random.Next(1000);
            box.NetPosition += new Vector3(0, 0.01f, 0);
            net.Step();
        }

        net.Step(180);
        foreach (var box in boxes)
        {
            var copy = client.Find<NetBox>(box.NetworkId)!;
            Assert.Equal(box.Score, copy.Score);
            Assert.Equal(box.Position, copy.Position);
        }
    }

    [Fact]
    public void AClientThatNeverAcknowledgesNewSnapshotsIsDisconnected()
    {
        using var net = new NetHarness(configure: api => api.PeerTimeout = 1f);
        // A raw client: completes the handshake, then keeps sending a stale acknowledgement (alive, but stuck).
        var registry = new MessageRegistry();
        using var registryOwner = new MultiplayerApi(new SceneTree(), registry);
        var endpoint = (LoopbackTransport)net.Server.Bus!.Transport;
        using var stuck = new MessageBus(endpoint.ConnectClient(registry.Fingerprint), registry);
        var reasons = new List<DisconnectReason>();
        stuck.PeerDisconnected += (_, reason) => reasons.Add(reason);
        var left = new List<DisconnectReason>();
        net.Server.PeerLeft += (_, reason) => left.Add(reason);

        stuck.Poll();
        stuck.Send(LoopbackTransport.RemotePeerId, new HelloMessage { ReplicationFingerprint = net.Server.ReplicationFingerprint });
        for (var frame = 0; frame < 120 && reasons.Count == 0; frame++)
        {
            net.Step();
            stuck.Poll();
            if (reasons.Count == 0)
                stuck.Send(LoopbackTransport.RemotePeerId, new AckMessage { Tick = 1 }, NetChannel.Unreliable);
        }

        Assert.Equal([DisconnectReason.Timeout], reasons);
        net.Step(2); // the server hears about the disconnect on its next poll
        Assert.Equal([DisconnectReason.Timeout], left);
    }

    private static byte[] SpawnPacket(uint netId, uint count, uint tick = 5)
    {
        using var writer = new NetBufferWriter();
        new MessageHeader(1, ReplicationMessageIds.Spawn, tick).Write(writer);
        writer.WriteVarUInt32(netId);
        writer.WriteVarUInt32(2); // the box scene
        writer.Write((byte)0);
        writer.Write("");
        writer.Write("Forged");
        writer.WriteVarUInt32(count);
        for (var i = 0; i < count; i++)
        {
            writer.Write(true);
            writer.Write(0UL);
            writer.WriteVarUInt64(0);
        }

        return writer.WrittenSpan.ToArray();
    }

    [Fact]
    public void MalformedSpawnsCannotCorruptTheNodeTable()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);
        var copy = client.Find<NetBox>(box.NetworkId)!;
        var dropped = new List<MessageDropReason>();
        client.Api.Bus!.MessageDropped += (_, reason) => dropped.Add(reason);
        ITransportListener listener = client.Api.Bus;

        listener.OnReceive(LoopbackTransport.RemotePeerId, NetChannel.Reliable, SpawnPacket(box.NetworkId, 1));          // id in use
        listener.OnReceive(LoopbackTransport.RemotePeerId, NetChannel.Reliable, SpawnPacket(uint.MaxValue, 2));          // overflows
        listener.OnReceive(LoopbackTransport.RemotePeerId, NetChannel.Reliable, SpawnPacket(0, 1));                      // id 0

        Assert.Equal([MessageDropReason.Malformed, MessageDropReason.Malformed, MessageDropReason.Malformed], dropped);
        Assert.Same(copy, client.Api.FindNode(box.NetworkId));
        Assert.True(copy.IsNetworked);
        Assert.Single(client.Tree.CurrentScene!.Children);

        // Still replicating normally.
        box.Score = 3;
        net.Step(3);
        Assert.Equal(3, copy.Score);
    }

    [Fact]
    public void SnapshotEntriesWithBadLengthsAreMalformed()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var dropped = new List<MessageDropReason>();
        client.Api.Bus!.MessageDropped += (_, reason) => dropped.Add(reason);
        using var writer = new NetBufferWriter();
        new MessageHeader(1, ReplicationMessageIds.Snapshot, net.Server.Tick + 100).Write(writer);
        writer.Write((byte)0);
        writer.WriteVarUInt32(999);
        writer.WriteVarUInt32(1_000_000); // longer than the packet
        ((ITransportListener)client.Api.Bus).OnReceive(LoopbackTransport.RemotePeerId, NetChannel.Unreliable, writer.WrittenSpan);
        Assert.Equal([MessageDropReason.Malformed], dropped);
    }

    /// <summary>A transport whose connection attempt fails on the first poll (like an unreachable ENet host).</summary>
    private sealed class UnreachableTransport : ITransport
    {
        private bool _reported;

        public bool IsServer => false;

        public void Poll(ITransportListener listener)
        {
            if (_reported)
                return;
            _reported = true;
            listener.OnPeerDisconnected(new PeerId(1), DisconnectReason.ConnectFailed);
        }

        public bool Send(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload) => false;

        public void Disconnect(PeerId peer, DisconnectReason reason)
        {
        }

        public void Flush()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class UnreachableFactory : ITransportFactory
    {
        public int Attempts { get; private set; }

        public string Scheme => "dead";

        public bool TryConnect(in NetworkAddress address, uint connectData, [NotNullWhen(true)] out ITransport? transport, out string? failure)
        {
            Attempts++;
            transport = new UnreachableTransport();
            failure = null;
            return true;
        }
    }

    [Fact]
    public void AFailedConnectionFallsBackToTheNextAddress()
    {
        using var net = new NetHarness();
        var dead = new UnreachableFactory();
        var loopback = new LoopbackTransportFactory();
        var selector = new TransportSelector();
        selector.Register(dead);
        selector.Register(loopback);
        loopback.Add("host", (LoopbackTransport)net.Server.Bus!.Transport);

        var tree = NetHarness.CreateTree();
        var api = net.CreateApi(tree);
        var reasons = new List<DisconnectReason>();
        api.Disconnected += reasons.Add;
        Assert.True(api.TryConnect("dead:a;dead:b;loopback:host", selector));
        var time = new GameTime { DeltaTime = NetHarness.FrameTime };
        for (var frame = 0; frame < 20 && !api.IsConnected; frame++)
        {
            net.ServerTree.Tick(time);
            tree.Tick(time);
        }

        Assert.True(api.IsConnected);
        Assert.Equal(2, dead.Attempts);
        Assert.Empty(reasons); // fallbacks are silent

        // Nothing left to try: one Disconnected with the last reason.
        api.Stop();
        Assert.True(api.TryConnect("dead:a;dead:b", selector));
        for (var frame = 0; frame < 5; frame++)
            tree.Tick(time);
        Assert.Equal([DisconnectReason.ConnectFailed], reasons);
        Assert.Equal(MultiplayerMode.Offline, api.Mode);
        tree.Shutdown();
        tree.Servers.Dispose();
    }

    [Fact]
    public void MisuseIsRejected()
    {
        using var net = new NetHarness();
        var client = net.Join();
        Assert.Throws<InvalidOperationException>(() => net.Server.TickRate = 20);
        net.Server.TickRate = MultiplayerApi.DefaultTickRate; // unchanged: fine
        Assert.Throws<ArgumentException>(() => net.Server.Spawn(NetHarness.BoxScene, authority: new PeerId(4242)));
        var box = net.Server.Spawn(NetHarness.BoxScene, authority: client.Api.LocalPeerId);
        Assert.Throws<ArgumentException>(() => net.Server.SetAuthority(box, new PeerId(4242)));

        // A kicked client stops receiving at once (it leaves ConnectedPeers before the disconnect completes).
        net.Server.Kick(client.Api.LocalPeerId);
        Assert.Empty(net.Server.ConnectedPeers.ToArray());
        var snapshots = net.Server.Stats.Snapshots;
        net.Step(4);
        Assert.Equal(snapshots, net.Server.Stats.Snapshots);
    }

    [Fact]
    public void SnapshotsFromAnythingButTheServerAreIgnored()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        net.Step(3);
        var tick = client.Api.Tick;
        using var writer = new NetBufferWriter();
        new MessageHeader(1, ReplicationMessageIds.Despawn, tick + 50).Write(writer);
        writer.WriteVarUInt32(box.NetworkId);
        ((ITransportListener)client.Api.Bus!).OnReceive(new PeerId(77), NetChannel.Reliable, writer.WrittenSpan); // not the server
        Assert.NotNull(client.Api.FindNode(box.NetworkId));
    }
}
