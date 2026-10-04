using System.Numerics;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

public sealed class MessageHeaderTests
{
    [Fact]
    public void HeaderRoundTripsInSevenLittleEndianBytes()
    {
        var header = new MessageHeader(3, 0x1234, 0xAABBCCDD);
        Span<byte> bytes = stackalloc byte[MessageHeader.Size];

        Assert.True(header.TryWrite(bytes));
        Assert.Equal(new byte[] { 3, 0x34, 0x12, 0xDD, 0xCC, 0xBB, 0xAA }, bytes.ToArray());
        Assert.True(MessageHeader.TryRead(bytes, out var read));
        Assert.Equal(header, read);
    }

    [Fact]
    public void ShortBuffersAreRejected()
    {
        Span<byte> bytes = stackalloc byte[MessageHeader.Size - 1];

        Assert.False(new MessageHeader(1, 1, 1).TryWrite(bytes));
        Assert.False(MessageHeader.TryRead(bytes, out _));
    }

    [Fact]
    public void WriterAppendsTheHeader()
    {
        using var writer = new NetBufferWriter(4);
        writer.Write((byte)9);
        new MessageHeader(1, 2, 3).Write(writer);

        Assert.Equal(1 + MessageHeader.Size, writer.Length);
        Assert.True(MessageHeader.TryRead(writer.WrittenSpan[1..], out var header));
        Assert.Equal(new MessageHeader(1, 2, 3), header);
    }
}

public sealed class MessageRegistryTests
{
    [Fact]
    public void IdsFollowRegistrationOrder()
    {
        var registry = TestRegistry.Create();

        Assert.Equal(0, registry.GetId<ChatMessage>());
        Assert.Equal(1, registry.GetId<TransformMessage>());
        Assert.Equal(typeof(TransformMessage), registry.GetMessageType(1));
        Assert.Null(registry.GetMessageType(7));
        Assert.False(registry.TryGetId<PingMessage>(out _));
        Assert.Equal(2, registry.Count);
    }

    [Fact]
    public void ExplicitIdsAreHonouredAndSequentialIdsSkipThem()
    {
        var registry = new MessageRegistry();
        Assert.Equal(0, registry.Register<PingMessage>(0));
        Assert.Equal(5, registry.Register<TransformMessage>(5));
        Assert.Equal(1, registry.Register<ChatMessage>());
    }

    [Fact]
    public void DuplicatesAndUnknownTypesThrow()
    {
        var registry = TestRegistry.Create();

        Assert.Throws<InvalidOperationException>(() => registry.Register<ChatMessage>());
        Assert.Throws<InvalidOperationException>(() => registry.Register<PingMessage>(1));
        Assert.Throws<InvalidOperationException>(() => registry.GetId<PingMessage>());
        Assert.Throws<ArgumentOutOfRangeException>(() => registry.Register<PingMessage>(MessageRegistry.MaxMessages));
    }

    [Fact]
    public void FingerprintFreezesAndDependsOnVersionAndTypes()
    {
        var a = TestRegistry.Create();
        var b = TestRegistry.Create();
        var otherVersion = TestRegistry.Create(protocolVersion: 2);
        var otherTypes = TestRegistry.Create();
        otherTypes.Register<PingMessage>();

        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(a.Fingerprint, otherVersion.Fingerprint);
        Assert.NotEqual(a.Fingerprint, otherTypes.Fingerprint);
        Assert.True(a.IsFrozen);
        Assert.Throws<InvalidOperationException>(() => a.Register<PingMessage>());
    }
}

public sealed class MessageBusTests
{
    private sealed class Pair : IDisposable
    {
        public readonly MessageBus Server;
        public readonly MessageBus Client;
        public readonly BusRecorder ServerEvents;
        public readonly BusRecorder ClientEvents;

        public Pair(MessageRegistry? clientRegistry = null, uint? connectData = null)
        {
            var registry = TestRegistry.Create();
            clientRegistry ??= registry;
            var (server, client) = LoopbackTransport.CreatePair(connectData ?? clientRegistry.Fingerprint);
            Server       = new MessageBus(server, registry);
            Client       = new MessageBus(client, clientRegistry);
            ServerEvents = new BusRecorder(Server);
            ClientEvents = new BusRecorder(Client);
        }

        public void Pump(int times = 2)
        {
            for (var i = 0; i < times; i++)
            {
                Server.Poll();
                Client.Poll();
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }

    [Fact]
    public void PeersConnectAndExchangeReliableAndUnreliableMessages()
    {
        using var pair = new Pair();
        pair.Pump();

        var client = Assert.Single(pair.ServerEvents.Connected);
        var server = Assert.Single(pair.ClientEvents.Connected);
        Assert.Equal(client, pair.Server.Peers[0]);

        pair.Client.Tick = 42;
        Assert.True(pair.Client.Send(server, new ChatMessage { Sequence = 1, Text = "hello" }));
        var transform = new TransformMessage { NodeId = 7, Position = new Vector3(1, 2, 3), Rotation = Quaternion.Identity };
        Assert.True(pair.Server.Send(client, transform, NetChannel.Unreliable));
        pair.Pump();

        var (chatContext, chat) = Assert.Single(pair.ServerEvents.Chats);
        Assert.Equal("hello", chat.Text);
        Assert.Equal(client, chatContext.Sender);
        Assert.Equal(NetChannel.Reliable, chatContext.Channel);
        Assert.Equal(42u, chatContext.Header.Tick);
        Assert.Equal(0, chatContext.Header.MessageId);

        var (transformContext, received) = Assert.Single(pair.ClientEvents.Transforms);
        Assert.Equal(NetChannel.Unreliable, transformContext.Channel);
        Assert.Equal(transform.Position, received.Position);
        Assert.Equal(transform.Rotation, received.Rotation);

        Assert.Equal(1, pair.Client.Stats.MessagesSent);
        Assert.Equal(1, pair.Server.Stats.MessagesReceived);
        Assert.Equal(MessageHeader.Size + 4 + 1 + 5, pair.Server.Stats.BytesReceived);
    }

    [Fact]
    public void BroadcastReachesEveryPeerExceptTheExcludedOne()
    {
        using var pair = new Pair();
        pair.Pump();
        var client = pair.Server.Peers[0];

        Assert.Equal(1, pair.Server.Broadcast(new ChatMessage { Text = "all" }));
        Assert.Equal(0, pair.Server.Broadcast(new ChatMessage { Text = "none" }, NetChannel.Reliable, except: client));
        pair.Pump();

        Assert.Equal("all", Assert.Single(pair.ClientEvents.Chats).Message.Text);
    }

    [Fact]
    public void UnsubscribedHandlersStopReceiving()
    {
        using var pair = new Pair();
        pair.Pump();
        var count = 0;
        MessageHandler<ChatMessage> handler = (in MessageContext _, in ChatMessage _) => count++;
        pair.Client.Subscribe(handler);

        pair.Server.Broadcast(new ChatMessage { Text = "1" });
        pair.Pump();
        pair.Client.Unsubscribe(handler);
        pair.Server.Broadcast(new ChatMessage { Text = "2" });
        pair.Pump();

        Assert.Equal(1, count);
        Assert.Equal(2, pair.ClientEvents.Chats.Count);
    }

    [Fact]
    public void SendingToAnUnknownPeerReportsInsteadOfThrowing()
    {
        using var pair = new Pair();
        pair.Pump();

        Assert.False(pair.Server.Send(new PeerId(999), new ChatMessage { Text = "x" }));

        var failure = Assert.Single(pair.ServerEvents.SendFailures);
        Assert.Equal(new PeerId(999), failure.Peer);
        Assert.Equal(1, pair.Server.Stats.SendFailures);
    }

    [Fact]
    public void UnregisteredMessagesCannotBeSentOrSubscribed()
    {
        using var pair = new Pair();
        pair.Pump();

        Assert.Throws<InvalidOperationException>(() => pair.Server.Broadcast(new PingMessage()));
        Assert.Throws<InvalidOperationException>(() => pair.Server.Subscribe((in MessageContext _, in PingMessage _) => { }));
    }

    [Fact]
    public void BadPacketsAreDroppedWithAReason()
    {
        var (serverTransport, clientTransport) = LoopbackTransport.CreatePair(TestRegistry.Create().Fingerprint);
        using var server = new MessageBus(serverTransport, TestRegistry.Create());
        var events = new BusRecorder(server);
        server.Poll();
        clientTransport.Poll(new NullListener());
        var peer = LoopbackTransport.RemotePeerId;

        Span<byte> packet = stackalloc byte[MessageHeader.Size + 2];
        clientTransport.Send(peer, NetChannel.Reliable, packet[..3]);                                   // truncated
        new MessageHeader(9, 0, 0).TryWrite(packet);
        clientTransport.Send(peer, NetChannel.Reliable, packet[..MessageHeader.Size]);                  // wrong version
        new MessageHeader(1, 77, 0).TryWrite(packet);
        clientTransport.Send(peer, NetChannel.Reliable, packet[..MessageHeader.Size]);                  // unknown id
        new MessageHeader(1, 0, 0).TryWrite(packet);
        clientTransport.Send(peer, NetChannel.Reliable, packet);                                        // chat cut short
        server.Poll();

        Assert.Equal(
            [MessageDropReason.Truncated, MessageDropReason.ProtocolMismatch, MessageDropReason.UnknownMessage, MessageDropReason.Malformed],
            events.Dropped.Select(d => d.Reason));
        Assert.Empty(events.Chats);
        Assert.Equal(4, server.Stats.MessagesDropped);
        Assert.Equal(0, server.Stats.MessagesReceived);
        clientTransport.Dispose();
    }

    [Fact]
    public void ServerRefusesAMismatchedRegistry()
    {
        var clientRegistry = TestRegistry.Create(protocolVersion: 2);
        using var pair = new Pair(clientRegistry);
        pair.Pump(3);

        Assert.Empty(pair.ServerEvents.Connected);
        Assert.Empty(pair.Server.Peers.ToArray());
        Assert.Empty(pair.ServerEvents.Disconnected);
        var (_, reason) = Assert.Single(pair.ClientEvents.Disconnected);
        Assert.Equal(DisconnectReason.ProtocolMismatch, reason);
        Assert.Empty(pair.Client.Peers.ToArray());
    }

    [Fact]
    public void DisconnectRemovesThePeerOnBothSides()
    {
        using var pair = new Pair();
        pair.Pump();
        var client = pair.Server.Peers[0];

        pair.Server.Disconnect(client);
        pair.Pump();

        Assert.Empty(pair.Server.Peers.ToArray());
        Assert.Empty(pair.Client.Peers.ToArray());
        Assert.Equal((client, DisconnectReason.Kicked), Assert.Single(pair.ServerEvents.Disconnected));
        Assert.Equal(DisconnectReason.Kicked, Assert.Single(pair.ClientEvents.Disconnected).Reason);
        Assert.False(pair.Server.Send(client, new ChatMessage { Text = "gone" }));
    }

    [Fact]
    public void DisposingOneSideTellsTheOther()
    {
        using var pair = new Pair();
        pair.Pump();

        pair.Client.Dispose();
        pair.Server.Poll();

        Assert.Equal(DisconnectReason.Shutdown, Assert.Single(pair.ServerEvents.Disconnected).Reason);
        Assert.Throws<ObjectDisposedException>(() => pair.Client.Poll());
    }

    [Fact]
    public void SteadyStateSendReceiveDispatchDoesNotAllocate()
    {
        using var pair = new Pair();
        pair.Pump();
        var client  = pair.Server.Peers[0];
        var server  = pair.Client.Peers[0];
        var counter = 0;
        pair.Client.Subscribe((in MessageContext _, in TransformMessage _) => counter++);
        pair.Server.Subscribe((in MessageContext _, in TransformMessage _) => counter++);
        var message = new TransformMessage { NodeId = 1, Position = Vector3.One, Rotation = Quaternion.Identity };

        void Round()
        {
            pair.Server.Send(client, message, NetChannel.Unreliable);
            pair.Client.Send(server, message, NetChannel.Reliable);
            pair.Server.Poll();
            pair.Client.Poll();
        }

        // The recorder lists grow while warming up; give them headroom first.
        pair.ServerEvents.Transforms.Capacity = 4096;
        pair.ClientEvents.Transforms.Capacity = 4096;
        for (var i = 0; i < 200; i++)
            Round();

        var windows   = 0;
        var allocated = AllocationGate.SmallestWindow(() =>
        {
            windows++;
            for (var i = 0; i < 1000; i++)
                Round();
        });

        Assert.Equal(0, allocated);
        Assert.Equal(400 + 2000 * windows, counter);
    }

    // Regression (flake ~1 in 15 full runs): the loopback transport's packet copies came from ArrayPool<byte>.Shared,
    // whose per-core partitions every thread shares; a test on another thread renting the same size class took the
    // arrays this thread returned, so its next rent allocated. The transports now use a private pool (PacketPool).
    [Fact]
    public void SteadyStateDoesNotAllocateWhileAnotherThreadUsesTheSharedArrayPool()
    {
        using var pair = new Pair();
        pair.Pump();
        var client = pair.Server.Peers[0];
        var server = pair.Client.Peers[0];
        var message = new TransformMessage { NodeId = 1, Position = Vector3.One, Rotation = Quaternion.Identity };
        pair.ServerEvents.Transforms.Capacity = 100_000; // warm-up + AllocationGate.MaxWindows windows
        pair.ClientEvents.Transforms.Capacity = 100_000;

        void Round()
        {
            pair.Server.Send(client, message, NetChannel.Unreliable);
            pair.Client.Send(server, message, NetChannel.Reliable);
            pair.Server.Poll();
            pair.Client.Poll();
        }

        for (var i = 0; i < 200; i++)
            Round();

        using var stop = new CancellationTokenSource();
        var competitor = new Thread(() =>
        {
            var held = new byte[8][];
            while (!stop.IsCancellationRequested)
            {
                for (var k = 0; k < held.Length; k++)
                    held[k] = System.Buffers.ArrayPool<byte>.Shared.Rent(64);
                Thread.SpinWait(50);
                for (var k = 0; k < held.Length; k++)
                    System.Buffers.ArrayPool<byte>.Shared.Return(held[k]);
            }
        })
        { IsBackground = true };
        competitor.Start();
        long allocated;
        try
        {
            allocated = AllocationGate.SmallestWindow(() =>
            {
                for (var i = 0; i < 30_000; i++)
                    Round();
            });
        }
        finally
        {
            stop.Cancel();
            competitor.Join();
        }

        Assert.Equal(0, allocated);
    }

    private sealed class NullListener : ITransportListener
    {
        public void OnPeerConnected(PeerId peer, uint connectData)
        {
        }

        public void OnPeerDisconnected(PeerId peer, DisconnectReason reason)
        {
        }

        public void OnReceive(PeerId peer, NetChannel channel, ReadOnlySpan<byte> payload)
        {
        }
    }
}
