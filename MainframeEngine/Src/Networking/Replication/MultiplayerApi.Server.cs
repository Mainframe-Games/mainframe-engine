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
    private readonly Stack<int> _freeSlots = new();
    private readonly NetBufferWriter _entryWriter = new(256);
    private readonly (uint Tick, double Time)[] _sendTimes = new (uint, double)[SendTimeSlots];
    private uint _tick = 1;
    private double _tickAccumulator;
    private uint _nextNetId = 1;
    private int _slotCapacity = 64;
    private int _nextSlot;

    /// <summary>
    /// Server: the most bytes one snapshot may carry (0, the default: unlimited). When the changed nodes do not fit,
    /// the rest go out in the following ticks (each snapshot continues where the previous one stopped), so a burst of
    /// changes is spread instead of sent as one oversized, fragmented packet. Use about 1200 for internet play.
    /// </summary>
    public int MaxSnapshotBytes
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            field = value;
        }
    }

    /// <summary>Server: clients that completed the handshake (and are not being disconnected), in join order. Valid until the next frame.</summary>
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
    /// <param name="authority">The peer with authority over the new nodes: the server (default) or a connected client.</param>
    public Node Spawn(PackedScene scene, Node? parent = null, PeerId authority = default)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ThrowIfDisposed();
        if (Mode != MultiplayerMode.Server)
            throw new InvalidOperationException("Only a running server can spawn networked scenes.");
        var index = _scenes.IndexOf(scene);
        if (index < 0)
            throw new InvalidOperationException("The scene is not registered; call RegisterScene on the server and on every client.");
        ValidateAuthority(authority);
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

    /// <summary>The spawn message's scene index for a <see cref="Bind"/>: the node already exists on every peer.</summary>
    internal const int BoundSceneIndex = -1;

    /// <summary>
    /// Server: networks a node that every peer already has at the same path (a node of the loaded scene — Godot's
    /// <c>MultiplayerSynchronizer</c> on a scene node). It and its networked descendants get ids as a spawn would, and
    /// clients bind their own node at that path instead of instantiating one; late joiners get it like any spawn. Freeing
    /// it despawns it (clients free theirs). A node that is already networked is left alone.
    /// </summary>
    public void Bind(Node root, PeerId authority = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ThrowIfDisposed();
        if (Mode != MultiplayerMode.Server)
            throw new InvalidOperationException("Only a running server can bind scene nodes.");
        if (root.NetworkEntity is not null)
            return;
        if (!ReferenceEquals(root.Tree, _tree) || root.Parent is not { } parent)
            throw new ArgumentException("The node must be inside this multiplayer API's tree, below the root.", nameof(root));
        ValidateAuthority(authority);
        RegisterSpawn(root, BoundSceneIndex, parent, authority);
        _stats.Spawns++;
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
    /// Server: gives <paramref name="peer"/> (the server or a connected client) authority over <paramref name="node"/>
    /// (and, by default, its networked descendants) and tells the clients.
    /// </summary>
    public void SetAuthority(Node node, PeerId peer, bool includeDescendants = true)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Mode != MultiplayerMode.Server)
            throw new InvalidOperationException("Only the server assigns authority.");
        if (node.NetworkEntity is not { } entity || !ReferenceEquals(entity.Api, this) || entity.Released)
            throw new ArgumentException("The node is not networked by this multiplayer API.", nameof(node));
        ValidateAuthority(peer);

        SetAuthority(entity, peer);
        if (includeDescendants && entity.IsRoot)
            foreach (var descendant in entity.Descendants)
                if (!descendant.Released)
                    SetAuthority(descendant, peer);
    }

    private void ValidateAuthority(PeerId peer)
    {
        if (peer != ServerPeerId && !_readyPeers.Contains(peer))
            throw new ArgumentException($"{peer} is not a connected client (authority must be the server or a peer that joined).", nameof(peer));
    }

    private void SetAuthority(NetworkEntity entity, PeerId peer)
    {
        if (entity.Authority == peer)
            return;
        entity.Authority = peer;
        if (!entity.Root.SpawnSent)
            return; // the spawn will carry it
        var message = new AuthorityMessage { NetId = entity.NetId, Authority = peer };
        SendReliableToReady(in message);
    }

    /// <summary>Server: disconnects a client (<see cref="DisconnectReason.Kicked"/> unless another reason is given).</summary>
    public void Kick(PeerId peer, DisconnectReason reason = DisconnectReason.Kicked)
    {
        if (Mode != MultiplayerMode.Server || !_peersById.TryGetValue(peer, out var state) || state.Closing)
            return;
        Log.Info($"[Net] kicking {peer} ({reason})");
        Close(state, reason);
    }

    // Stops all traffic to the peer at once (it leaves ConnectedPeers); PeerLeft follows the transport's disconnect.
    private void Close(PeerState peer, DisconnectReason reason)
    {
        if (peer.Closing)
            return;
        peer.Closing = true;
        _readyPeers.Remove(peer.Id);
        _bus?.Disconnect(peer.Id, reason);
    }

    private void RegisterSpawn(Node root, int sceneIndex, Node parent, PeerId authority)
    {
        _collectBuffer.Clear();
        CollectNetworked(root, _collectBuffer, isRoot: true);
        if (_nextNetId > uint.MaxValue - (uint)_collectBuffer.Count)
            throw new InvalidOperationException("Network ids exhausted.");

        var rootEntity = new NetworkEntity(this, _nextNetId, root, null)
        {
            Authority = authority,
            SceneIndex = sceneIndex,
            Slot = AllocateSlot(),
        };
        if (parent.NetworkEntity is { Released: false } parentEntity && ReferenceEquals(parentEntity.Api, this))
            rootEntity.ParentNetId = parentEntity.NetId;
        else
            rootEntity.ParentPath = parent.GetPath().Path;

        var descendants = new NetworkEntity[_collectBuffer.Count - 1];
        for (var i = 1; i < _collectBuffer.Count; i++)
            descendants[i - 1] = new NetworkEntity(this, _nextNetId + (uint)i, _collectBuffer[i], rootEntity) { Authority = authority, Slot = AllocateSlot() };
        rootEntity.Descendants = descendants;
        _nextNetId += (uint)_collectBuffer.Count;
        _collectBuffer.Clear();

        AddEntity(rootEntity);
        foreach (var descendant in descendants)
            AddEntity(descendant);
        _pendingSpawns.Add(rootEntity);
    }

    // Dense per-entity index into every client's acknowledgement array (reused after despawn).
    private int AllocateSlot()
    {
        var slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : _nextSlot++;
        if (slot >= _slotCapacity)
        {
            _slotCapacity = Math.Max(slot + 1, _slotCapacity * 2);
            foreach (var peer in _peers)
                peer.EnsureSlots(_slotCapacity);
        }

        return slot;
    }

    private void FreeSlot(NetworkEntity entity)
    {
        if (entity.Slot < 0)
            return;
        _freeSlots.Push(entity.Slot);
        entity.Slot = -1;
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
    // delivered by the spawn, so snapshots do not resend it; clients' acknowledgements for the new nodes start now).
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
            PrepareSpawn(root);
            foreach (var descendant in root.Descendants)
                PrepareSpawn(descendant);
            root.SpawnSent = true;

            var message = new SpawnMessage { Entity = root };
            SendReliableToReady(in message);
        }

        _pendingSpawns.Clear();
    }

    private void PrepareSpawn(NetworkEntity entity)
    {
        if (entity.Node is not { } node)
            return;
        foreach (var state in entity.States)
            state.Capture(node, 0);
        foreach (var peer in _peers)
            peer.EntityAcks[entity.Slot] = _tick;
    }

    private void SendSnapshots()
    {
        _sendTimes[_tick % SendTimeSlots] = (_tick, _time);
        for (var i = 0; i < _peers.Count; i++)
        {
            var peer = _peers[i];
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

    private bool SendTracked<T>(PeerId peer, in T message, NetChannel channel) where T : struct, INetworkTransferable
    {
        var before = _bus!.Stats.BytesSent;
        if (!_bus.Send(peer, in message, channel))
            return false;
        if (_peersById.TryGetValue(peer, out var state))
            state.BytesSent += _bus.Stats.BytesSent - before;
        return true;
    }

    // Control messages (spawn, despawn, authority, welcome) must arrive: a client that missed one would diverge (and
    // stop acknowledging), so a refused send disconnects it.
    private bool SendReliable<T>(PeerId peer, in T message) where T : struct, INetworkTransferable
    {
        if (SendTracked(peer, in message, NetChannel.Reliable))
            return true;
        if (_peersById.TryGetValue(peer, out var state))
        {
            Log.Warning($"[Net] {peer} refused a reliable {typeof(T).Name}; disconnecting it");
            Close(state, DisconnectReason.Closed);
        }

        return false;
    }

    private void SendReliableToReady<T>(in T message) where T : struct, INetworkTransferable
    {
        // Index loop from the end: a failed send closes the peer, which removes it from the list.
        for (var i = _readyPeers.Count - 1; i >= 0; i--)
        {
            if (i < _readyPeers.Count)
                SendReliable(_readyPeers[i], in message);
        }
    }

    // Snapshot: flags (bit 0: truncated by MaxSnapshotBytes), then entries — netId (varint), byte length (varint), per
    // state: change mask (varint) + changed values — ending with id 0. Each node sends what changed after *that node's*
    // last acknowledged tick for this client; the ticks a snapshot covered are recorded so the acknowledgement advances
    // exactly the nodes it carried. Entries are skippable (a client may not know an id yet).
    internal void WriteSnapshot(NetBufferWriter writer, PeerState peer)
    {
        var flagsAt = writer.Length;
        writer.Write((byte)0);
        var included = peer.BeginSnapshot(_tick);
        var budget = MaxSnapshotBytes;
        var count = _entityList.Count;
        var start = count == 0 ? 0 : peer.SnapshotCursor % count;
        var truncated = false;

        for (var k = 0; k < count; k++)
        {
            var index = (start + k) % count;
            var entity = _entityList[index];
            if (entity.States.Length == 0 || !entity.Root.SpawnSent || entity.Node is null)
                continue;

            var since = peer.EntityAcks[entity.Slot];
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

            _entryWriter.Reset();
            foreach (var state in entity.States)
            {
                var mask = state.ChangedSince(since);
                _entryWriter.WriteVarUInt64(mask);
                state.Write(_entryWriter, mask);
            }

            var length = _entryWriter.Length;
            if (budget > 0 && included.Count > 0 && writer.Length + VarIntSize(entity.NetId) + VarIntSize((uint)length) + length + 1 > budget)
            {
                truncated = true;
                peer.SnapshotCursor = index; // continue here next tick
                break;
            }

            writer.WriteVarUInt32(entity.NetId);
            writer.WriteVarUInt32((uint)length);
            writer.Write(_entryWriter.WrittenSpan);
            included.Add(entity.Slot);
        }

        writer.WriteVarUInt32(0);
        if (truncated)
            writer.PatchByte(flagsAt, 1);
        else
            peer.SnapshotCursor = 0;
    }

    private static int VarIntSize(uint value) => value < 1u << 7 ? 1 : value < 1u << 14 ? 2 : value < 1u << 21 ? 3 : value < 1u << 28 ? 4 : 5;

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
        if (entity.Released)
            return; // a handler stopped the API (or freed it some other way)

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
        SendReliableToReady(in message);
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
            else if (peer.Ready && _time - peer.LastAckProgress > PeerTimeout)
            {
                // Still talking, but not acknowledging new snapshots: it cannot apply them (it would only ever
                // receive larger and larger catch-up snapshots), so it is dropped rather than kept half-synchronized.
                Log.Warning($"[Net] {peer.Id} stopped acknowledging snapshots (newest {peer.AckedTick}, server at {_tick})");
                Close(peer, DisconnectReason.Timeout);
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Messages (server)
    // ------------------------------------------------------------------------------------------------

    private void OnServerPeerConnected(PeerId peer)
    {
        var state = new PeerState(peer, _time, _slotCapacity);
        _peers.Add(state);
        _peersById[peer] = state;
    }

    private void OnServerPeerDisconnected(PeerId peer, DisconnectReason reason)
    {
        if (!_peersById.Remove(peer, out var state))
            return;
        _peers.Remove(state);
        _readyPeers.Remove(peer);
        if (!state.Ready)
            return;

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
        peer.LastAckProgress = _time;
        peer.AckedTick = _tick;
        // Late join: every spawned node's state as captured so far travels in its spawn.
        foreach (var entity in _entityList)
            if (entity.Slot >= 0)
                peer.EntityAcks[entity.Slot] = _tick;
        _readyPeers.Add(peer.Id);
        _bus!.Tick = _tick;
        var welcome = new WelcomeMessage { PeerId = peer.Id, TickRate = (ushort)TickRate, Tick = _tick };
        if (!SendReliable(peer.Id, in welcome))
            return;

        // Every node already spawned, in id order (parents before children).
        foreach (var entity in _entityList)
        {
            if (!entity.IsRoot || !entity.SpawnSent)
                continue;
            var message = new SpawnMessage { Entity = entity };
            if (!SendReliable(peer.Id, in message))
                return;
        }

        Log.Info($"[Net] {peer.Id} joined");
        PeerJoined?.Invoke(peer.Id);
    }

    private void OnAck(in MessageContext context, in AckMessage ack)
    {
        if (!_peersById.TryGetValue(context.Sender, out var peer) || !peer.Ready || peer.Closing)
            return;
        peer.LastReceived = _time;
        if (ack.Tick > _tick || !peer.Acknowledge(ack.Tick))
            return;

        if (ack.Tick > peer.AckedTick)
        {
            peer.AckedTick = ack.Tick;
            peer.LastAckProgress = _time;
        }

        var sent = _sendTimes[ack.Tick % SendTimeSlots];
        if (sent.Tick == ack.Tick)
        {
            var sample = _time - sent.Time;
            peer.RoundTripTime = peer.RoundTripTime <= 0 ? sample : peer.RoundTripTime + (sample - peer.RoundTripTime) * 0.125;
        }
    }
}
