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
