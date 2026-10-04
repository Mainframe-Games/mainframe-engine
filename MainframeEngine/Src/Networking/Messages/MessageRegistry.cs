namespace MainframeEngine.Networking;

/// <summary>
/// Assigns wire ids to message types. Server and client must register the same types with the same ids: register
/// them in the same order (ids 0, 1, 2…) or give explicit ids, then hand the registry to <see cref="MessageBus"/>.
/// </summary>
/// <remarks>
/// The first <see cref="MessageBus"/> built on a registry freezes it; registering afterwards throws.
/// <see cref="Fingerprint"/> hashes the protocol version with every (id, type name) pair; clients send it when
/// connecting and servers refuse a mismatch with <see cref="DisconnectReason.ProtocolMismatch"/>.
/// </remarks>
public sealed class MessageRegistry
{
    /// <summary>Highest id + 1. Dispatch indexes an array by id, so ids are kept small.</summary>
    public const int MaxMessages = 4096;

    private readonly Dictionary<Type, ushort> _ids = [];
    private Entry?[] _entries = new Entry?[16];
    private int      _slotCount;
    private ushort   _nextId;
    private uint     _fingerprint;

    internal readonly record struct Entry(Type Type, Func<MessageSlot> CreateSlot);

    /// <param name="protocolVersion">Written into every header; bump it when the meaning of messages changes.</param>
    public MessageRegistry(byte protocolVersion = 1) => ProtocolVersion = protocolVersion;

    /// <summary>Game protocol version, sent in every <see cref="MessageHeader"/>.</summary>
    public byte ProtocolVersion { get; }

    /// <summary>Number of registered message types.</summary>
    public int Count => _ids.Count;

    /// <summary>True once a bus uses the registry (or <see cref="Freeze"/> was called).</summary>
    public bool IsFrozen { get; private set; }

    /// <summary>Hash of the protocol version and every registered (id, type) pair. Freezes the registry.</summary>
    public uint Fingerprint
    {
        get
        {
            Freeze();
            return _fingerprint;
        }
    }

    /// <summary>One past the highest registered id.</summary>
    internal int SlotCount => _slotCount;

    /// <summary>Registers <typeparamref name="T"/> with the next free id (registration order) and returns it.</summary>
    public ushort Register<T>() where T : struct, INetworkTransferable
    {
        while (_nextId < MaxMessages && _nextId < _entries.Length && _entries[_nextId] is not null)
            _nextId++;
        return Register<T>(_nextId);
    }

    /// <summary>Registers <typeparamref name="T"/> with an explicit <paramref name="id"/>.</summary>
    /// <exception cref="InvalidOperationException">Frozen, the type is already registered, or the id is taken.</exception>
    public ushort Register<T>(ushort id) where T : struct, INetworkTransferable
    {
        if (IsFrozen)
            throw new InvalidOperationException("Message registry is frozen: register every message before creating a MessageBus.");
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(id, MaxMessages);

        var type = typeof(T);
        if (_ids.TryGetValue(type, out var existing))
            throw new InvalidOperationException($"{type.FullName} is already registered with id {existing}.");

        if (id >= _entries.Length)
            Array.Resize(ref _entries, Math.Min(MaxMessages, Math.Max(id + 1, _entries.Length * 2)));
        if (_entries[id] is { } taken)
            throw new InvalidOperationException($"Message id {id} is already used by {taken.Type.FullName}.");

        _entries[id] = new Entry(type, static () => new MessageSlot<T>());
        _ids.Add(type, id);
        _slotCount = Math.Max(_slotCount, id + 1);
        return id;
    }

    /// <summary>The id of <typeparamref name="T"/>, if registered.</summary>
    public bool TryGetId<T>(out ushort id) where T : struct, INetworkTransferable => _ids.TryGetValue(typeof(T), out id);

    /// <summary>The id of <typeparamref name="T"/>.</summary>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not registered.</exception>
    public ushort GetId<T>() where T : struct, INetworkTransferable =>
        _ids.TryGetValue(typeof(T), out var id)
            ? id
            : throw new InvalidOperationException($"{typeof(T).FullName} is not a registered message.");

    /// <summary>The type registered with <paramref name="id"/>, or null.</summary>
    public Type? GetMessageType(ushort id) => id < _entries.Length ? _entries[id]?.Type : null;

    /// <summary>Prevents further registration and computes <see cref="Fingerprint"/>. Idempotent.</summary>
    public void Freeze()
    {
        if (IsFrozen)
            return;
        IsFrozen     = true;
        _fingerprint = ComputeFingerprint();
    }

    internal MessageSlot? CreateSlot(ushort id) => id < _entries.Length ? _entries[id]?.CreateSlot() : null;

    // FNV-1a over the protocol version and each (id, full type name), in id order.
    private uint ComputeFingerprint()
    {
        const uint prime = 16777619;
        var hash = 2166136261;
        hash = (hash ^ ProtocolVersion) * prime;
        for (var id = 0; id < _slotCount; id++)
        {
            if (_entries[id] is not { } entry)
                continue;
            hash = (hash ^ (uint)(id & 0xFF)) * prime;
            hash = (hash ^ (uint)(id >> 8)) * prime;
            foreach (var c in entry.Type.FullName ?? entry.Type.Name)
                hash = (hash ^ c) * prime;
        }

        return hash;
    }
}
