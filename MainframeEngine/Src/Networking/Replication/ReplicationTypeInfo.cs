namespace MainframeEngine.Networking;

/// <summary>Decodes an RPC's arguments from <paramref name="reader"/> and invokes it on <paramref name="node"/> (generated).</summary>
public delegate void RpcDispatcher(Node node, NetBufferReader reader);

/// <summary>A <see cref="ReplicatedAttribute"/> member, as generated.</summary>
public sealed class ReplicatedPropertyInfo(string name, Type valueType, bool interpolate)
{
    public string Name { get; } = name ?? throw new ArgumentNullException(nameof(name));

    public Type ValueType { get; } = valueType ?? throw new ArgumentNullException(nameof(valueType));

    /// <summary><see cref="ReplicatedAttribute.Interpolate"/>.</summary>
    public bool Interpolate { get; } = interpolate;

    public override string ToString() => $"{Name}: {ValueType.Name}";
}

/// <summary>An <see cref="RpcAttribute"/> method, with its generated dispatcher.</summary>
public sealed class RpcInfo
{
    private readonly RpcDispatcher _dispatch;

    public RpcInfo(string name, RpcMode mode, bool reliable, bool callLocal, Type[] parameterTypes, RpcDispatcher dispatch)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(parameterTypes);
        ArgumentNullException.ThrowIfNull(dispatch);
        Name = name;
        Mode = mode;
        Reliable = reliable;
        CallLocal = callLocal;
        ParameterTypes = parameterTypes;
        _dispatch = dispatch;
    }

    public string Name { get; }

    public RpcMode Mode { get; }

    /// <summary>Sent on <see cref="NetChannel.Reliable"/> (true) or <see cref="NetChannel.Unreliable"/>.</summary>
    public bool Reliable { get; }

    /// <summary>Also invoked on the caller.</summary>
    public bool CallLocal { get; }

    public IReadOnlyList<Type> ParameterTypes { get; }

    /// <summary>The type that declares the method.</summary>
    public ReplicationTypeInfo DeclaringType { get; internal set; } = null!;

    /// <summary>Position among <see cref="DeclaringType"/>'s own RPCs.</summary>
    public int LocalIndex { get; internal set; }

    /// <summary>The id sent on the wire: base types' RPCs first, then this type's in declaration order.</summary>
    public int WireIndex => DeclaringType.RpcOffset + LocalIndex;

    internal NetChannel Channel => Reliable ? NetChannel.Reliable : NetChannel.Unreliable;

    internal void Dispatch(Node node, NetBufferReader reader) => _dispatch(node, reader);

    public override string ToString() => $"{DeclaringType?.Type.Name}.{Name}";
}

/// <summary>
/// The networking shape of one node type: its own <see cref="ReplicatedAttribute"/> members and
/// <see cref="RpcAttribute"/> methods (base types have their own info), a factory for its generated
/// <see cref="ReplicatedState"/> and a schema hash. Emitted by <c>MainframeEngine.Generators</c> and registered with
/// <see cref="ReplicationRegistry"/> from a module initializer.
/// </summary>
public sealed class ReplicationTypeInfo
{
    private readonly Func<ReplicatedState>? _createState;
    private int _rpcOffset = -1;

    public ReplicationTypeInfo(
        Type type,
        uint schemaHash,
        ReplicatedPropertyInfo[] properties,
        RpcInfo[] rpcs,
        Func<ReplicatedState>? createState)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(rpcs);
        if (properties.Length > 64)
            throw new ArgumentException("A type may declare at most 64 replicated members.", nameof(properties));
        if (properties.Length > 0 && createState is null)
            throw new ArgumentException("Types with replicated members need a state factory.", nameof(createState));

        Type = type;
        SchemaHash = schemaHash;
        Properties = properties;
        Rpcs = rpcs;
        _createState = properties.Length > 0 ? createState : null;
        for (var i = 0; i < rpcs.Length; i++)
        {
            rpcs[i].DeclaringType = this;
            rpcs[i].LocalIndex = i;
        }
    }

    public Type Type { get; }

    /// <summary>Hash of the member names, types and RPC signatures (part of <see cref="ReplicationRegistry.Fingerprint"/>).</summary>
    public uint SchemaHash { get; }

    /// <summary>Members declared on this type, in declaration order.</summary>
    public IReadOnlyList<ReplicatedPropertyInfo> Properties { get; }

    /// <summary>RPCs declared on this type, in declaration order.</summary>
    public IReadOnlyList<RpcInfo> Rpcs { get; }

    /// <summary>True when the type declares replicated members (and so has a state).</summary>
    public bool HasState => _createState is not null;

    /// <summary>The nearest base type with replication info, if any.</summary>
    public ReplicationTypeInfo? Base => Type.BaseType is { } baseType ? ReplicationRegistry.GetNearest(baseType) : null;

    /// <summary>Number of RPCs declared by base types (their wire ids come first).</summary>
    public int RpcOffset
    {
        get
        {
            if (_rpcOffset >= 0)
                return _rpcOffset;
            var offset = 0;
            for (var b = Base; b is not null; b = b.Base)
                offset += b.Rpcs.Count;
            return _rpcOffset = offset;
        }
    }

    internal ReplicatedState CreateState() =>
        _createState?.Invoke() ?? throw new InvalidOperationException($"{Type.Name} has no replicated members.");

    public override string ToString() => $"{Type.Name} ({Properties.Count} replicated, {Rpcs.Count} RPCs)";
}
