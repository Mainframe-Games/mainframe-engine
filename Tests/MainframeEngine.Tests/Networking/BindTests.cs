using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>Scene nodes every peer already has, networked by <see cref="MultiplayerSynchronizer"/> / <see cref="MultiplayerApi.Bind"/>.</summary>
public sealed class BindTests
{
    private static NetBox AddPlaced(SceneTree tree)
    {
        var box = new NetBox { Name = "Placed" };
        box.AddChild(new MultiplayerSynchronizer { Name = "Sync" });
        tree.CurrentScene!.AddChild(box);
        return box;
    }

    [Fact]
    public void ASynchronizedSceneNodeReplicatesToJoinedAndLateClients()
    {
        using var net = new NetHarness();
        var serverBox = AddPlaced(net.ServerTree);
        Assert.NotNull(serverBox.NetworkEntity); // the server binds it as soon as it is ready

        NetBox? clientBox = null;
        var client = net.AddClient(api => clientBox = AddPlaced(api.Tree));
        net.StepUntil(() => client.Api.IsConnected);
        serverBox.Score = 7;
        net.StepUntil(() => clientBox!.Score == 7);
        Assert.Same(clientBox, client.Find<NetBox>(serverBox.NetworkEntity!.NetId)); // bound, not instantiated
        Assert.Single(client.Tree.CurrentScene!.Children, c => c.Name == "Placed");

        NetBox? lateBox = null;
        var late = net.AddClient(api => lateBox = AddPlaced(api.Tree));
        net.StepUntil(() => late.Api.IsConnected && lateBox!.Score == 7);
    }

    [Fact]
    public void ASynchronizerInsideASpawnedSceneDoesNothing()
    {
        using var net = new NetHarness();
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene);
        var sync = new MultiplayerSynchronizer();
        box.AddChild(sync);
        var id = box.NetworkEntity!.NetId;
        net.Step();
        Assert.Equal(id, box.NetworkEntity!.NetId);
    }

    [Fact]
    public void ABindWaitsForTheServerToStart()
    {
        var tree = NetHarness.CreateTree();
        var api = MultiplayerApi.Attach(tree);
        var box = AddPlaced(tree);
        Assert.Null(box.NetworkEntity); // offline: nothing to bind yet
        Assert.Throws<InvalidOperationException>(() => api.Bind(box));
        tree.Shutdown();
    }
}

public sealed class SpawnConfigureTests
{
    [Fact]
    public void ConfigureRunsBeforeTheNodeEntersTheTreeAndItsValuesTravelInTheSpawn()
    {
        using var net = new NetHarness();
        var client = net.Join();
        var inTreeWhenConfigured = true;
        var box = net.Server.Spawn<NetBox>(NetHarness.BoxScene, configure: b => { inTreeWhenConfigured = b.IsInsideTree; b.Score = 42; });
        Assert.False(inTreeWhenConfigured);
        net.StepUntil(() => client.Find<NetBox>(box.NetworkEntity!.NetId) is { Score: 42 });
    }
}

public sealed class SendToServerTests
{
    private struct Hi : INetworkTransferable
    {
        public int Value;
        public readonly void NetworkWrite(NetBufferWriter w) => w.Write(Value);
        public void NetworkRead(NetBufferReader r) => Value = r.ReadInt32();
    }

    [Fact]
    public void AClientReachesTheServerWithAGameMessage()
    {
        using var net = new NetHarness(configure: api => api.Messages.Register<Hi>());
        var got = 0;
        net.Server.Bus!.Subscribe((in MessageContext ctx, in Hi hi) => got = hi.Value);
        var client = net.Join();
        Assert.False(net.Server.SendToServer(new Hi { Value = 1 })); // only clients send to the server
        Assert.True(client.Api.SendToServer(new Hi { Value = 9 }));
        net.StepUntil(() => got == 9);
    }
}
