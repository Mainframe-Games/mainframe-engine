using System.Runtime.InteropServices;

namespace MainframeEngine.Networking;

public sealed partial class MultiplayerApi
{
    private const int SendTimeSlots = 64;

    private readonly List<PeerState> _peers = [];
    private readonly Dictionary<PeerId, PeerState> _peersById = [];
    private readonly List<PeerId> _readyPeers = [];
    private readonly List<NetworkEntity> _pendingSpawns = [];
    private readonly List<Node> _collectBuffer = [];
    private readonly (uint Tick, double Time)[] _sendTimes = new (uint, double)[SendTimeSlots];
    private uint _tick = 1;
    private double _tickAccumulator;
    private uint _nextNetId = 1;

    /// <summary>Server: clients that completed the handshake, in join order. Valid until the next frame.</summary>
    public ReadOnlySpan<PeerId> ConnectedPeers => CollectionsMarshal.AsSpan(_readyPeers);

    /// <summary>Server: one client's traffic and acknowledgement state; false for an unknown peer.</summary>
    public bool GetPeerStats(PeerId peer, out PeerNetworkStats stats)
    {
        if (!_peersById.TryGetValue(peer, out var state))
        {
            stats = default;
            return false;
        }

        stats = new PeerNetworkStats(state.Id, state.Ready, state.AckedTick, state.BytesSent, state.SnapshotsSent,
            state.LastSnapshotBytes, state.RoundTripTime);
        return true;
    }

    // ------------------------------------------------------------------------------------------------
    // Spawning (server)
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Server: instantiates a registered scene under <paramref name="parent"/> and replicates it to every client
    /// (and to clients joining later). Configure the returned node during this frame: the spawn message is sent at
    /// the end of the frame with the state at that time.
    /// </summary>
    /// <param name="scene">A scene registered with <see cref="RegisterScene(PackedScene, string)"/>.</param>
    /// <param name="parent">
    /// Where to add it (default: the current scene, else the root): a networked node, or a node that exists at the same
    /// path on clients (e.g. part of the level both load).
    /// </param>
    /// <param name="authority">The peer with authority over the new nodes (default: the server).</param>
    public Node Spawn(PackedScene scene, Node? parent = null, PeerId authority = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ThrowIfDisposed();
        if (Mode != MultiplayerMode.Server)
            throw new InvalidOperationException("Only a running server can spawn networked scenes.");
        var index = _scenes.IndexOf(scene);
        if (index < 0)
            throw new InvalidOperationException("The scene is not registered; call RegisterScene on the server and on every client.");
        parent ??= _tree.CurrentScene ?? _tree.Root;
        if (!ReferenceEquals(parent.Tree, _tree))
            throw new ArgumentException("The parent must be inside this multiplayer API's tree.", nameof(parent));

        var root = scene.Instantiate();
        try
        {
            RegisterSpawn(root, index, parent, authority);
        }
        catch
        {
            root.Free();
            throw;
        }

        parent.AddChild(root);
        _stats.Spawns++;
        NodeSpawned?.Invoke(root);
        return root;
    }

    /// <summary>Server: <see cref="Spawn"/> and cast the root.</summary>
    public T Spawn<T>(PackedScene scene, Node? parent = null, PeerId authority = default) where T : Node
    {
        var root = Spawn(scene, parent, authority);
        if (root is T typed)
            return typed;
        root.QueueFree();
        throw new InvalidCastException($"The scene's root is a {root.GetType().Name}, not a {typeof(T).Name}.");
    }

    /// <summary>Server: frees a networked node; clients free it too (same as <see cref="Node.QueueFree"/>).</summary>
    public static void Despawn(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        node.QueueFree();
    }

    /// <summary>
    /// Server: gives <paramref name="peer"/> authority over <paramref name="node"/> (and, by default, its networked
    /// descendants) and tells the clients.
    /// </summary>
    public void SetAuthority(Node node, PeerId peer, bool includeDescendants = true)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Mode != MultiplayerMode.Server)
            throw new InvalidOperationException("Only the server assigns authority.");
        if (node.NetworkEntity is not { } entity || !ReferenceEquals(entity.Api, this) || entity.Released)
            throw new ArgumentException("The node is not networked by this multiplayer API.", nameof(node));

        SetAuthority(entity, peer);
        if (includeDescendants && entity.IsRoot)
            foreach (var descendant in entity.Descendants)
                if (!descendant.Released)
                    SetAuthority(descendant, peer);
    }

    private void SetAuthority(NetworkEntity entity, PeerId peer)
    {
        if (entity.Authority == peer)
            return;
        entity.Authority = peer;
        if (!entity.Root.SpawnSent)
            return; // the spawn will carry it
        var message = new AuthorityMessage { NetId = entity.NetId, Authority = peer };
        foreach (var client in _readyPeers)
            _bus!.Send(client, in message);
    }

    /// <summary>Server: disconnects a client (<see cref="DisconnectReason.Kicked"/> unless another reason is given).</summary>
    public void Kick(PeerId peer, DisconnectReason reason = DisconnectReason.Kicked)
    {
        if (Mode != MultiplayerMode.Server || !_peersById.TryGetValue(peer, out var state) || state.Closing)
            return;
        Log.Info($"[Net] kicking {peer} ({reason})");
        Close(state, reason);
    }

    private void Close(PeerState peer, DisconnectReason reason)
    {
        peer.Closing = true;
        _bus?.Disconnect(peer.Id, reason);
    }

    private void RegisterSpawn(Node root, int sceneIndex, Node parent, PeerId authority)
    {
        _collectBuffer.Clear();
        CollectNetworked(root, _collectBuffer, isRoot: true);

        var rootEntity = new NetworkEntity(this, _nextNetId, root, null)
        {
            Authority = authority,
            SceneIndex = sceneIndex,
        };
        if (parent.NetworkEntity is { Released: false } parentEntity && ReferenceEquals(parentEntity.Api, this))
            rootEntity.ParentNetId = parentEntity.NetId;
        else
            rootEntity.ParentPath = parent.GetPath().Path;

        var descendants = new NetworkEntity[_collectBuffer.Count - 1];
        for (var i = 1; i < _collectBuffer.Count; i++)
            descendants[i - 1] = new NetworkEntity(this, _nextNetId + (uint)i, _collectBuffer[i], rootEntity) { Authority = authority };
        rootEntity.Descendants = descendants;
        _nextNetId += (uint)_collectBuffer.Count;
        _collectBuffer.Clear();

        AddEntity(rootEntity);
        foreach (var descendant in descendants)
            AddEntity(descendant);
        _pendingSpawns.Add(rootEntity);
    }

    // ------------------------------------------------------------------------------------------------
    // Frame (server)
    // ------------------------------------------------------------------------------------------------

    private void ServerProcess(float delta)
    {
        ProcessRemovals();
        CheckPeerTimeouts();

        var step = 1.0 / TickRate;
        _tickAccumulator += delta;
        var ticked = false;
        if (_tickAccumulator + step * 1e-6 >= step)
        {
            var steps = (uint)Math.Max(1, Math.Floor((_tickAccumulator + step * 1e-6) / step));
            _tickAccumulator = Math.Max(0, _tickAccumulator - steps * step);
            if (_tickAccumulator > step)
                _tickAccumulator = 0; // a long stall: do not try to catch up
            _tick += steps;
            _bus!.Tick = _tick;
            CaptureAllAt(_tick);
            ticked = true;
        }

        FlushPendingSpawns();
        if (ticked)
            SendSnapshots();
    }

    /// <summary>Change detection for every spawned node; changes are stamped with <paramref name="tick"/>.</summary>
    internal void CaptureAllAt(uint tick)
    {
        foreach (var entity in _entityList)
        {
            if (!entity.Root.SpawnSent || entity.Node is not { } node)
                continue;
            foreach (var state in entity.States)
                state.Capture(node, tick);
        }
    }

    /// <summary>The server-side state of a client (tests, benchmarks).</summary>
    internal PeerState? PeerStateFor(PeerId peer) => _peersById.GetValueOrDefault(peer);

    // Spawns queued this frame go to every ready client, with the state they have now (stamped tick 0: already
    // delivered by the spawn, so snapshots do not resend it).
    private void FlushPendingSpawns()
    {
        if (_pendingSpawns.Count == 0 || _bus is null)
            return;

        _bus.Tick = _tick;
        for (var i = 0; i < _pendingSpawns.Count; i++)
        {
            var root = _pendingSpawns[i];
            if (root.Released)
                continue;
            CaptureForSpawn(root);
            foreach (var descendant in root.Descendants)
                CaptureForSpawn(descendant);
            root.SpawnSent = true;

            var message = new SpawnMessage { Entity = root };
            foreach (var peer in _readyPeers)
                SendTracked(peer, in message, NetChannel.Reliable);
        }

        _pendingSpawns.Clear();
    }

    private static void CaptureForSpawn(NetworkEntity entity)
    {
        if (entity.Node is not { } node)
            return;
        foreach (var state in entity.States)
            state.Capture(node, 0);
    }

    private void SendSnapshots()
    {
        _sendTimes[_tick % SendTimeSlots] = (_tick, _time);
        foreach (var peer in _peers)
        {
            if (!peer.Ready || peer.Closing)
                continue;
            var message = new SnapshotMessage { Api = this, Peer = peer };
            var before = _bus!.Stats.BytesSent;
            if (!_bus.Send(peer.Id, in message, NetChannel.Unreliable))
                continue;
            var size = _bus.Stats.BytesSent - before;
            peer.BytesSent += size;
            peer.SnapshotsSent++;
            peer.LastSnapshotBytes = size;
            _stats.Snapshots++;
            _stats.LastSnapshotBytes = (int)size;
        }
    }

    private void SendTracked<T>(PeerId peer, in T message, NetChannel channel) where T : struct, INetworkTransferable
    {
        var before = _bus!.Stats.BytesSent;
        if (_bus.Send(peer, in message, channel) && _peersById.TryGetValue(peer, out var state))
            state.BytesSent += _bus.Stats.BytesSent - before;
    }

    // Snapshot entry: netId (varint), byte length (ushort), then per state: change mask (varint) + changed values.
    // A 0 id ends the list. Entries are skippable, so a client that does not know an id yet can step over it.
    internal void WriteSnapshot(NetBufferWriter writer, PeerState peer)
    {
        var since = peer.AckedTick;
        foreach (var entity in _entityList)
        {
            if (entity.States.Length == 0 || !entity.Root.SpawnSent || entity.Node is null)
                continue;

            var changed = false;
            foreach (var state in entity.States)
            {
                if (state.ChangedSince(since) != 0)
                {
                    changed = true;
                    break;
                }
            }

            if (!changed)
                continue;

            writer.WriteVarUInt32(entity.NetId);
            var lengthAt = writer.Length;
            writer.Write((ushort)0);
            var start = writer.Length;
            foreach (var state in entity.States)
            {
                var mask = state.ChangedSince(since);
                writer.WriteVarUInt64(mask);
                state.Write(writer, mask);
            }

            var length = writer.Length - start;
            if (length > ushort.MaxValue)
                throw new InvalidOperationException($"Replicated state of {entity} is {length} bytes; at most {ushort.MaxValue} per node.");
            writer.PatchUInt16(lengthAt, (ushort)length);
        }

        writer.WriteVarUInt32(0);
    }

    // Spawn: root id, scene index, parent (networked id or path), root name, node count, then per node: present flag,
    // authority and the full state of every replicated member (descendants despawned since are marked absent).
    internal static void WriteSpawn(NetBufferWriter writer, NetworkEntity root)
    {
        writer.WriteVarUInt32(root.NetId);
        writer.WriteVarUInt32((uint)root.SceneIndex);
        if (root.ParentNetId != 0)
        {
            writer.Write((byte)1);
            writer.WriteVarUInt32(root.ParentNetId);
        }
        else
        {
            writer.Write((byte)0);
            writer.Write(root.ParentPath ?? string.Empty);
        }

        writer.Write(root.Node?.Name ?? string.Empty);
        writer.WriteVarUInt32((uint)(1 + root.Descendants.Length));
        WriteFullState(writer, root);
        foreach (var descendant in root.Descendants)
            WriteFullState(writer, descendant);
    }

    private static void WriteFullState(NetBufferWriter writer, NetworkEntity entity)
    {
        var present = !entity.Released && entity.Node is not null;
        writer.Write(present);
        if (!present)
            return;
        writer.Write((ulong)entity.Authority);
        foreach (var state in entity.States)
        {
            var mask = state.FullMask;
            writer.WriteVarUInt64(mask);
            state.Write(writer, mask);
        }
    }

    private void ServerDespawn(NetworkEntity entity)
    {
        var node = entity.Node;
        if (node is not null)
            NodeDespawned?.Invoke(node);

        var announce = entity.Root.SpawnSent && !entity.Root.Released;
        if (entity.IsRoot)
        {
            foreach (var descendant in entity.Descendants)
                ReleaseEntity(descendant);
            _pendingSpawns.Remove(entity);
        }

        ReleaseEntity(entity);
        _stats.Despawns++;
        if (!announce || _bus is null)
            return;
        var message = new DespawnMessage { NetId = entity.NetId };
        foreach (var peer in _readyPeers)
            SendTracked(peer, in message, NetChannel.Reliable);
    }

    private void CheckPeerTimeouts()
    {
        for (var i = _peers.Count - 1; i >= 0; i--)
        {
            var peer = _peers[i];
            if (peer.Closing)
                continue;
            if (!peer.Ready && _time - peer.ConnectedAt > HandshakeTimeout)
            {
                Log.Warning($"[Net] {peer.Id} did not complete the handshake in {HandshakeTimeout} s");
                Close(peer, DisconnectReason.Timeout);
            }
            else if (peer.Ready && _time - peer.LastReceived > PeerTimeout)
            {
                Log.Warning($"[Net] {peer.Id} timed out (silent for {PeerTimeout} s)");
                Close(peer, DisconnectReason.Timeout);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Messages (server)
    // ------------------------------------------------------------------------------------------------

    private void OnServerPeerConnected(PeerId peer)
    {
        var state = new PeerState(peer, _time);
        _peers.Add(state);
        _peersById[peer] = state;
    }

    private void OnServerPeerDisconnected(PeerId peer, DisconnectReason reason)
    {
        if (!_peersById.Remove(peer, out var state))
            return;
        _peers.Remove(state);
        if (!state.Ready)
            return;

        _readyPeers.Remove(peer);
        Log.Info($"[Net] {peer} left ({reason})");

        // Nodes the client owned: freed (despawned at the end of the frame) or handed back to the server.
        foreach (var entity in _entityList.ToArray())
        {
            if (entity.Released || entity.Authority != peer)
                continue;
            if (DespawnOwnedOnDisconnect && entity.IsRoot)
                entity.Node?.QueueFree();
            else
                SetAuthority(entity, ServerPeerId);
        }

        PeerLeft?.Invoke(peer, reason);
    }

    private void OnHello(in MessageContext context, in HelloMessage hello)
    {
        if (!_peersById.TryGetValue(context.Sender, out var peer) || peer.Ready || peer.Closing)
            return;
        peer.LastReceived = _time;

        if (hello.ReplicationFingerprint != ReplicationFingerprint)
        {
            Log.Warning($"[Net] refused {peer.Id}: replication fingerprint {hello.ReplicationFingerprint:X8} != {ReplicationFingerprint:X8} " +
                        "(different networked types or spawnable scenes)");
            Close(peer, DisconnectReason.ProtocolMismatch);
            return;
        }

        peer.Ready = true;
        peer.AckedTick = _tick; // late-join spawns carry everything captured so far
        _readyPeers.Add(peer.Id);
        _bus!.Tick = _tick;
        var welcome = new WelcomeMessage { PeerId = peer.Id, TickRate = (ushort)TickRate, Tick = _tick };
        SendTracked(peer.Id, in welcome, NetChannel.Reliable);

        // Late join: every node already spawned, in id order (parents before children).
        foreach (var entity in _entityList)
        {
            if (!entity.IsRoot || !entity.SpawnSent)
                continue;
            var message = new SpawnMessage { Entity = entity };
            SendTracked(peer.Id, in message, NetChannel.Reliable);
        }

        Log.Info($"[Net] {peer.Id} joined");
        PeerJoined?.Invoke(peer.Id);
    }

    private void OnAck(in MessageContext context, in AckMessage ack)
    {
        if (!_peersById.TryGetValue(context.Sender, out var peer) || !peer.Ready)
            return;
        peer.LastReceived = _time;
        if (ack.Tick <= peer.AckedTick || ack.Tick > _tick)
            return;
        peer.AckedTick = ack.Tick;

        var sent = _sendTimes[ack.Tick % SendTimeSlots];
        if (sent.Tick == ack.Tick)
        {
            var sample = _time - sent.Time;
            peer.RoundTripTime = peer.RoundTripTime <= 0 ? sample : peer.RoundTripTime + (sample - peer.RoundTripTime) * 0.125;
        }
    }
}
