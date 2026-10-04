using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>Multi-client loopback, the simulated bad network, and replication surviving loss, reordering and duplication.</summary>
public sealed class TransportTests
{
    private sealed class Recorder : ITransportListener
    {
        public readonly List<string> Events = [];
        public readonly List<(PeerId Peer, NetChannel Channel, byte Value)> Packets = [];

        public void OnPeerConnected(PeerId peer, uint connectData) => Events.Add($"connect {(ulong)peer} {connectData}");

        public void OnPeerDisconnected(PeerId peer, DisconnectReason reason) => Events.Add($"disconnect {(ulong)peer} {reason}");

        public void OnReceive(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload) => Packets.Add((peer, channel, payload[0]));
    }

    [Fact]
    public void ALoopbackServerLinksSeveralClients()
    {
        var server = LoopbackTransport.CreateServer();
        var a = server.ConnectClient(7);
        var b = server.ConnectClient(8);
        var serverEvents = new Recorder();
        var aEvents = new Recorder();
        var bEvents = new Recorder();
        server.Poll(serverEvents);
        a.Poll(aEvents);
        b.Poll(bEvents);
        Assert.Equal(["connect 1 7", "connect 2 8"], serverEvents.Events);
        Assert.Equal(["connect 1 0"], aEvents.Events);
        Assert.Equal(2, server.ClientCount);

        Assert.True(server.Send(new PeerId(2), NetChannel.Reliable, [42]));
        Assert.True(a.Send(LoopbackTransport.RemotePeerId, NetChannel.Unreliable, [1]));
        Assert.True(b.Send(LoopbackTransport.RemotePeerId, NetChannel.Reliable, [2]));
        Assert.False(server.Send(new PeerId(3), NetChannel.Reliable, [0]));
        Assert.False(a.Send(new PeerId(2), NetChannel.Reliable, [0]));
        server.Poll(serverEvents);
        a.Poll(aEvents);
        b.Poll(bEvents);
        Assert.Equal([(new PeerId(1), NetChannel.Unreliable, (byte)1), (new PeerId(2), NetChannel.Reliable, (byte)2)], serverEvents.Packets);
        Assert.Empty(aEvents.Packets);
        Assert.Equal((new PeerId(1), NetChannel.Reliable, (byte)42), Assert.Single(bEvents.Packets));

        server.Disconnect(new PeerId(1), DisconnectReason.Kicked);
        server.Poll(serverEvents);
        a.Poll(aEvents);
        Assert.Equal("disconnect 1 Kicked", serverEvents.Events[^1]);
        Assert.Equal("disconnect 1 Kicked", aEvents.Events[^1]);
        Assert.False(a.IsLinked);
        Assert.True(server.IsLinked);

        var c = server.ConnectClient();
        server.Poll(serverEvents);
        Assert.Equal("connect 3 0", serverEvents.Events[^1]); // ids are never reused

        server.Dispose();
        b.Poll(bEvents);
        c.Poll(new Recorder());
        Assert.Equal("disconnect 1 Shutdown", bEvents.Events[^1]);
        Assert.Throws<InvalidOperationException>(() => a.ConnectClient());
        Assert.Throws<ObjectDisposedException>(() => server.ConnectClient());
        a.Dispose();
        b.Dispose();
        c.Dispose();
    }

    [Fact]
    public void SimulatedLatencyHoldsPacketsUntilDue()
    {
        var now = 0.0;
        var (serverEnd, clientEnd) = LoopbackTransport.CreatePair();
        using var client = new SimulatedTransport(clientEnd, new NetworkConditions { Latency = 0.1 }, clock: () => now);
        var events = new Recorder();
        serverEnd.Poll(events);
        Assert.False(client.Send(LoopbackTransport.RemotePeerId, NetChannel.Reliable, [0])); // not connected yet
        client.Poll(new Recorder());

        client.Send(LoopbackTransport.RemotePeerId, NetChannel.Reliable, [1]);
        client.Flush();
        serverEnd.Poll(events);
        Assert.Empty(events.Packets);
        Assert.Equal(1, client.InFlight);

        now = 0.11;
        client.Flush();
        serverEnd.Poll(events);
        Assert.Equal((byte)1, Assert.Single(events.Packets).Value);
        Assert.Equal(1, client.Delivered);
        Assert.False(client.IsServer);
        Assert.Same(clientEnd, client.Inner);
        serverEnd.Dispose();
    }

    [Fact]
    public void SimulatedLossAndDuplicationHitOnlyUnreliablePacketsAndAreSeeded()
    {
        static (long Dropped, long Duplicated, List<byte> Received) Run(int seed)
        {
            var (serverEnd, clientEnd) = LoopbackTransport.CreatePair();
            using var client = new SimulatedTransport(clientEnd, new NetworkConditions { Loss = 0.3, Duplication = 0.2 }, seed, () => 0);
            var events = new Recorder();
            client.Poll(new Recorder());
            for (var i = 0; i < 200; i++)
                client.Send(LoopbackTransport.RemotePeerId, NetChannel.Unreliable, [(byte)i]);
            for (var i = 0; i < 50; i++)
                client.Send(LoopbackTransport.RemotePeerId, NetChannel.Reliable, [(byte)i]);
            client.Flush();
            serverEnd.Poll(events);
            var reliable = events.Packets.Where(p => p.Channel == NetChannel.Reliable).Select(p => p.Value).ToList();
            Assert.Equal(Enumerable.Range(0, 50).Select(i => (byte)i), reliable); // all, once, in order
            var result = (client.Dropped, client.Duplicated, events.Packets.Where(p => p.Channel == NetChannel.Unreliable).Select(p => p.Value).ToList());
            serverEnd.Dispose();
            return result;
        }

        var first = Run(7);
        var again = Run(7);
        var other = Run(8);
        Assert.InRange(first.Dropped, 35, 85);
        Assert.InRange(first.Duplicated, 10, 50);
        Assert.Equal(200 - first.Dropped + first.Duplicated, first.Received.Count);
        Assert.Equal(first.Received, again.Received);
        Assert.NotEqual(first.Received, other.Received);
    }

    [Fact]
    public void JitterReordersUnreliablePacketsButNotReliableOnes()
    {
        var now = 0.0;
        var (serverEnd, clientEnd) = LoopbackTransport.CreatePair();
        using var client = new SimulatedTransport(clientEnd, new NetworkConditions { Latency = 0.05, Jitter = 0.1 }, seed: 3, clock: () => now);
        var events = new Recorder();
        client.Poll(new Recorder());
        for (var i = 0; i < 100; i++)
        {
            client.Send(LoopbackTransport.RemotePeerId, NetChannel.Unreliable, [(byte)i]);
            client.Send(LoopbackTransport.RemotePeerId, NetChannel.Reliable, [(byte)i]);
            now += 0.005;
            client.Flush();
            serverEnd.Poll(events);
        }

        now += 1;
        client.Flush();
        serverEnd.Poll(events);
        var unreliable = events.Packets.Where(p => p.Channel == NetChannel.Unreliable).Select(p => (int)p.Value).ToList();
        var reliable = events.Packets.Where(p => p.Channel == NetChannel.Reliable).Select(p => (int)p.Value).ToList();
        Assert.Equal(100, unreliable.Count);
        Assert.NotEqual(Enumerable.Range(0, 100), unreliable);       // reordered
        Assert.Equal(Enumerable.Range(0, 100), unreliable.Order());  // but all there
        Assert.Equal(Enumerable.Range(0, 100), reliable);             // in order
        serverEnd.Dispose();
    }

    [Fact]
    public void DisconnectingDeliversQueuedReliablePacketsFirst()
    {
        var now = 0.0;
        var server = LoopbackTransport.CreateServer();
        using var simulated = new SimulatedTransport(server, new NetworkConditions { Latency = 1 }, clock: () => now);
        var client = server.ConnectClient();
        simulated.Poll(new Recorder());
        var clientPeer = new PeerId(1);

        simulated.Send(clientPeer, NetChannel.Reliable, [1]);
        simulated.Send(clientPeer, NetChannel.Unreliable, [2]);
        simulated.Send(clientPeer, NetChannel.Reliable, [3]);
        Assert.Equal(3, simulated.InFlight);
        simulated.Disconnect(clientPeer, DisconnectReason.Kicked);
        Assert.Equal(0, simulated.InFlight);
        Assert.False(simulated.Send(clientPeer, NetChannel.Reliable, [4])); // gone

        var events = new Recorder();
        client.Poll(events);
        Assert.Equal([(byte)1, (byte)3], events.Packets.Select(p => p.Value));
        Assert.Equal("disconnect 1 Kicked", events.Events[^1]);
        client.Dispose();
    }

    [Theory]
    [InlineData(0.3, 0.0, 0.0, 1)]
    [InlineData(0.2, 0.03, 0.06, 2)]
    [InlineData(0.1, 0.02, 0.05, 3)]
    public void ReplicationConvergesOverABadNetwork(double loss, double latency, double jitter, int seed)
    {
        var conditions = new NetworkConditions { Loss = loss, Latency = latency, Jitter = jitter, Duplication = 0.1 };
        using var net = new NetHarness(conditions, seed);
        var a = net.Join();
        var boxes = new List<NetBox>();
        for (var i = 0; i < 20; i++)
            boxes.Add(net.Server.Spawn<NetBox>(NetHarness.BoxScene));
        var b = net.Join(); // joins mid-way

        var random = new Random(seed);
        for (var frame = 0; frame < 300; frame++)
        {
            var box = boxes[random.Next(boxes.Count)];
            box.Score = random.Next(1000);
            box.NetPosition += new Vector3(0.01f, 0, 0);
            if (frame == 150)
                boxes[0].QueueFree(); // reliable despawn under loss
            net.Step();
        }

        net.Step(120); // settle: changes stop, snapshots keep repeating until acknowledged
        foreach (var client in new[] { a, b })
        {
            Assert.Null(client.Api.FindNode(boxes[0].NetworkId));
            foreach (var box in boxes.Skip(1))
            {
                var copy = client.Find<NetBox>(box.NetworkId);
                Assert.NotNull(copy);
                Assert.Equal(box.Score, copy.Score);
                Assert.Equal(box.Position, copy.Position);
            }
        }

        if (loss > 0 || jitter > 0)
            Assert.True(a.Api.Stats.SnapshotsDiscarded + ((SimulatedTransport)net.Server.Bus!.Transport).Dropped > 0);
    }
}
