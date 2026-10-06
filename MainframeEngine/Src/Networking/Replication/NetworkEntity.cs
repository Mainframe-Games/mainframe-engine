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

    /// <summary>Server: index into every client's acknowledgement array (-1 once released).</summary>
    public int Slot { get; set; } = -1;

    /// <summary>Client: the newest snapshot (or spawn) tick that carried this node.</summary>
    public uint LastAppliedTick { get; set; }

    // ---- roots ----

    /// <summary>Index of the spawned scene in <see cref="MultiplayerApi.SpawnableScenes"/>.</summary>
    public int SceneIndex { get; set; }

    /// <summary>A <see cref="MultiplayerSpawner"/> spawn: the spawner's path and the serialized <see cref="SpawnData"/>.</summary>
    public string? SpawnerPath { get; set; }

    public byte[]? SpawnPayload { get; set; }

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
internal sealed class PeerState
{
    /// <summary>Snapshots remembered for acknowledgement (about two seconds at 30 Hz).</summary>
    public const int SentSnapshotSlots = 64;

    private readonly uint[] _sentTicks = new uint[SentSnapshotSlots];
    private readonly List<int>[] _sentEntries = new List<int>[SentSnapshotSlots];
    private uint _lastAcknowledged;

    public PeerState(PeerId id, double connectedAt, int slotCapacity)
    {
        Id = id;
        ConnectedAt = connectedAt;
        LastReceived = connectedAt;
        LastAckProgress = connectedAt;
        EntityAcks = new uint[slotCapacity];
        for (var i = 0; i < SentSnapshotSlots; i++)
            _sentEntries[i] = new List<int>(slotCapacity);
    }

    public PeerId Id { get; }

    /// <summary>Handshake done: receives spawns, snapshots and RPCs.</summary>
    public bool Ready { get; set; }

    public double ConnectedAt { get; }

    public double LastReceived { get; set; }

    /// <summary>When an acknowledgement last moved <see cref="AckedTick"/> forward.</summary>
    public double LastAckProgress { get; set; }

    /// <summary>Newest snapshot tick the client fully applied.</summary>
    public uint AckedTick { get; set; }

    /// <summary>
    /// Per networked node (by <see cref="NetworkEntity.Slot"/>): the newest acknowledged tick of a snapshot that carried
    /// it. A snapshot sends a node's members changed after this tick.
    /// </summary>
    public uint[] EntityAcks { get; private set; }

    /// <summary>Where the next snapshot starts when the previous one was cut by the byte budget.</summary>
    public int SnapshotCursor { get; set; }

    public long BytesSent { get; set; }

    public long SnapshotsSent { get; set; }

    public long LastSnapshotBytes { get; set; }

    /// <summary>Smoothed round-trip time in seconds (snapshot → acknowledgement); 0 until measured.</summary>
    public double RoundTripTime { get; set; }

    /// <summary>Kicked or refused: ignore its messages until the transport reports the disconnect.</summary>
    public bool Closing { get; set; }

    /// <summary>Grows the per-node arrays (when nodes are spawned), so recording snapshots never allocates.</summary>
    public void EnsureSlots(int capacity)
    {
        if (EntityAcks.Length >= capacity)
            return;
        var acks = EntityAcks;
        Array.Resize(ref acks, capacity);
        EntityAcks = acks;
        foreach (var entries in _sentEntries)
            entries.Capacity = Math.Max(entries.Capacity, capacity);
    }

    /// <summary>Starts recording the nodes carried by the snapshot of <paramref name="tick"/>.</summary>
    public List<int> BeginSnapshot(uint tick)
    {
        var index = (int)(tick % SentSnapshotSlots);
        _sentTicks[index] = tick;
        var entries = _sentEntries[index];
        entries.Clear();
        return entries;
    }

    /// <summary>
    /// The client fully applied the snapshot of <paramref name="tick"/>: every node it carried is acknowledged up to that
    /// tick. False for ticks no longer remembered (too old) or already processed.
    /// </summary>
    public bool Acknowledge(uint tick)
    {
        var index = (int)(tick % SentSnapshotSlots);
        if (_sentTicks[index] != tick || tick == _lastAcknowledged)
            return false;
        _lastAcknowledged = tick;
        var acks = EntityAcks;
        foreach (var slot in _sentEntries[index])
        {
            if ((uint)slot < (uint)acks.Length && acks[slot] < tick)
                acks[slot] = tick;
        }

        return true;
    }
}
