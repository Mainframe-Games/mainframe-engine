using System.Diagnostics.CodeAnalysis;
using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// Lobby → transport handoff: connect strings, address parsing and transport selection, exercised end to end over
/// loopback (Steam itself cannot run here: no natives).
/// </summary>
public sealed class TransportHandoffTests
{
    /// <summary>A Steam sockets factory that behaves like the real one would without Steam.</summary>
    private sealed class UnavailableFactory(string scheme) : ITransportFactory
    {
        public int Attempts { get; private set; }

        public string Scheme => scheme;

        public bool TryConnect(in NetworkAddress address, uint connectData, [NotNullWhen(true)] out ITransport? transport, out string? failure)
        {
            Attempts++;
            transport = null;
            failure = "unavailable";
            return false;
        }
    }

    [Theory]
    [InlineData("enet:203.0.113.5:7777", "enet", "203.0.113.5", 7777)]
    [InlineData("ENET:localhost:1", "enet", "localhost", 1)]
    [InlineData("enet:[::1]:7777", "enet", "::1", 7777)]
    [InlineData(" steam:76561197960287930 ", "steam", "76561197960287930", 0)]
    [InlineData("loopback:host", "loopback", "host", 0)]
    [InlineData("custom:anything", "custom", "anything", 0)]
    public void AddressesParse(string text, string scheme, string host, int port)
    {
        Assert.True(NetworkAddress.TryParse(text, out var address));
        Assert.Equal(new NetworkAddress(scheme, host, (ushort)port), address);
        Assert.True(NetworkAddress.TryParse(address.ToString(), out var again));
        Assert.Equal(address, again);
    }

    [Theory]
    [InlineData("")]
    [InlineData("enet")]
    [InlineData(":host")]
    [InlineData("enet:host")]
    [InlineData("enet:host:99999")]
    [InlineData("enet::7777")]
    [InlineData("steam:0")]
    [InlineData("steam:abc")]
    [InlineData("loopback:")]
    public void MalformedAddressesDoNotParse(string text) => Assert.False(NetworkAddress.TryParse(text, out _));

    [Fact]
    public void ConnectStringsListAddressesBestFirst()
    {
        var text = NetworkAddress.FormatList(NetworkAddress.Steam(76561197960287930), NetworkAddress.Enet("::1", 7777));
        Assert.Equal("steam:76561197960287930;enet:[::1]:7777", text);
        Assert.Equal(76561197960287930ul, NetworkAddress.ParseList(text)[0].SteamId);
        Assert.Equal(["steam", "enet"], NetworkAddress.ParseList(text + ";;garbage").Select(a => a.Scheme));
        Assert.Empty(NetworkAddress.ParseList(null));
        Assert.Equal(0ul, NetworkAddress.Enet("h", 1).SteamId);
    }

    [Fact]
    public void TheSelectorFallsBackToTheNextAddress()
    {
        var steam = new UnavailableFactory("steam");
        var loopback = new LoopbackTransportFactory();
        var selector = new TransportSelector();
        selector.Register(steam);
        selector.Register(loopback);
        var server = LoopbackTransport.CreateServer();
        loopback.Add("host", server);

        Assert.True(selector.TryConnect("steam:76561197960287930;nope:1;loopback:host", 5, out var transport, out var address));
        Assert.Equal(1, steam.Attempts);
        Assert.Equal(NetworkAddress.Loopback("host"), address);
        Assert.Equal(1, server.ClientCount);
        Assert.False(selector.TryConnect("loopback:missing", 0, out _, out _));
        Assert.False(selector.TryConnect("", 0, out _, out _));
        Assert.True(selector.Supports("STEAM"));
        transport.Dispose();
        server.Dispose();
    }

    [Fact]
    public void TheDefaultSelectorKnowsEnetAndSteamAndSteamSocketsAreUnavailable()
    {
        Assert.True(TransportSelector.Default.Supports("enet"));
        Assert.True(TransportSelector.Default.Supports("steam"));
        Assert.False(SteamSocketsTransport.IsAvailable);
        Assert.False(TransportSelector.Default.TryConnect("steam:76561197960287930", 0, out _, out _));
    }

    [Fact]
    public void ALobbyConnectStringHandsOffToTheMultiplayerApi()
    {
        // Host: a server reachable over loopback; the lobby advertises Steam first (as a real host would).
        var loopback = new LoopbackTransportFactory();
        var selector = new TransportSelector();
        selector.Register(new SteamSocketsTransportFactory());
        selector.Register(loopback);

        var serverTree = NetHarness.CreateTree();
        using var server = MultiplayerApi.Attach(serverTree);
        server.RegisterScene(NetHarness.BoxScene, NetHarness.BoxUid);
        var endpoint = LoopbackTransport.CreateServer();
        server.StartServer(endpoint);
        var lobbyConnect = NetworkAddress.FormatList(NetworkAddress.Steam(76561197960287930), loopback.Add("lobby-host", endpoint));

        // Member: reads the lobby's connect metadata and connects through whatever works here.
        var clientTree = NetHarness.CreateTree();
        using var client = MultiplayerApi.Attach(clientTree);
        client.RegisterScene(NetHarness.BoxScene, NetHarness.BoxUid);
        Assert.True(client.TryConnect(lobbyConnect, selector));
        Assert.Throws<InvalidOperationException>(() => client.TryConnect(lobbyConnect, selector));

        var time = new GameTime { DeltaTime = NetHarness.FrameTime };
        for (var frame = 0; frame < 10 && !client.IsConnected; frame++)
        {
            serverTree.Tick(time);
            clientTree.Tick(time);
        }

        Assert.True(client.IsConnected);
        var box = server.Spawn<NetBox>(NetHarness.BoxScene);
        serverTree.Tick(time);
        clientTree.Tick(time);
        Assert.NotNull(client.FindNode(box.NetworkId));

        // Nothing reachable: false, still offline.
        using var stranded = MultiplayerApi.Attach(NetHarness.CreateTree());
        Assert.False(stranded.TryConnect("steam:76561197960287930;loopback:gone", selector));
        Assert.Equal(MultiplayerMode.Offline, stranded.Mode);

        client.Stop();
        server.Stop();
        clientTree.Shutdown();
        serverTree.Shutdown();
    }

    [Fact]
    public void WithoutSteamTheLobbyConnectAddressIsEmpty()
    {
        var lobby = new SteamLobbyInfo(42);
        lobby.ConnectAddress = "enet:1.2.3.4:5"; // no Steam: ignored
        Assert.Equal("", lobby.ConnectAddress);
        Assert.Equal("connect", SteamLobbyInfo.ConnectKey);
    }
}
