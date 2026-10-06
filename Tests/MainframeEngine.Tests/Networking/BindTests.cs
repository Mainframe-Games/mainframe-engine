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

public sealed class MultiplayerSpawnerTests
{
    private static MultiplayerSpawner AddSpawner(SceneTree tree, List<string>? spawned = null)
    {
        var spawner = new MultiplayerSpawner { Name = "Spawner", SpawnPath = ".." };
        spawner.SpawnFunction = data => new NetBox { Name = (string)data["name"], Position = new System.Numerics.Vector3((float)data["x"], 0, 0) };
        if (spawned is not null)
            spawner.Spawned += n => spawned.Add(n.Name);
        tree.CurrentScene!.AddChild(spawner);
        return spawner;
    }

    [Fact]
    public void SpawnDataKeepsItsTypes()
    {
        var data = new SpawnData { ["owner"] = 7L, ["slot"] = 2, ["x"] = 1.5f, ["crew"] = "npc3", ["ok"] = true, ["keys"] = new[] { 1, 2 } };
        var back = SpawnData.Deserialize(data.Serialize());
        Assert.Equal(7L, (long)back["owner"]);
        Assert.Equal(2, (int)back["slot"]);
        Assert.Equal(1.5f, (float)back["x"]);
        Assert.Equal("npc3", (string)back["crew"]);
        Assert.True((bool)back["ok"]);
        Assert.Equal([1, 2], (int[])back["keys"]);
    }

    [Fact]
    public void ClientsBuildTheNodeFromTheSameDataLateJoinersToo()
    {
        using var net = new NetHarness();
        var spawner = AddSpawner(net.ServerTree);
        var seen = new List<string>();
        var client = net.AddClient(api => AddSpawner(api.Tree, seen));
        net.StepUntil(() => client.Api.IsConnected);
        var box = (NetBox)spawner.Spawn(new SpawnData { ["name"] = "Crate_1", ["x"] = 4f });
        box.Score = 3;
        net.StepUntil(() => client.Find<NetBox>(box.NetworkEntity!.NetId) is { Score: 3 });
        var copy = client.Find<NetBox>(box.NetworkEntity!.NetId)!;
        Assert.Equal("Crate_1", copy.Name);
        Assert.Equal(4f, copy.Position.X);
        Assert.Same(client.Tree.CurrentScene, copy.Parent);
        Assert.Equal(["Crate_1"], seen);

        var late = net.AddClient(api => AddSpawner(api.Tree));
        net.StepUntil(() => late.Api.IsConnected && late.Find<NetBox>(box.NetworkEntity!.NetId) is { Name: "Crate_1", Score: 3 });
    }
}
