using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

[Collection(nameof(SerialEnet))]
public sealed class NetworkNodeTests
{
    [Fact]
    public void ConstructingANodeDoesNotTouchTheNetwork()
    {
        var before = EnetLibrary.References;
        using var node = new NetworkNode();

        Assert.Equal(before, EnetLibrary.References);
        Assert.Null(node.Server);
        Assert.Null(node.Client);
        Assert.False(node.Messages.IsFrozen);
    }

    [Fact]
    public void ListenServerTalksToItsOwnClient()
    {
        EnetTransportTests.SkipUnlessNativePresent();
        using var node = new NetworkNode();
        node.Messages.Register<ChatMessage>();
        var port = EnetTransportTests.FreeUdpPort();

        var server = node.StartServer(port, 2);
        var client = node.StartClient("127.0.0.1", port);
        var serverEvents = new BusRecorder2(server);
        var clientEvents = new BusRecorder2(client);
        Pump(node, () => serverEvents.Connected == 1 && clientEvents.Connected == 1);

        server.Broadcast(new ChatMessage { Text = "welcome" });
        Pump(node, () => clientEvents.Chats.Count == 1);
        Assert.Equal("welcome", clientEvents.Chats[0]);

        // Restarting replaces (and disposes) the old server instead of leaking it.
        var restarted = node.StartServer(EnetTransportTests.FreeUdpPort(), 2);
        Assert.NotSame(server, restarted);
        Assert.Throws<ObjectDisposedException>(() => server.Poll());
        Pump(node, () => clientEvents.Disconnected == 1);

        node.StopClient();
        node.StopServer();
        Assert.Null(node.Server);
        Assert.Null(node.Client);
    }

    [Fact]
    public void LocalIpIsLazyAndNeverThrows()
    {
        Assert.False(string.IsNullOrEmpty(NetworkNode.LocalIp));
        Assert.True(System.Net.IPAddress.TryParse(NetworkNode.LocalIp, out _));
    }

    private static void Pump(NetworkNode node, Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Condition not met in time.");
            node.OnUpdate(default);
            Thread.Sleep(1);
        }
    }

    // ChatMessage-only recorder (this registry has no TransformMessage).
    private sealed class BusRecorder2
    {
        public int Connected;
        public int Disconnected;
        public readonly List<string> Chats = [];

        public BusRecorder2(MessageBus bus)
        {
            bus.PeerConnected    += _ => Connected++;
            bus.PeerDisconnected += (_, _) => Disconnected++;
            bus.Subscribe((in MessageContext _, in ChatMessage message) => Chats.Add(message.Text));
        }
    }
}

public sealed class NetworkUtilsTests
{
    [Fact]
    public void LocalIpv4LookupDoesNotThrow()
    {
        var found = NetworkUtils.TryGetPrimaryLocalIPv4(out var address);

        Assert.NotNull(address);
        if (!found)
            Assert.Equal(System.Net.IPAddress.Loopback, address);
    }

    [Fact]
    public void RegionsAreConsistent()
    {
        Assert.Equal(5, NetworkUtils.RegionKeys.Count);
        foreach (var key in NetworkUtils.RegionKeys)
            Assert.True(NetworkUtils.Regions.ContainsKey(key));
        Assert.StartsWith("eu-", NetworkUtils.GetServerNewId(NetworkUtils.RegionEu), StringComparison.Ordinal);
    }
}
