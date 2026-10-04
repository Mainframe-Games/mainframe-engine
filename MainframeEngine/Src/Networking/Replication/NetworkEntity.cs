namespace MainframeEngine.Networking;

/// <summary>
/// A node replicated by a <see cref="MultiplayerApi"/>: a spawned scene root or one of its networked descendants
/// (nodes whose type has <see cref="ReplicatedAttribute"/> members or <see cref="RpcAttribute"/> methods). A root and
/// its descendants get consecutive ids, assigned by the server in depth-first tree order, which both sides compute
/// from the same <see cref="PackedScene"/>.
/// </summary>
internal sealed class NetworkEntity
{
    public NetworkEntity(MultiplayerApi api, uint netId, Node node, NetworkEntity? root)
    {
        Api = api;
        NetId = netId;
        Node = node;
        Root = root ?? this;
        Chain = ReplicationRegistry.GetChain(node.GetType());
        States = Chain?.CreateStates() ?? [];
        HasInterpolation = Chain?.HasInterpolation ?? false;
    }

    public MultiplayerApi Api { get; }

    public uint NetId { get; }

    /// <summary>The node; null once it was freed (a client freeing a replicated node itself keeps the id known).</summary>
    public Node? Node { get; set; }

    /// <summary>The spawned scene root this node belongs to (itself for roots).</summary>
    public NetworkEntity Root { get; }

    public bool IsRoot => ReferenceEquals(Root, this);

    public PeerId Authority { get; set; }

    public ReplicationChain? Chain { get; }

    /// <summary>One generated state per declaring type with replicated members, base first.</summary>
    public ReplicatedState[] States { get; }

    public bool HasInterpolation { get; }

    // ---- roots ----

    /// <summary>Index of the spawned scene in <see cref="MultiplayerApi.SpawnableScenes"/>.</summary>
    public int SceneIndex { get; set; }

    /// <summary>The networked parent's id, or 0 when <see cref="ParentPath"/> names the parent.</summary>
    public uint ParentNetId { get; set; }

    /// <summary>Absolute path of a parent that is not networked (it must exist on clients too).</summary>
    public string? ParentPath { get; set; }

    /// <summary>The root's networked descendants, in id order (ids <c>NetId + 1</c>…).</summary>
    public NetworkEntity[] Descendants { get; set; } = [];

    // ---- server bookkeeping ----

    /// <summary>Spawn message sent (or included in late-join batches).</summary>
    public bool SpawnSent { get; set; }

    /// <summary>Left the tree this frame; despawned at the end of the frame unless it came back.</summary>
    public bool PendingRemoval { get; set; }

    /// <summary>Unregistered (despawned or orphaned).</summary>
    public bool Released { get; set; }

    public override string ToString() => $"#{NetId} {Node?.Name ?? "<freed>"}";
}

/// <summary>A client of the server-side <see cref="MultiplayerApi"/>.</summary>
internal sealed class PeerState(PeerId id, double connectedAt)
{
    public PeerId Id { get; } = id;

    /// <summary>Handshake done: receives spawns, snapshots and RPCs.</summary>
    public bool Ready { get; set; }

    public double ConnectedAt { get; } = connectedAt;

    public double LastReceived { get; set; } = connectedAt;

    /// <summary>Newest snapshot tick the client fully applied; snapshots carry everything changed after it.</summary>
    public uint AckedTick { get; set; }

    public long BytesSent { get; set; }

    public long SnapshotsSent { get; set; }

    public long LastSnapshotBytes { get; set; }

    /// <summary>Smoothed round-trip time in seconds (snapshot → acknowledgement); 0 until measured.</summary>
    public double RoundTripTime { get; set; }

    /// <summary>Kicked or refused: ignore its messages until the transport reports the disconnect.</summary>
    public bool Closing { get; set; }
}
