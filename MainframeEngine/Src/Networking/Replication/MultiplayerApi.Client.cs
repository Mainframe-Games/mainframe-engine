namespace MainframeEngine.Networking;

public sealed partial class MultiplayerApi
{
    private readonly List<NetworkEntity> _interpolated = [];
    private readonly List<Node> _spawnBuffer = [];
    private PeerId _serverPeer;

    /// <summary>
    /// Client: sends a game message (registered in <see cref="Messages"/> on both ends) to the server — the server's peer
    /// on the client's transport is not <see cref="ServerPeerId"/>. False when not connected or the send failed.
    /// </summary>
    public bool SendToServer<T>(in T message, NetChannel channel = NetChannel.Reliable) where T : struct, INetworkTransferable =>
        Mode == MultiplayerMode.Client && _bus is not null && _bus.Send(_serverPeer, in message, channel);
    private bool _transportConnected;
    private bool _welcomed;
    private int _serverTickRate = DefaultTickRate;
    private uint _lastAppliedTick;
    private uint _lastUntruncatedTick;
    private uint _ackTick;
    private uint _maxSpawnedNetId;
    private double _serverTickEstimate;
    private bool _clockStarted;
    private double _ackTimer;
    private double _lastServerMessage;
    private double _clientStartedAt;
    private DisconnectReason _pendingDisconnectReason;

    /// <summary>Client: the server tick being shown by interpolated members (estimated server tick minus the delay).</summary>
    public double RenderTick => _serverTickEstimate - InterpolationDelay * _serverTickRate;

    /// <summary>Client: the estimated current server tick (smoothed from snapshot arrivals).</summary>
    public double EstimatedServerTick => _serverTickEstimate;

    /// <summary>Client: the server's snapshot rate (from the handshake).</summary>
    public int ServerTickRate => _serverTickRate;

    private void ResetClientState()
    {
        _serverPeer = default;
        _transportConnected = false;
        _welcomed = false;
        _serverTickRate = DefaultTickRate;
        _lastAppliedTick = 0;
        _lastUntruncatedTick = 0;
        _ackTick = 0;
        _maxSpawnedNetId = 0;
        _serverTickEstimate = 0;
        _clockStarted = false;
        _ackTimer = 0;
        _lastServerMessage = _time;
    }

    private void ClientProcess(float delta)
    {
        ProcessRemovals();

        // Lifecycle: the handshake must finish, and the server must keep talking (snapshots every tick).
        if (!_welcomed && _time - _clientStartedAt > HandshakeTimeout)
        {
            Log.Warning($"[Net] no answer from the server within {HandshakeTimeout} s");
            RequestStop(DisconnectReason.Timeout);
            return;
        }

        if (_welcomed && _time - _lastServerMessage > PeerTimeout)
        {
            Log.Warning($"[Net] server silent for {PeerTimeout} s");
            RequestStop(DisconnectReason.Timeout);
            return;
        }

        if (!_welcomed)
            return;

        // Snapshots are acknowledged as they are applied; without one for a tick interval the newest acknowledgement
        // is repeated as the heartbeat.
        _ackTimer += delta;
        var step = 1.0 / _serverTickRate;
        if (_ackTimer + step * 1e-6 >= step)
            SendAck();
    }

    private void SendAck()
    {
        _ackTimer = 0;
        var ack = new AckMessage { Tick = _ackTick };
        _bus?.Send(_serverPeer, in ack, NetChannel.Unreliable);
    }

    private void RequestStop(DisconnectReason reason)
    {
        _pendingDisconnectReason = reason;
        _stopRequested = true;
    }

    // After a disconnect (or timeout): free what the server spawned, report it, go offline.
    private void StopAfterDisconnect()
    {
        var reason = _pendingDisconnectReason;
        var wasClient = Mode == MultiplayerMode.Client;
        var neverJoined = !_welcomed;
        var candidates = _connectCandidates;
        var selector = _connectSelector;
        var next = _connectIndex + 1;
        Stop();

        // A connect string with more addresses: an attempt that never got through moves on to the next one.
        if (wasClient && neverJoined && candidates is not null && next < candidates.Count
            && reason is DisconnectReason.ConnectFailed or DisconnectReason.Timeout)
        {
            _connectCandidates = candidates;
            _connectSelector = selector;
            Log.Info($"[Net] '{candidates[next - 1]}' failed ({reason}); trying the next address");
            if (TryConnectFrom(next))
                return;
        }

        if (wasClient)
            Disconnected?.Invoke(reason);
    }

    private void AdvanceClock(float delta)
    {
        if (_clockStarted)
            _serverTickEstimate += delta * _serverTickRate;
    }

    // The clock follows snapshot arrivals: small errors are corrected gradually (no visible jumps), large ones
    // (a stall, a clock reset) snap.
    private void SyncClock(uint tick)
    {
        if (!_clockStarted)
        {
            _serverTickEstimate = tick;
            _clockStarted = true;
            return;
        }

        var error = tick - _serverTickEstimate;
        if (Math.Abs(error) > _serverTickRate)
        {
            _serverTickEstimate = tick;
            foreach (var entity in _interpolated)
                foreach (var state in entity.States)
                    if (state.HasInterpolation)
                        state.ClearInterpolation();
        }
        else
        {
            _serverTickEstimate += error * 0.1;
        }
    }

    private void ApplyInterpolation()
    {
        if (!Interpolation || !_clockStarted || _interpolated.Count == 0)
            return;

        var renderTick = RenderTick;
        var maxExtrapolation = MaxExtrapolation * _serverTickRate;
        foreach (var entity in _interpolated)
        {
            if (entity.Node is not { } node)
                continue;
            // Known unchanged up to the newest snapshot that carried the node, or that carried every changed node.
            var time = new InterpolationTime(renderTick, Math.Max(_lastUntruncatedTick, entity.LastAppliedTick), maxExtrapolation);
            foreach (var state in entity.States)
                if (state.HasInterpolation)
                    state.Interpolate(node, in time);
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Messages (client)
    // ------------------------------------------------------------------------------------------------

    private void OnClientConnected(PeerId server)
    {
        _serverPeer = server;
        _transportConnected = true;
        _lastServerMessage = _time;
        var hello = new HelloMessage { ReplicationFingerprint = ReplicationFingerprint };
        _bus!.Send(server, in hello);
    }

    private void OnClientDisconnected(PeerId server, DisconnectReason reason)
    {
        Log.Info(_transportConnected ? $"[Net] disconnected from the server ({reason})" : $"[Net] could not connect ({reason})");
        RequestStop(reason);
    }

    private void OnWelcome(in MessageContext context, in WelcomeMessage welcome)
    {
        if (_welcomed || context.Sender != _serverPeer)
            return;
        _welcomed = true;
        _lastServerMessage = _time;
        LocalPeerId = new PeerId(welcome.PeerId);
        _serverTickRate = Math.Max(1, (int)welcome.TickRate);
        _serverTickEstimate = welcome.Tick;
        _clockStarted = true;
        Log.Info($"[Net] joined the server as {LocalPeerId} ({_serverTickRate} Hz)");
        ConnectedToServer?.Invoke();
    }

    private void OnSpawn(in MessageContext context, in SpawnMessage message)
    {
        if (context.Sender != _serverPeer)
            return;
        _lastServerMessage = _time;
        var reader = message.Reader!;
        var netId = reader.ReadVarUInt32();
        var sceneIndex = reader.ReadVarUInt32();
        var custom = sceneIndex == unchecked((uint)CustomSceneIndex);
        string? spawnerPath = null;
        byte[]? payload = null;
        if (custom)
        {
            spawnerPath = reader.ReadString();
            NetCodec.Read(reader, out payload);
        }

        var parentKind = reader.ReadByte();
        var parentNetId = parentKind == 1 ? reader.ReadVarUInt32() : 0;
        var parentPath = parentKind == 0 ? reader.ReadString() : null;
        if (parentKind > 1)
            throw new InvalidDataException($"Unknown spawn parent kind {parentKind}.");
        var name = reader.ReadString();
        var count = reader.ReadVarUInt32();
        if (netId == 0 || count == 0 || count > (uint)reader.Remaining || netId > uint.MaxValue - (count - 1))
            throw new InvalidDataException($"Invalid spawn header (id {netId}, {count} nodes).");
        for (var id = netId; id - netId < count; id++)
        {
            if (_entities.ContainsKey(id))
                throw new InvalidDataException($"Spawn of #{netId}: id {id} is already in use.");
        }

        _maxSpawnedNetId = Math.Max(_maxSpawnedNetId, netId + count - 1);

        // A bind (MultiplayerApi.Bind): the node is this peer's own, at the same path as on the server.
        var bound = sceneIndex == unchecked((uint)BoundSceneIndex);
        MultiplayerSpawner? customSpawner = null;
        Node root;
        if (bound)
        {
            var bindParent = parentNetId != 0
                ? _entities.GetValueOrDefault(parentNetId)?.Node
                : parentPath is { Length: > 0 } ? _tree.Root.GetNodeOrNull(parentPath) : null;
            if (bindParent?.GetNodeOrNull(name) is not { NetworkEntity: null } existing)
            {
                Log.Error($"[Net] bind of #{netId}: '{parentPath}/{name}' does not exist here (or is already networked)");
                return;
            }

            root = existing;
        }
        else if (custom)
        {
            if (_tree.Root.GetNodeOrNull(spawnerPath!) is not MultiplayerSpawner { SpawnFunction: { } function } spawner)
            {
                Log.Error($"[Net] spawn of #{netId}: no MultiplayerSpawner with a spawn function at '{spawnerPath}' here");
                return;
            }

            customSpawner = spawner;
            root = function(SpawnData.Deserialize(payload!));
        }
        else if (sceneIndex >= (uint)_scenes.Count)
        {
            Log.Error($"[Net] spawn of #{netId}: scene index {sceneIndex} is not registered on this client");
            return;
        }
        else
        {
            root = _scenes[(int)sceneIndex].Instantiate();
        }

        NetworkEntity? rootEntity = null;
        try
        {
            if (name.Length > 0 && !bound)
                root.Name = name;

            _spawnBuffer.Clear();
            CollectNetworked(root, _spawnBuffer, isRoot: true);
            if (_spawnBuffer.Count != count)
            {
                Log.Error($"[Net] spawn of #{netId}: the scene has {_spawnBuffer.Count} networked nodes here but {count} on the server");
                if (!bound)
                    root.Free();
                return;
            }

            var context0 = new ReplicationReadContext(context.Header.Tick, context.Header.Tick, ApplyDirectly: true);
            var entities = new NetworkEntity[count];
            for (var i = 0; i < count; i++)
            {
                var node = _spawnBuffer[i];
                var entity = new NetworkEntity(this, netId + (uint)i, node, rootEntity) { LastAppliedTick = context.Header.Tick };
                rootEntity ??= entity;
                entities[i] = entity;
                if (!reader.ReadBoolean())
                {
                    if (i == 0)
                        throw new InvalidDataException($"Spawn of #{netId}: the root is marked absent.");
                    entity.Released = true; // despawned on the server before we joined
                    continue;
                }

                entity.Authority = new PeerId(reader.ReadUInt64());
                foreach (var state in entity.States)
                {
                    var mask = reader.ReadVarUInt64();
                    if ((mask & ~state.FullMask) != 0)
                        throw new InvalidDataException($"Spawn of #{entity.NetId}: invalid member mask.");
                    state.Read(reader, node, mask, in context0);
                }
            }

            rootEntity!.SceneIndex = bound ? BoundSceneIndex : custom ? CustomSceneIndex : (int)sceneIndex;
            rootEntity.ParentNetId = parentNetId;
            rootEntity.ParentPath = parentPath;
            rootEntity.Descendants = entities[1..];
            rootEntity.SpawnSent = true;

            var parent = bound ? null : customSpawner?.SpawnParent ?? ResolveSpawnParent(rootEntity);
            foreach (var entity in entities)
            {
                if (entity.Released)
                {
                    var gone = entity.Node;
                    entity.Node = null;
                    gone?.Free();
                    continue;
                }

                AddEntity(entity);
            }

            parent?.AddChild(root);
        }
        catch
        {
            if (rootEntity is not null)
                ReleaseSpawn(rootEntity);
            if (!bound && !root.IsFreed)
                root.Free();
            throw;
        }
        finally
        {
            _spawnBuffer.Clear();
        }

        _stats.Spawns++;
        NodeSpawned?.Invoke(root);
        customSpawner?.RaiseSpawned(root);
    }

    private Node ResolveSpawnParent(NetworkEntity root)
    {
        if (root.ParentNetId != 0)
        {
            if (_entities.TryGetValue(root.ParentNetId, out var parent) && parent.Node is { } parentNode)
                return parentNode;
            Log.Warning($"[Net] spawn of #{root.NetId}: parent #{root.ParentNetId} is unknown; adding it to the current scene");
        }
        else if (root.ParentPath is { Length: > 0 } path && _tree.Root.GetNodeOrNull(path) is { } byPath)
        {
            return byPath;
        }
        else
        {
            Log.Warning($"[Net] spawn of #{root.NetId}: parent '{root.ParentPath}' does not exist here; adding it to the current scene");
        }

        return _tree.CurrentScene ?? _tree.Root;
    }

    private void ReleaseSpawn(NetworkEntity root)
    {
        foreach (var descendant in root.Descendants)
            ReleaseEntity(descendant);
        ReleaseEntity(root);
    }

    private void OnDespawn(in MessageContext context, in DespawnMessage message)
    {
        if (context.Sender != _serverPeer)
            return;
        _lastServerMessage = _time;
        if (!_entities.TryGetValue(message.NetId, out var entity))
            return;

        var node = entity.Node;
        if (node is not null)
            NodeDespawned?.Invoke(node);
        if (entity.IsRoot)
            ReleaseSpawn(entity);
        else
            ReleaseEntity(entity);
        _stats.Despawns++;
        node?.QueueFree();
    }

    private void OnAuthority(in MessageContext context, in AuthorityMessage message)
    {
        if (context.Sender != _serverPeer)
            return;
        _lastServerMessage = _time;
        if (_entities.TryGetValue(message.NetId, out var entity))
            entity.Authority = new PeerId(message.Authority);
    }

    private void OnSnapshot(in MessageContext context, in SnapshotMessage message)
    {
        if (context.Sender != _serverPeer)
            return;
        _lastServerMessage = _time;
        if (!_welcomed)
            return;

        var tick = context.Header.Tick;
        if (_lastAppliedTick != 0 && tick <= _lastAppliedTick)
        {
            _stats.SnapshotsDiscarded++; // late, duplicated or reordered: a newer one already applied
            return;
        }

        var reader = message.Reader!;
        var start = reader.Position;
        var flags = reader.ReadByte();
        var truncated = (flags & 1) != 0;
        _lastAppliedTick = tick;
        var complete = true;

        while (true)
        {
            var netId = reader.ReadVarUInt32();
            if (netId == 0)
                break;
            var length = reader.ReadVarUInt32();
            if (length > (uint)reader.Remaining)
                throw new InvalidDataException($"Snapshot entry #{netId}: length {length} exceeds the {reader.Remaining} bytes left.");
            if (_entities.TryGetValue(netId, out var entity) && entity.Node is { } node)
            {
                // Members absent from the entry were unchanged up to what this node was last known at.
                var known = Math.Max(_lastUntruncatedTick, entity.LastAppliedTick);
                var readContext = new ReplicationReadContext(tick, known == 0 ? tick : known, ApplyDirectly: !Interpolation);
                var entryStart = reader.Position;
                foreach (var state in entity.States)
                {
                    var mask = reader.ReadVarUInt64();
                    if ((mask & ~state.FullMask) != 0)
                        throw new InvalidDataException($"Snapshot entry #{netId}: invalid member mask.");
                    state.Read(reader, node, mask, in readContext);
                }

                if (reader.Position - entryStart != length)
                    throw new InvalidDataException($"Snapshot entry #{netId}: {reader.Position - entryStart} bytes read, {length} declared.");
                entity.LastAppliedTick = tick;
            }
            else
            {
                reader.Skip((int)length);
                // An id above every spawn received: its spawn (reliable channel) has not arrived yet, so this snapshot
                // cannot be acknowledged (the server would stop resending those changes). Lower ids were despawned.
                if (netId > _maxSpawnedNetId)
                    complete = false;
            }
        }

        // A snapshot cut by the server's byte budget does not prove that absent nodes were still.
        if (!truncated)
            _lastUntruncatedTick = tick;
        _stats.Snapshots++;
        _stats.LastSnapshotBytes = reader.Position - start + MessageHeader.Size;
        SyncClock(tick);
        if (complete)
        {
            _ackTick = tick;
            SendAck();
        }
    }
}
