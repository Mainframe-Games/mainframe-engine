namespace MainframeEngine.Networking;

public sealed partial class MultiplayerApi
{
    private readonly List<NetworkEntity> _interpolated = [];
    private readonly List<Node> _spawnBuffer = [];
    private PeerId _serverPeer;
    private bool _transportConnected;
    private bool _welcomed;
    private int _serverTickRate = DefaultTickRate;
    private uint _lastAppliedTick;
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

        // Acknowledge at the server's tick rate; doubles as the heartbeat.
        _ackTimer += delta;
        var step = 1.0 / _serverTickRate;
        if (_ackTimer + step * 1e-6 >= step)
        {
            _ackTimer = Math.Min(_ackTimer - step, step);
            var ack = new AckMessage { Tick = _ackTick };
            _bus!.Send(_serverPeer, in ack, NetChannel.Unreliable);
        }
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
        Stop();
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

        var time = new InterpolationTime(RenderTick, _lastAppliedTick, MaxExtrapolation * _serverTickRate);
        foreach (var entity in _interpolated)
        {
            if (entity.Node is not { } node)
                continue;
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
        _lastServerMessage = _time;
        var reader = message.Reader!;
        var netId = reader.ReadVarUInt32();
        var sceneIndex = reader.ReadVarUInt32();
        var parentKind = reader.ReadByte();
        var parentNetId = parentKind == 1 ? reader.ReadVarUInt32() : 0;
        var parentPath = parentKind == 0 ? reader.ReadString() : null;
        if (parentKind > 1)
            throw new InvalidDataException($"Unknown spawn parent kind {parentKind}.");
        var name = reader.ReadString();
        var count = reader.ReadVarUInt32();
        if (netId == 0 || count == 0 || count > (uint)reader.Remaining)
            throw new InvalidDataException($"Invalid spawn header (id {netId}, {count} nodes).");

        _maxSpawnedNetId = Math.Max(_maxSpawnedNetId, netId + count - 1);
        if (_entities.ContainsKey(netId))
        {
            Log.Warning($"[Net] duplicate spawn of #{netId} ignored");
            return;
        }

        if (sceneIndex >= (uint)_scenes.Count)
        {
            Log.Error($"[Net] spawn of #{netId}: scene index {sceneIndex} is not registered on this client");
            return;
        }

        var root = _scenes[(int)sceneIndex].Instantiate();
        NetworkEntity? rootEntity = null;
        try
        {
            if (name.Length > 0)
                root.Name = name;

            _spawnBuffer.Clear();
            CollectNetworked(root, _spawnBuffer, isRoot: true);
            if (_spawnBuffer.Count != count)
            {
                Log.Error($"[Net] spawn of #{netId}: the scene has {_spawnBuffer.Count} networked nodes here but {count} on the server");
                root.Free();
                return;
            }

            var context0 = new ReplicationReadContext(context.Header.Tick, context.Header.Tick, ApplyDirectly: true);
            var entities = new NetworkEntity[count];
            for (var i = 0; i < count; i++)
            {
                var node = _spawnBuffer[i];
                var entity = new NetworkEntity(this, netId + (uint)i, node, rootEntity);
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

            rootEntity!.SceneIndex = (int)sceneIndex;
            rootEntity.ParentNetId = parentNetId;
            rootEntity.ParentPath = parentPath;
            rootEntity.Descendants = entities[1..];
            rootEntity.SpawnSent = true;

            var parent = ResolveSpawnParent(rootEntity);
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

            parent.AddChild(root);
        }
        catch
        {
            if (rootEntity is not null)
                ReleaseSpawn(rootEntity);
            if (!root.IsFreed)
                root.Free();
            throw;
        }
        finally
        {
            _spawnBuffer.Clear();
        }

        _stats.Spawns++;
        NodeSpawned?.Invoke(root);
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
        _lastServerMessage = _time;
        if (_entities.TryGetValue(message.NetId, out var entity))
            entity.Authority = new PeerId(message.Authority);
    }

    private void OnSnapshot(in MessageContext context, in SnapshotMessage message)
    {
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
        var previous = _lastAppliedTick == 0 ? tick : _lastAppliedTick;
        _lastAppliedTick = tick;
        var readContext = new ReplicationReadContext(tick, previous, ApplyDirectly: !Interpolation);
        var complete = true;

        while (true)
        {
            var netId = reader.ReadVarUInt32();
            if (netId == 0)
                break;
            int length = reader.ReadUInt16();
            if (_entities.TryGetValue(netId, out var entity) && entity.Node is { } node)
            {
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
            }
            else
            {
                reader.Skip(length);
                // An id above every spawn received: its spawn (reliable channel) has not arrived yet, so this snapshot
                // cannot be acknowledged (the server would stop resending those changes). Lower ids were despawned.
                if (netId > _maxSpawnedNetId)
                    complete = false;
            }
        }

        if (complete)
            _ackTick = tick;
        _stats.Snapshots++;
        _stats.LastSnapshotBytes = reader.Position - start + MessageHeader.Size;
        SyncClock(tick);
    }
}
