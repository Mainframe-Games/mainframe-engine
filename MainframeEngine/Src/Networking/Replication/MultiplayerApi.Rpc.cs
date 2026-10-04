namespace MainframeEngine.Networking;

public sealed partial class MultiplayerApi
{
    private static RpcCall Begin(Node node, RpcInfo rpc, PeerId target, bool targeted)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(rpc);

        // Not networked (single player, or not spawned): CallLocal methods still run here.
        if (node.NetworkEntity is not { Released: false, Node: not null } entity || entity.Api is not { _bus: not null } api)
            return new RpcCall(null, null, null, rpc, target, targeted, null, rpc.CallLocal && !targeted);

        if (!ReferenceEquals(entity.Chain?.GetRpc(rpc.WireIndex), rpc))
            throw new ArgumentException($"{rpc} is not an RPC of {node.GetType().Name}.", nameof(rpc));

        if (!api.MayCall(entity, rpc))
        {
            api._stats.RpcsRejected++;
            Log.Warning($"[Net] {rpc} on {node.Name} refused: {api.LocalPeerId} may not call a {rpc.Mode} RPC (authority {entity.Authority})");
            return new RpcCall(null, null, null, rpc, target, targeted, null, false);
        }

        var invokeLocally = rpc.CallLocal;
        var send = true;
        if (targeted)
        {
            invokeLocally = false;
            if (target == api.LocalPeerId)
            {
                // Targeting yourself runs the method here, without the network.
                invokeLocally = true;
                send = false;
            }
            else if (api.Mode == MultiplayerMode.Client && target != ServerPeerId)
            {
                api._stats.RpcsRejected++;
                Log.Warning($"[Net] {rpc}: a client can only call the server ({target} is another client)");
                send = false;
            }
            else if (api.Mode == MultiplayerMode.Server && !api._readyPeers.Contains(target))
            {
                Log.Warning($"[Net] {rpc}: {target} is not a connected client");
                send = false;
            }
        }
        else if (api.Mode == MultiplayerMode.Client && !api._welcomed)
        {
            send = false; // not connected yet: nowhere to send
        }

        if (!send)
            return new RpcCall(null, api, entity, rpc, target, targeted, null, invokeLocally);

        api._rpcArguments.Reset();
        return new RpcCall(api, api, entity, rpc, target, targeted, api._rpcArguments, invokeLocally);
    }

    /// <summary>Whether this peer may call <paramref name="rpc"/> on <paramref name="entity"/>. The server may call everything.</summary>
    private bool MayCall(NetworkEntity entity, RpcInfo rpc) => Mode == MultiplayerMode.Server || rpc.Mode switch
    {
        RpcMode.AnyPeer => true,
        RpcMode.Authority => entity.Authority == LocalPeerId,
        _ => false,
    };

    /// <summary>Whether a call from <paramref name="sender"/> (a client) to <paramref name="rpc"/> on <paramref name="entity"/> is allowed.</summary>
    private static bool MayReceive(NetworkEntity entity, RpcInfo rpc, PeerId sender) => rpc.Mode switch
    {
        RpcMode.AnyPeer => true,
        RpcMode.Authority => entity.Authority == sender,
        _ => false,
    };

    private void SendRpc(NetworkEntity entity, RpcInfo rpc, PeerId target, bool targeted)
    {
        if (_bus is null)
            return;

        // A node spawned this frame: its spawn must reach the clients first (same reliable channel, so ordered).
        if (Mode == MultiplayerMode.Server && !entity.Root.SpawnSent)
            FlushPendingSpawns();

        var message = new RpcMessage { NetId = entity.NetId, WireIndex = rpc.WireIndex, Arguments = _rpcArguments };
        var channel = rpc.Channel;
        if (Mode == MultiplayerMode.Client)
        {
            if (_bus.Send(_serverPeer, in message, channel))
                _stats.RpcsSent++;
        }
        else if (targeted)
        {
            SendTracked(target, in message, channel);
            _stats.RpcsSent++;
        }
        else
        {
            foreach (var peer in _readyPeers)
                SendTracked(peer, in message, channel);
            _stats.RpcsSent++;
        }
    }

    private void OnRpc(in MessageContext context, in RpcMessage message)
    {
        var sender = ServerPeerId;
        if (Mode == MultiplayerMode.Server)
        {
            if (!_peersById.TryGetValue(context.Sender, out var peer) || !peer.Ready || peer.Closing)
                return;
            peer.LastReceived = _time;
            sender = peer.Id;
        }
        else
        {
            if (context.Sender != _serverPeer)
                return;
            _lastServerMessage = _time;
        }

        if (!_entities.TryGetValue(message.NetId, out var entity) || entity.Node is not { } node)
            return; // despawned meanwhile (or, unreliable, not spawned yet)

        var rpc = entity.Chain?.GetRpc(message.WireIndex)
                  ?? throw new InvalidDataException($"RPC #{message.WireIndex} does not exist on {node.GetType().Name}.");

        if (Mode == MultiplayerMode.Server && !MayReceive(entity, rpc, sender))
        {
            _stats.RpcsRejected++;
            Log.Warning($"[Net] rejected {rpc} on {node.Name} from {sender}: {rpc.Mode} RPC, authority {entity.Authority}");
            RpcRejected?.Invoke(sender, node, rpc);
            return;
        }

        var previous = _remoteSender;
        _remoteSender = sender;
        try
        {
            rpc.Dispatch(node, message.Reader!);
        }
        catch (Exception e) when (Mode == MultiplayerMode.Server && e is not (EndOfStreamException or InvalidDataException or OutOfMemoryException))
        {
            // A client must never be able to take the server down: an RPC body (or a custom argument decoder) that
            // throws is logged and counted; malformed payloads still go to the bus as dropped packets.
            _stats.RpcsFailed++;
            Log.Error($"[Net] {rpc} on {node.Name} from {sender} threw: {e}");
            RpcFailed?.Invoke(sender, node, rpc, e);
            if (KickOnRpcFailure && _peersById.TryGetValue(sender, out var offender))
                Kick(offender.Id);
            return;
        }
        finally
        {
            _remoteSender = previous;
        }

        _stats.RpcsReceived++;
    }
}
