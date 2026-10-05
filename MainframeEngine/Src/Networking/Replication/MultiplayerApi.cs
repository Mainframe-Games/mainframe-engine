using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

/// <summary>What a <see cref="MultiplayerApi"/> is doing.</summary>
public enum MultiplayerMode : byte
{
    /// <summary>Not started (or stopped): nothing is replicated.</summary>
    Offline,

    /// <summary>The authoritative host: spawns nodes, sends snapshots, validates client RPCs.</summary>
    Server,

    /// <summary>Connected (or connecting) to a server: mirrors its spawned nodes.</summary>
    Client,
}

/// <summary>
/// Server-authoritative node replication for one <see cref="SceneTree"/> (Godot's <c>MultiplayerAPI</c>): spawns and
/// despawns scenes on clients by <see cref="PackedScene"/> UID, sends <see cref="ReplicatedAttribute"/> members as delta
/// snapshots at <see cref="TickRate"/>, interpolates them on clients, routes <see cref="RpcAttribute"/> calls with
/// authority checks, and runs the connection lifecycle (handshake, timeouts, kicks) over any <see cref="ITransport"/>.
/// </summary>
/// <remarks>
/// <para>
/// A server in the M2 sense: register it with the tree's <see cref="ServerRegistry"/> (<see cref="Attach"/> does both;
/// <see cref="Engine"/> registers one as <see cref="Engine.Multiplayer"/>). It receives at the start of every frame's
/// process step (so RPCs and snapshots are applied before nodes process) and sends at the end of the frame (after
/// process, deferred frees and transform sync), with its own fixed network tick, separate from rendering and physics.
/// </para>
/// <para>
/// Steady-state ticks allocate nothing on either side (change detection and serialization are generated code, see
/// <see cref="ReplicatedState"/>). Single-threaded, like the tree.
/// </para>
/// </remarks>
public sealed partial class MultiplayerApi : IFrameServer
{
    /// <summary>The server's id in <see cref="Node.NetworkAuthority"/> and <see cref="RemoteSender"/>.</summary>
    public static readonly PeerId ServerPeerId = new(0);

    /// <summary>A client's <see cref="LocalPeerId"/> before the server assigned one.</summary>
    public static readonly PeerId UnassignedPeerId = new(ulong.MaxValue);

    /// <summary>Default snapshot rate (Hz).</summary>
    public const int DefaultTickRate = 30;

    private readonly SceneTree _tree;
    private readonly Action _onProcessFrame;
    private readonly Action<Node> _onNodeRemoved;
    private readonly List<PackedScene> _scenes = [];
    private readonly List<string> _sceneUids = [];
    private readonly List<PackedScene> _loadedScenes = [];
    private readonly Dictionary<uint, NetworkEntity> _entities = [];
    private readonly List<NetworkEntity> _entityList = []; // registration (= id) order
    private List<NetworkEntity> _pendingRemovals = [];
    private List<NetworkEntity> _removalBatch = [];
    private readonly NetBufferWriter _rpcArguments = new(256);

    private MessageBus? _bus;
    private NetworkStats _stats;
    private double _time;
    private double _rateWindowStart;
    private long _rateBytesSent;
    private long _rateBytesReceived;
    private bool _stopRequested;
    private List<NetworkAddress>? _connectCandidates;
    private TransportSelector? _connectSelector;
    private int _connectIndex;
    private bool _disposed;
    private PeerId _remoteSender = ServerPeerId;

    /// <param name="tree">The tree whose nodes are replicated.</param>
    /// <param name="messages">
    /// Message types (game messages plus the multiplayer layer's own, which use the top 16 ids). A new registry by
    /// default; the same game types must be registered on both ends.
    /// </param>
    public MultiplayerApi(SceneTree tree, MessageRegistry? messages = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        _tree = tree;
        Messages = messages ?? new MessageRegistry();
        if (Messages.IsFrozen)
            throw new ArgumentException("The message registry is already in use; give the multiplayer API its own.", nameof(messages));
        ReplicationMessageIds.Register(Messages);

        _onProcessFrame = OnProcessFrame;
        _onNodeRemoved = OnNodeRemoved;
        tree.ProcessFrame += _onProcessFrame;
        tree.NodeRemoved += _onNodeRemoved;
    }

    /// <summary>Creates a multiplayer API for <paramref name="tree"/> and registers it with <see cref="SceneTree.Servers"/>.</summary>
    public static MultiplayerApi Attach(SceneTree tree, MessageRegistry? messages = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        var api = new MultiplayerApi(tree, messages);
        tree.Servers.Register(api);
        return api;
    }

    // ------------------------------------------------------------------------------------------------
    // State and settings
    // ------------------------------------------------------------------------------------------------

    public SceneTree Tree => _tree;

    /// <summary>Message types; register game messages before starting, subscribe on <see cref="Bus"/>.</summary>
    public MessageRegistry Messages { get; }

    /// <summary>The message bus while started (game messages: <see cref="MessageBus.Subscribe{T}"/>, <see cref="MessageBus.Send{T}"/>).</summary>
    public MessageBus? Bus => _bus;

    public MultiplayerMode Mode { get; private set; }

    /// <summary>
    /// True on the server and while offline (Godot's rule: a game with no peer is its own authority, id
    /// <see cref="ServerPeerId"/>, so server-stepped logic runs in single-player and tool scenes). False only on a client.
    /// </summary>
    public bool IsServer => Mode != MultiplayerMode.Client;

    public bool IsClient => Mode == MultiplayerMode.Client;

    /// <summary>Server: running. Client: the handshake completed (<see cref="ConnectedToServer"/> was raised).</summary>
    public bool IsConnected => Mode == MultiplayerMode.Server || (Mode == MultiplayerMode.Client && _welcomed);

    /// <summary>This peer's id: <see cref="ServerPeerId"/> on the server, the id the server assigned on a client.</summary>
    public PeerId LocalPeerId { get; private set; } = ServerPeerId;

    /// <summary>
    /// During an RPC: the peer that called it (<see cref="ServerPeerId"/> for calls from the server, the local id for
    /// <see cref="RpcAttribute.CallLocal"/> calls).
    /// </summary>
    public PeerId RemoteSender => _remoteSender;

    /// <summary>
    /// Snapshots per second sent by the server (1–255, default <see cref="DefaultTickRate"/>). Set it before starting:
    /// clients learn it in the handshake.
    /// </summary>
    public int TickRate
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 255);
            if (Mode != MultiplayerMode.Offline && value != field)
                throw new InvalidOperationException("Set TickRate before starting; clients learn it in the handshake.");
            field = value;
        }
    } = DefaultTickRate;

    /// <summary>Server: the current network tick. Client: the newest snapshot tick applied. 0 while offline.</summary>
    public uint Tick => Mode switch
    {
        MultiplayerMode.Server => _tick,
        MultiplayerMode.Client => _lastAppliedTick,
        _ => 0,
    };

    /// <summary>Client: interpolate <see cref="ReplicatedAttribute.Interpolate"/> members (default true); false snaps to every snapshot.</summary>
    public bool Interpolation { get; set; } = true;

    /// <summary>Client: how far in the past (seconds) interpolated members are shown; about three snapshots by default.</summary>
    public float InterpolationDelay
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 0.1f;

    /// <summary>Client: how long (seconds) a moving member keeps its velocity when snapshots are late, before it stops.</summary>
    public float MaxExtrapolation
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    } = 0.25f;

    /// <summary>A peer silent for this long (seconds) is disconnected with <see cref="DisconnectReason.Timeout"/>.</summary>
    public float PeerTimeout
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            field = value;
        }
    } = 10f;

    /// <summary>A connection that has not completed the handshake within this time (seconds) is dropped.</summary>
    public float HandshakeTimeout
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, 0);
            field = value;
        }
    } = 5f;

    /// <summary>Server: free the nodes a client had authority over when it leaves (default); false hands them to the server.</summary>
    public bool DespawnOwnedOnDisconnect { get; set; } = true;

    /// <summary>Totals and rates.</summary>
    public NetworkStats Stats
    {
        get
        {
            var stats = _stats;
            if (_bus is not null)
            {
                stats.BytesSent = _bus.Stats.BytesSent;
                stats.BytesReceived = _bus.Stats.BytesReceived;
            }

            stats.NetworkedNodes = _entities.Count;
            return stats;
        }
    }

    /// <summary>The fingerprint exchanged in the handshake: <see cref="ReplicationRegistry.Fingerprint"/> and the spawnable scene UIDs.</summary>
    public uint ReplicationFingerprint { get; private set; }

    /// <summary>Scenes that can be spawned, in registration order (both ends must register the same, in the same order).</summary>
    public IReadOnlyList<string> SpawnableScenes => _sceneUids;

    // ------------------------------------------------------------------------------------------------
    // Events
    // ------------------------------------------------------------------------------------------------

    /// <summary>Server: a client completed the handshake (it has received every spawned node).</summary>
    public event Action<PeerId>? PeerJoined;

    /// <summary>Server: a client left (after <see cref="PeerJoined"/>).</summary>
    public event Action<PeerId, DisconnectReason>? PeerLeft;

    /// <summary>Client: the server accepted us; <see cref="LocalPeerId"/> is set.</summary>
    public event Action? ConnectedToServer;

    /// <summary>Client: the connection ended or could not be made. Replicated nodes are freed.</summary>
    public event Action<DisconnectReason>? Disconnected;

    /// <summary>A scene was spawned (server: when <see cref="Spawn"/> is called; client: when it arrives).</summary>
    public event Action<Node>? NodeSpawned;

    /// <summary>A networked node is being despawned (before it is freed on clients).</summary>
    public event Action<Node>? NodeDespawned;

    /// <summary>Server: a client's RPC failed the authority check and was dropped.</summary>
    public event Action<PeerId, Node, RpcInfo>? RpcRejected;

    /// <summary>Server: a client's RPC threw while running (the exception is logged, the server keeps going).</summary>
    public event Action<PeerId, Node, RpcInfo, Exception>? RpcFailed;

    /// <summary>Server: disconnect a client whose RPC throws (default false: log and continue).</summary>
    public bool KickOnRpcFailure { get; set; }

    // ------------------------------------------------------------------------------------------------
    // Spawnable scenes
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Makes <paramref name="scene"/> spawnable. Spawns carry the scene's index in this list, so server and clients
    /// must register the same scenes in the same order (verified in the handshake through the UIDs).
    /// </summary>
    /// <param name="scene">The scene.</param>
    /// <param name="uid">Its UID; defaults to <see cref="Resource.Uid"/> (required for in-memory scenes).</param>
    public void RegisterScene(PackedScene scene, string? uid = null)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ThrowIfDisposed();
        if (Mode != MultiplayerMode.Offline)
            throw new InvalidOperationException("Register spawnable scenes before starting the server or client.");
        uid ??= scene.Uid ?? throw new ArgumentException("The scene has no UID; pass one.", nameof(scene));
        if (_sceneUids.Contains(uid))
            throw new InvalidOperationException($"Scene {uid} is already registered.");
        if (_scenes.Contains(scene))
            throw new InvalidOperationException($"This scene is already registered as {_sceneUids[_scenes.IndexOf(scene)]}.");
        _scenes.Add(scene);
        _sceneUids.Add(uid);
    }

    /// <summary>Loads a scene by path or UID (<see cref="ResourceLoader"/>) and makes it spawnable. Released on dispose.</summary>
    public PackedScene RegisterScene(string pathOrUid)
    {
        var scene = ResourceLoader.Load<PackedScene>(pathOrUid);
        try
        {
            RegisterScene(scene, scene.Uid ?? (AssetUid.IsUid(pathOrUid) ? pathOrUid : null));
        }
        catch
        {
            scene.Release();
            throw;
        }

        _loadedScenes.Add(scene);
        return scene;
    }

    // ------------------------------------------------------------------------------------------------
    // Start / stop
    // ------------------------------------------------------------------------------------------------

    /// <summary>Starts a server on <paramref name="transport"/> (an accepting transport, e.g. <see cref="EnetTransport.Listen"/>). Owns it.</summary>
    public void StartServer(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ThrowIfDisposed();
        if (!transport.IsServer)
            throw new ArgumentException("A server needs an accepting transport.", nameof(transport));
        Start(transport, MultiplayerMode.Server);
        LocalPeerId = ServerPeerId;
        _tick = 1;
        _tickAccumulator = 0;
        Log.Info($"[Net] multiplayer server started ({TickRate} Hz, {_sceneUids.Count} spawnable scenes)");
    }

    /// <summary>Starts a client on <paramref name="transport"/> (a connecting transport, e.g. <see cref="EnetTransport.Connect"/> with <see cref="MessageRegistry.Fingerprint"/>). Owns it.</summary>
    public void StartClient(ITransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        ThrowIfDisposed();
        if (transport.IsServer)
            throw new ArgumentException("A client needs a connecting transport.", nameof(transport));
        _connectCandidates = null; // an explicit transport: no address fallback
        StartClientCore(transport);
    }

    private void StartClientCore(ITransport transport)
    {
        Start(transport, MultiplayerMode.Client);
        LocalPeerId = UnassignedPeerId;
        ResetClientState();
        _clientStartedAt = _time;
    }

    /// <summary>Listens for ENet clients on <paramref name="port"/>.</summary>
    public void Host(ushort port, int maxClients, string? bindAddress = null) =>
        StartServer(EnetTransport.Listen(port, maxClients, bindAddress));

    /// <summary>Connects to an ENet server.</summary>
    public void Connect(string host, ushort port, uint timeoutMs = 0) =>
        StartClient(EnetTransport.Connect(host, port, Messages.Fingerprint, timeoutMs));

    /// <summary>
    /// Connects through <paramref name="connectString"/>: a <see cref="NetworkAddress"/> list such as a Steam lobby's
    /// <c>connect</c> metadata (<c>"steam:7656…;enet:203.0.113.5:7777"</c>), trying each in order (see
    /// <see cref="TransportSelector"/>). Connections complete asynchronously: if one then fails (no answer, or no
    /// handshake in time) the next address is tried, and <see cref="Disconnected"/> is raised only when none is left.
    /// False when no address could even be opened.
    /// </summary>
    public bool TryConnect(string connectString, TransportSelector? selector = null)
    {
        ArgumentNullException.ThrowIfNull(connectString);
        ThrowIfDisposed();
        if (Mode != MultiplayerMode.Offline)
            throw new InvalidOperationException($"Already running as {Mode}; call Stop first.");
        _connectSelector = selector ?? TransportSelector.Default;
        _connectCandidates = NetworkAddress.ParseList(connectString);
        return TryConnectFrom(0);
    }

    // Opens the first usable candidate at or after `index`; remembers where it is for the fallback.
    private bool TryConnectFrom(int index)
    {
        var candidates = _connectCandidates;
        var selector = _connectSelector;
        if (candidates is null || selector is null)
            return false;
        for (var i = index; i < candidates.Count; i++)
        {
            if (!selector.TryConnect(candidates[i], Messages.Fingerprint, out var transport, out var failure))
            {
                Log.Info($"[Net] cannot use '{candidates[i]}': {failure}");
                continue;
            }

            Log.Info($"[Net] connecting through {candidates[i]}");
            _connectIndex = i;
            StartClientCore(transport);
            return true;
        }

        _connectCandidates = null;
        _connectSelector = null;
        return false;
    }

    private void Start(ITransport transport, MultiplayerMode mode)
    {
        if (Mode != MultiplayerMode.Offline)
        {
            transport.Dispose();
            throw new InvalidOperationException($"Already running as {Mode}; call Stop first.");
        }

        ReplicationFingerprint = ComputeFingerprint();
        _bus = new MessageBus(transport, Messages);
        if (mode == MultiplayerMode.Server)
        {
            _bus.PeerConnected += OnServerPeerConnected;
            _bus.PeerDisconnected += OnServerPeerDisconnected;
            _bus.Subscribe<HelloMessage>(OnHello);
            _bus.Subscribe<AckMessage>(OnAck);
        }
        else
        {
            _bus.PeerConnected += OnClientConnected;
            _bus.PeerDisconnected += OnClientDisconnected;
            _bus.Subscribe<WelcomeMessage>(OnWelcome);
            _bus.Subscribe<SpawnMessage>(OnSpawn);
            _bus.Subscribe<DespawnMessage>(OnDespawn);
            _bus.Subscribe<SnapshotMessage>(OnSnapshot);
            _bus.Subscribe<AuthorityMessage>(OnAuthority);
        }

        _bus.Subscribe<RpcMessage>(OnRpc);
        Mode = mode;
        _stats = default;
        _rateWindowStart = _time;
        _rateBytesSent = 0;
        _rateBytesReceived = 0;
        _stopRequested = false;
    }

    /// <summary>
    /// Stops. A server keeps its nodes (they stop being networked) and clients see it shut down; a client frees every
    /// node the server spawned.
    /// </summary>
    public void Stop()
    {
        if (Mode == MultiplayerMode.Offline)
            return;

        var wasClient = Mode == MultiplayerMode.Client;
        Mode = MultiplayerMode.Offline;
        _connectCandidates = null;
        _connectSelector = null;
        var bus = _bus;
        _bus = null;
        bus?.Dispose();

        ReleaseAllEntities(freeClientNodes: wasClient);
        _peers.Clear();
        _peersById.Clear();
        _readyPeers.Clear();
        _pendingSpawns.Clear();
        _pendingRemovals.Clear();
        _removalBatch.Clear();
        _freeSlots.Clear();
        _nextSlot = 0;
        ResetClientState();
        LocalPeerId = ServerPeerId;
        Log.Info($"[Net] multiplayer {(wasClient ? "client" : "server")} stopped");
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _disposed = true;
        _tree.ProcessFrame -= _onProcessFrame;
        _tree.NodeRemoved -= _onNodeRemoved;
        foreach (var scene in _loadedScenes)
            if (scene.ReferenceCount > 0)
                scene.Release();
        _loadedScenes.Clear();
        _rpcArguments.ReleaseStorage();
        _entryWriter.ReleaseStorage();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    // FNV-1a over the replication schema of every loaded networked type and the spawnable scene UIDs.
    // FNV-1a over every spawnable scene: its UID, then each networked node it instantiates (in id order) with the
    // type name and schema hash of every replication info in its chain. Scoped to what can actually be spawned, so
    // unrelated assemblies loaded on one side only (tools, editor reloads) cannot cause a mismatch.
    private uint ComputeFingerprint()
    {
        var hash = Fnv.Offset;
        var nodes = new List<Node>();
        for (var i = 0; i < _scenes.Count; i++)
        {
            hash = Fnv.Add(hash, _sceneUids[i]);
            var instance = _scenes[i].Instantiate();
            try
            {
                nodes.Clear();
                CollectNetworked(instance, nodes, isRoot: true);
                foreach (var node in nodes)
                {
                    hash = Fnv.Add(hash, node.GetType().FullName ?? node.GetType().Name);
                    foreach (var info in ReplicationRegistry.GetChain(node.GetType())?.Infos ?? [])
                        hash = Fnv.Add(hash, info.SchemaHash);
                }
            }
            finally
            {
                instance.Free();
            }
        }

        return hash;
    }

    // ------------------------------------------------------------------------------------------------
    // Frame
    // ------------------------------------------------------------------------------------------------

    // Start of the tree's process step: receive, then (clients) apply interpolation for this frame.
    private void OnProcessFrame()
    {
        if (_bus is null)
            return;

        var delta = Math.Max(0f, _tree.ProcessDeltaTime);
        _time += delta;
        _bus.Poll();
        if (_stopRequested)
        {
            StopAfterDisconnect();
            return;
        }

        if (Mode == MultiplayerMode.Client)
        {
            AdvanceClock(delta);
            ApplyInterpolation();
        }
    }

    /// <summary>End of the frame (after process, deferred frees and transform sync): send.</summary>
    void IFrameServer.Process(in GameTime gameTime)
    {
        if (_bus is null)
            return;

        if (Mode == MultiplayerMode.Server)
            ServerProcess(Math.Max(0f, gameTime.DeltaTime));
        else
            ClientProcess(Math.Max(0f, gameTime.DeltaTime));

        if (_stopRequested)
        {
            StopAfterDisconnect();
            return;
        }

        _bus?.Flush();
        UpdateRates();
    }

    private void UpdateRates()
    {
        var elapsed = _time - _rateWindowStart;
        if (elapsed < 1 || _bus is null)
            return;
        var stats = _bus.Stats;
        _stats.SendBytesPerSecond = (stats.BytesSent - _rateBytesSent) / elapsed;
        _stats.ReceiveBytesPerSecond = (stats.BytesReceived - _rateBytesReceived) / elapsed;
        _rateBytesSent = stats.BytesSent;
        _rateBytesReceived = stats.BytesReceived;
        _rateWindowStart = _time;
    }

    // ------------------------------------------------------------------------------------------------
    // Entities
    // ------------------------------------------------------------------------------------------------

    /// <summary>The networked node with <paramref name="networkId"/>, or null.</summary>
    public Node? FindNode(uint networkId) => _entities.TryGetValue(networkId, out var entity) ? entity.Node : null;

    private void AddEntity(NetworkEntity entity)
    {
        _entities.Add(entity.NetId, entity);
        _entityList.Add(entity);
        entity.Node!.NetworkEntity = entity;
        if (entity.HasInterpolation)
            _interpolated.Add(entity);
    }

    /// <summary>Forgets <paramref name="entity"/>; its node (if any) is no longer networked.</summary>
    private void ReleaseEntity(NetworkEntity entity)
    {
        if (entity.Released)
            return;
        entity.Released = true;
        if (_entities.TryGetValue(entity.NetId, out var registered) && ReferenceEquals(registered, entity))
            _entities.Remove(entity.NetId);
        FreeSlot(entity);
        _entityList.Remove(entity);
        if (entity.HasInterpolation)
            _interpolated.Remove(entity);
        if (entity.Node is { } node && ReferenceEquals(node.NetworkEntity, entity))
            node.NetworkEntity = null;
        entity.Node = null;
    }

    private void ReleaseAllEntities(bool freeClientNodes)
    {
        // Snapshot: releasing edits the list.
        var all = _entityList.ToArray();
        foreach (var entity in all)
        {
            var node = entity.Node;
            var isRoot = entity.IsRoot;
            ReleaseEntity(entity);
            if (freeClientNodes && isRoot && node is { IsFreed: false })
                node.QueueFree();
        }

        _entities.Clear();
        _entityList.Clear();
        _interpolated.Clear();
    }

    private void OnNodeRemoved(Node node)
    {
        if (node.NetworkEntity is not { } entity || !ReferenceEquals(entity.Api, this) || entity.Released || entity.PendingRemoval)
            return;
        entity.PendingRemoval = true;
        _pendingRemovals.Add(entity);
    }

    /// <summary>
    /// Networked nodes that left the tree this frame: still out of the tree → despawned (server) or forgotten
    /// (client freed it itself). Nodes that came back (reparented) stay networked.
    /// </summary>
    private void ProcessRemovals()
    {
        // Despawn handlers (NodeDespawned) may free more networked nodes, which queues more removals: work on a
        // swapped-out batch and repeat until nothing is left (bounded, in case handlers keep re-adding nodes).
        for (var round = 0; round < 64 && _pendingRemovals.Count > 0; round++)
        {
            (_pendingRemovals, _removalBatch) = (_removalBatch, _pendingRemovals);
            var batch = _removalBatch;
            foreach (var entity in batch)
                entity.PendingRemoval = false;

            // Roots first, so their descendants go with one despawn message.
            for (var pass = 0; pass < 2; pass++)
            {
                for (var i = 0; i < batch.Count; i++)
                {
                    var entity = batch[i];
                    if (entity.Released || entity.IsRoot != (pass == 0))
                        continue;
                    if (entity.Node is { IsFreed: false } node && ReferenceEquals(node.Tree, _tree))
                        continue; // re-added in the same frame

                    if (Mode == MultiplayerMode.Server)
                        ServerDespawn(entity);
                    else
                        ReleaseEntity(entity);
                }
            }

            batch.Clear();
            if (Mode == MultiplayerMode.Offline)
                return; // a handler stopped the API
        }
    }

    // Root and every networked descendant, depth-first in tree order: the id order both ends compute.
    internal static void CollectNetworked(Node node, List<Node> into, bool isRoot)
    {
        if (isRoot || ReplicationRegistry.GetChain(node.GetType()) is not null)
            into.Add(node);
        for (var i = 0; i < node.ChildCount; i++)
            CollectNetworked(node.GetChild(i), into, isRoot: false);
    }

    // ------------------------------------------------------------------------------------------------
    // RPC calls (generated senders)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Starts an RPC from generated code: if the call goes out, <see cref="RpcCall.Writer"/> receives the arguments and
    /// <see cref="EndRpc"/> sends them; <see cref="RpcCall.InvokeLocally"/> says whether to run the method here too.
    /// </summary>
    public static RpcCall BeginRpc(Node node, RpcInfo rpc) => Begin(node, rpc, default, targeted: false);

    /// <summary>Like <see cref="BeginRpc(Node, RpcInfo)"/>, for one peer.</summary>
    public static RpcCall BeginRpc(Node node, RpcInfo rpc, PeerId peer) => Begin(node, rpc, peer, targeted: true);

    /// <summary>Sends the RPC started by <see cref="BeginRpc(Node, RpcInfo)"/>.</summary>
    public static void EndRpc(in RpcCall call)
    {
        if (call.Api is { } api && call.Entity is { } entity)
            api.SendRpc(entity, call.Rpc, call.Target, call.Targeted);
    }

    /// <summary>Sets <see cref="RemoteSender"/> to the local peer for a <see cref="RpcAttribute.CallLocal"/> invocation; returns the previous sender.</summary>
    public static PeerId BeginLocalCall(in RpcCall call)
    {
        if (call.LocalApi is not { } api)
            return ServerPeerId;
        var previous = api._remoteSender;
        api._remoteSender = api.LocalPeerId;
        return previous;
    }

    /// <summary>Restores <see cref="RemoteSender"/> after <see cref="BeginLocalCall"/>.</summary>
    public static void EndLocalCall(in RpcCall call, PeerId previous)
    {
        if (call.LocalApi is { } api)
            api._remoteSender = previous;
    }
}

/// <summary>An RPC call in progress (generated code; see <see cref="MultiplayerApi.BeginRpc(Node, RpcInfo)"/>).</summary>
[StructLayout(LayoutKind.Auto)]
public readonly struct RpcCall
{
    internal RpcCall(MultiplayerApi? api, MultiplayerApi? localApi, NetworkEntity? entity, RpcInfo rpc, PeerId target, bool targeted,
        NetBufferWriter? writer, bool invokeLocally)
    {
        Api = api;
        LocalApi = localApi;
        Entity = entity;
        Rpc = rpc;
        Target = target;
        Targeted = targeted;
        Writer = writer;
        InvokeLocally = invokeLocally;
    }

    internal MultiplayerApi? Api { get; }
    internal MultiplayerApi? LocalApi { get; }
    internal NetworkEntity? Entity { get; }
    internal RpcInfo Rpc { get; }
    internal PeerId Target { get; }
    internal bool Targeted { get; }

    /// <summary>Where the arguments go when the call is sent; null when nothing is sent.</summary>
    public NetBufferWriter? Writer { get; }

    /// <summary>Run the method on this peer too.</summary>
    public bool InvokeLocally { get; }
}
