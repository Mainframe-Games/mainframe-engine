using MainframeEngine.Networking;

namespace MainframeEngine.Tests.Networking;

/// <summary>
/// A server and any number of clients, each with its own <see cref="SceneTree"/> and <see cref="MultiplayerApi"/>,
/// linked by <see cref="LoopbackTransport"/> (optionally degraded by <see cref="SimulatedTransport"/>), stepped
/// together at a fixed 60 Hz frame on a manual clock: deterministic.
/// </summary>
internal sealed class NetHarness : IDisposable
{
    public const float FrameTime = 1f / 60f;

    public const string PlayerUid = "scn_00000000a001";
    public const string BossUid = "scn_00000000a002";
    public const string BoxUid = "scn_00000000a003";
    public const string AllTypesUid = "scn_00000000a004";
    public const string MarkerUid = "scn_00000000a005";

    private readonly LoopbackTransport _endpoint;
    private readonly NetworkConditions? _conditions;
    private int _seed;

    public NetHarness(NetworkConditions? conditions = null, int seed = 1, Action<MultiplayerApi>? configure = null)
    {
        _conditions = conditions;
        _seed = seed;
        Configure = configure;
        ServerTree = CreateTree();
        Server = CreateApi(ServerTree);
        _endpoint = LoopbackTransport.CreateServer();
        Server.StartServer(Wrap(_endpoint));
    }

    public double Time { get; private set; }

    public SceneTree ServerTree { get; }

    public MultiplayerApi Server { get; }

    public List<Client> Clients { get; } = [];

    public Action<MultiplayerApi>? Configure { get; }

    public static PackedScene PlayerScene { get; } = BuildPlayerScene();
    public static PackedScene BossScene { get; } = PackedScene.Pack(new NetBoss { Name = "Boss" });
    public static PackedScene BoxScene { get; } = PackedScene.Pack(new NetBox { Name = "Box" });
    public static PackedScene AllTypesScene { get; } = PackedScene.Pack(new NetAllTypes { Name = "AllTypes" });
    public static PackedScene MarkerScene { get; } = PackedScene.Pack(new Node3D { Name = "Marker" });

    /// <summary>Player root, a plain "Body" child and a networked "Body/Part" grandchild.</summary>
    private static PackedScene BuildPlayerScene()
    {
        var root = new NetPlayer { Name = "Player" };
        var body = new Node3D { Name = "Body" };
        root.AddChild(body);
        body.Owner = root;
        var part = new NetPart { Name = "Part" };
        body.AddChild(part);
        part.Owner = root;
        var scene = PackedScene.Pack(root);
        root.Free();
        return scene;
    }

    public sealed class Client(NetHarness harness, SceneTree tree, MultiplayerApi api, ITransport transport)
    {
        public SceneTree Tree { get; } = tree;
        public MultiplayerApi Api { get; } = api;
        public ITransport Transport { get; } = transport;

        /// <summary>When false the harness stops ticking this client (a frozen or vanished peer).</summary>
        public bool Ticking { get; set; } = true;

        public T? Find<T>(uint networkId) where T : Node => Api.FindNode(networkId) as T;

        public void Step(int frames = 1) => harness.Step(frames);
    }

    public static SceneTree CreateTree()
    {
        var tree = new SceneTree();
        tree.ChangeScene(new Node3D { Name = "Level" });
        return tree;
    }

    public MultiplayerApi CreateApi(SceneTree tree, MessageRegistry? messages = null)
    {
        var api = MultiplayerApi.Attach(tree, messages);
        api.RegisterScene(PlayerScene, PlayerUid);
        api.RegisterScene(BossScene, BossUid);
        api.RegisterScene(BoxScene, BoxUid);
        api.RegisterScene(AllTypesScene, AllTypesUid);
        api.RegisterScene(MarkerScene, MarkerUid);
        Configure?.Invoke(api);
        return api;
    }

    /// <summary>Connects a new client (the handshake completes after a few <see cref="Step"/>s).</summary>
    public Client AddClient(Action<MultiplayerApi>? configure = null, MessageRegistry? messages = null, uint? connectData = null)
    {
        var tree = CreateTree();
        var api = CreateApi(tree, messages);
        configure?.Invoke(api);
        var transport = Wrap(_endpoint.ConnectClient(connectData ?? api.Messages.Fingerprint));
        api.StartClient(transport);
        var client = new Client(this, tree, api, transport);
        Clients.Add(client);
        return client;
    }

    /// <summary>Connects a client and steps until it joined.</summary>
    public Client Join()
    {
        var client = AddClient();
        StepUntil(() => client.Api.IsConnected);
        return client;
    }

    public ITransport Wrap(ITransport transport) =>
        _conditions is { } conditions ? new SimulatedTransport(transport, conditions, _seed++, () => Time) : transport;

    /// <summary>Runs <paramref name="frames"/> frames: the server tree, then every ticking client tree.</summary>
    public void Step(int frames = 1)
    {
        for (var i = 0; i < frames; i++)
        {
            Time += FrameTime;
            var time = new GameTime { DeltaTime = FrameTime, FrameCount = (uint)(Time * 60) };
            ServerTree.Tick(time);
            foreach (var client in Clients)
                if (client.Ticking)
                    client.Tree.Tick(time);
        }
    }

    public void StepUntil(Func<bool> condition, int maxFrames = 600)
    {
        for (var i = 0; i < maxFrames; i++)
        {
            if (condition())
                return;
            Step();
        }

        Assert.True(condition(), $"Condition not met within {maxFrames} frames.");
    }

    public void Dispose()
    {
        foreach (var client in Clients)
        {
            client.Api.Stop();
            client.Tree.Shutdown();
            client.Tree.Servers.Dispose();
        }

        Server.Stop();
        ServerTree.Shutdown();
        ServerTree.Servers.Dispose();
    }
}
