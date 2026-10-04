using System.Numerics;
using MainframeEngine.Networking;
using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// Spawning scenes loaded from <c>.mscene</c> files by path or UID, including a scene that instances another scene
/// whose nodes are replicated (nested scene instances).
/// </summary>
[Collection(nameof(SerialResources))] // process-wide asset database and loader cache
public sealed class NestedSceneReplicationTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-net-tests", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previousAssets = AssetDatabase.Current;

    public NestedSceneReplicationTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder, "Scenes"));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previousAssets;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string ContentPath(string relative) => Path.Combine(_project, AssetDatabase.ContentFolder, relative);

    /// <summary>Turret.mscene (a NetPart) and Tank.mscene (a NetPlayer with two Turret instances, one overridden).</summary>
    private (string TankUid, string TankPath) SaveScenes()
    {
        var turret = new NetPart { Name = "Turret" };
        SceneSaver.Save(turret, ContentPath("Scenes/Turret.mscene"));
        turret.Free();
        var turretScene = ResourceLoader.Load<PackedScene>("Content/Scenes/Turret.mscene");

        var tank = new NetPlayer { Name = "Tank" };
        var front = turretScene.Instantiate();
        front.Name = "Front";
        tank.AddChild(front);
        front.Owner = tank;
        var back = (NetPart)turretScene.Instantiate();
        back.Name = "Back";
        back.Scale = new Vector3(2, 2, 2); // an override inside the nested instance
        tank.AddChild(back);
        back.Owner = tank;
        var uid = SceneSaver.Save(tank, ContentPath("Scenes/Tank.mscene"));
        tank.Free();
        turretScene.Release();
        return (uid, "Content/Scenes/Tank.mscene");
    }

    [Fact]
    public void NestedSceneInstancesReplicateTheirNodes()
    {
        var (uid, path) = SaveScenes();

        var serverTree = NetHarness.CreateTree();
        using var server = MultiplayerApi.Attach(serverTree);
        var tankScene = server.RegisterScene(path);               // by path; the file's UID identifies it
        Assert.Equal(uid, Assert.Single(server.SpawnableScenes));
        var endpoint = LoopbackTransport.CreateServer();
        server.StartServer(endpoint);

        var clientTree = NetHarness.CreateTree();
        using var client = MultiplayerApi.Attach(clientTree);
        client.RegisterScene(uid);                                // by UID
        client.StartClient(endpoint.ConnectClient(client.Messages.Fingerprint));

        var time = new GameTime { DeltaTime = NetHarness.FrameTime };
        void Step(int frames)
        {
            for (var i = 0; i < frames; i++)
            {
                serverTree.Tick(time);
                clientTree.Tick(time);
            }
        }

        Step(5);
        Assert.True(client.IsConnected);

        var tank = server.Spawn<NetPlayer>(tankScene);
        var front = tank.GetNode<NetPart>("Front");
        var back = tank.GetNode<NetPart>("Back");
        Assert.Equal([tank.NetworkId + 1, tank.NetworkId + 2], new[] { front.NetworkId, back.NetworkId });
        back.Spin = 5;  // set in the spawn frame: carried by the spawn
        front.Spin = 1;
        Step(3);

        var copy = (NetPlayer)client.FindNode(tank.NetworkId)!;
        Assert.Equal("Content/Scenes/Tank.mscene", copy.SceneFilePath);
        Assert.Equal(new Vector3(2, 2, 2), copy.GetNode<NetPart>("Back").Scale); // the instance override came from the file
        Assert.Equal("Content/Scenes/Turret.mscene", copy.GetNode("Front").SceneFilePath);
        Assert.Equal(5, copy.GetNode<NetPart>("Back").Spin, 3);
        Assert.Equal(1, copy.GetNode<NetPart>("Front").Spin, 3);

        front.Spin = 9;
        back.Tint = System.Drawing.Color.Green;
        tank.NetPosition = new Vector3(0, 0, 4);
        Step(30);
        Assert.Equal(9, copy.GetNode<NetPart>("Front").Spin, 3);
        Assert.Equal(System.Drawing.Color.Green.ToArgb(), copy.GetNode<NetPart>("Back").Tint.ToArgb());
        Assert.Equal(new Vector3(0, 0, 4), copy.Position);

        client.Stop();
        server.Stop();
        clientTree.Shutdown();
        serverTree.Shutdown();
    }
}
