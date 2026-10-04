using System.Drawing;
using System.Globalization;
using ImGuiNET;
using MainframeEngine.Networking;

namespace MainframeEngine.Sandbox;

/// <summary>
/// <c>--server [port]</c> / <c>--client &lt;host&gt; [port]</c>: the Sandbox as a listen server that spawns a few
/// orbiting <see cref="NetBox"/>es, or as a client that shows them replicated (interpolated) from the server. Both
/// log the replication state every two seconds, so a local server + client run can be checked from the logs.
/// <c>--quit-after &lt;seconds&gt;</c> exits after that long (scripted QA runs).
/// </summary>
public sealed class NetworkDemo
{
    public const ushort DefaultPort = 7777;
    public const string BoxScene = "Content/Scenes/NetBox.mscene";
    public const int BoxCount = 4;

    private static readonly Color[] Colors = [Color.OrangeRed, Color.MediumSeaGreen, Color.DodgerBlue, Color.Gold];

    private NetworkDemo(bool isServer, string host, ushort port)
    {
        IsServer = isServer;
        Host = host;
        Port = port;
    }

    public bool IsServer { get; }

    public string Host { get; }

    public ushort Port { get; }

    /// <summary>Exit after this many seconds (0: run until closed).</summary>
    public double QuitAfter { get; private init; }

    private Engine? _engine;
    private MultiplayerApi? _api;
    private double _elapsed;
    private double _logTimer;
    private double _pingTimer;
    private int _pings;

    /// <summary>Parses <c>--server [port]</c> or <c>--client host [port]</c>; null when neither is given.</summary>
    public static NetworkDemo? FromArgs(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        double quitAfter = 0;
        var quitIndex = Array.IndexOf(args, "--quit-after");
        if (quitIndex >= 0 && (quitIndex + 1 >= args.Length
                               || !double.TryParse(args[quitIndex + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out quitAfter)))
            throw new ArgumentException("--quit-after needs a number of seconds, e.g. --quit-after 10");

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server")
                return new NetworkDemo(true, "", ParsePort(args, i + 1)) { QuitAfter = quitAfter };
            if (args[i] == "--client")
            {
                if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("--client needs a host, e.g. --client 127.0.0.1");
                return new NetworkDemo(false, args[i + 1], ParsePort(args, i + 2)) { QuitAfter = quitAfter };
            }
        }

        return null;
    }

    private static ushort ParsePort(string[] args, int index) =>
        index < args.Length && ushort.TryParse(args[index], NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : DefaultPort;

    /// <summary>Hosts or connects, after the Sandbox scene is loaded (both ends load the same level).</summary>
    public void Start(Engine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _engine = engine;
        _api = engine.Multiplayer;
        var boxScene = _api.RegisterScene(BoxScene);
        _api.PeerJoined += peer => Log.Info($"[NetDemo] client {peer} joined");
        _api.PeerLeft += (peer, reason) => Log.Info($"[NetDemo] client {peer} left ({reason})");
        _api.ConnectedToServer += () => Log.Info($"[NetDemo] connected as {_api.LocalPeerId}");
        _api.Disconnected += reason => Log.Info($"[NetDemo] disconnected ({reason})");
        _api.NodeSpawned += node => Log.Info($"[NetDemo] spawned {node.Name} #{node.NetworkId}");

        if (IsServer)
        {
            _api.Host(Port, maxClients: 8);
            var scene = engine.Tree.CurrentScene;
            for (var i = 0; i < BoxCount; i++)
            {
                var box = _api.Spawn<NetBox>(boxScene, scene);
                box.Name = $"NetBox{i}";
                box.Phase = i * MathF.Tau / BoxCount;
                box.OrbitRadius = 2.5f + i * 0.5f;
                box.NetColor = Colors[i % Colors.Length];
            }

            Log.Info($"[NetDemo] server listening on port {Port} with {BoxCount} boxes");
        }
        else
        {
            _api.Connect(Host, Port, timeoutMs: 5000);
            Log.Info($"[NetDemo] client connecting to {Host}:{Port}");
        }
    }

    /// <summary>Every frame: periodic logs, and (server) a ping RPC every 3 s.</summary>
    public void Update(in GameTime gameTime)
    {
        if (_api is null)
            return;

        _elapsed += gameTime.DeltaTime;
        if (QuitAfter > 0 && _elapsed >= QuitAfter)
        {
            var totals = _api.Stats;
            Log.Info(FormattableString.Invariant(
                $"[NetDemo] quitting after {QuitAfter} s: {totals.Snapshots} snapshots, {totals.Spawns} spawns, {totals.RpcsSent} RPCs sent, {totals.RpcsReceived} received, {totals.BytesSent} B out, {totals.BytesReceived} B in"));
            _engine?.Quit(ExitCode.Ok);
            QuitAfterDone();
        }

        if (IsServer && _api.ConnectedPeers.Length > 0)
        {
            _pingTimer += gameTime.DeltaTime;
            if (_pingTimer >= 3)
            {
                _pingTimer = 0;
                _pings++;
                if (FirstBox() is { } box)
                    box.RpcPing(_pings);
            }
        }

        _logTimer += gameTime.DeltaTime;
        if (_logTimer < 2)
            return;
        _logTimer = 0;

        var stats = _api.Stats;
        var first = FirstBox();
        var position = first is null ? "-" : FormattableString.Invariant($"<{first.Position.X:0.00}, {first.Position.Y:0.00}, {first.Position.Z:0.00}>");
        if (IsServer)
        {
            Log.Info(FormattableString.Invariant(
                $"[NetDemo] server tick {_api.Tick}, {_api.ConnectedPeers.Length} clients, {stats.NetworkedNodes} nodes, {first?.Name} at {position}, ping {_pings}, out {stats.SendBytesPerSecond:0} B/s, in {stats.ReceiveBytesPerSecond:0} B/s"));
        }
        else
        {
            Log.Info(FormattableString.Invariant(
                $"[NetDemo] client {(_api.IsConnected ? "connected" : _api.Mode.ToString())}, snapshot tick {_api.Tick}, render tick {_api.RenderTick:0.0}, {stats.NetworkedNodes} nodes, {first?.Name} at {position}, ping {first?.Pings ?? 0}, in {stats.ReceiveBytesPerSecond:0} B/s, discarded {stats.SnapshotsDiscarded}"));
        }
    }

    /// <summary>A small ImGui section with the connection state.</summary>
    public void DrawImGui()
    {
        if (_api is null)
            return;
        ImGui.SeparatorText(IsServer ? "Network (server)" : "Network (client)");
        var stats = _api.Stats;
        Span<char> text = stackalloc char[96];
        if (text.TryWrite(CultureInfo.InvariantCulture, $"Tick {_api.Tick}  nodes {stats.NetworkedNodes}", out var written))
            ImGui.TextUnformatted(text[..written]);
        if (text.TryWrite(CultureInfo.InvariantCulture, $"Out {stats.SendBytesPerSecond:0} B/s  in {stats.ReceiveBytesPerSecond:0} B/s", out written))
            ImGui.TextUnformatted(text[..written]);
        if (IsServer && text.TryWrite(CultureInfo.InvariantCulture, $"Clients {_api.ConnectedPeers.Length}", out written))
            ImGui.TextUnformatted(text[..written]);
    }

    private void QuitAfterDone() => _elapsed = double.NegativeInfinity;

    private NetBox? FirstBox()
    {
        var scene = _api?.Tree.CurrentScene;
        if (scene is null)
            return null;
        for (var i = 0; i < scene.ChildCount; i++)
            if (scene.GetChild(i) is NetBox box)
                return box;
        return null;
    }
}
