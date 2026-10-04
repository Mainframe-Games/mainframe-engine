using System.Reflection;

namespace MainframeEngine.Networking;

/// <summary>
/// The <see cref="ReplicationTypeInfo"/> of every loaded node type with <see cref="ReplicatedAttribute"/> members or
/// <see cref="RpcAttribute"/> methods. Generated module initializers register them when their assembly loads.
/// </summary>
public static class ReplicationRegistry
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<Type, ReplicationTypeInfo> ByType = [];
    private static readonly Dictionary<Type, ReplicationChain?> Chains = [];

    /// <summary>Registers <paramref name="info"/> (a reloaded assembly's type replaces the old one).</summary>
    public static void Register(ReplicationTypeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (Gate)
        {
            ByType[info.Type] = info;
            Chains.Clear();
        }
    }

    /// <summary>Removes every type declared in <paramref name="assembly"/> (editor code reload).</summary>
    public static void UnregisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Gate)
        {
            foreach (var type in ByType.Keys.Where(t => t.Assembly == assembly).ToArray())
                ByType.Remove(type);
            Chains.Clear();
        }
    }

    /// <summary>The info generated for exactly <paramref name="type"/>, or null.</summary>
    public static ReplicationTypeInfo? Get(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        Serialization.TypeRegistry.EnsureRegistered(type.Assembly);
        lock (Gate)
            return ByType.GetValueOrDefault(type);
    }

    /// <summary>The info of <paramref name="type"/> or its nearest base with one, or null.</summary>
    public static ReplicationTypeInfo? GetNearest(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            if (Get(t) is { } info)
                return info;
        return null;
    }

    /// <summary>
    /// Every replication info of <paramref name="type"/> and its bases, base first; null when the type has no
    /// networked members at all. Cached per type.
    /// </summary>
    internal static ReplicationChain? GetChain(Type type)
    {
        lock (Gate)
        {
            if (Chains.TryGetValue(type, out var cached))
                return cached;
        }

        var infos = new List<ReplicationTypeInfo>();
        for (var info = GetNearest(type); info is not null; info = info.Base)
            infos.Insert(0, info);
        var chain = infos.Count == 0 ? null : new ReplicationChain([.. infos]);

        lock (Gate)
            Chains[type] = chain;
        return chain;
    }

    /// <summary>Every registered info (a snapshot).</summary>
    public static IReadOnlyList<ReplicationTypeInfo> All
    {
        get
        {
            lock (Gate)
                return [.. ByType.Values];
        }
    }

    /// <summary>
    /// Hash of every registered type's full name and schema, in name order. Both ends of a connection must agree
    /// (see <see cref="MultiplayerApi"/>'s handshake). Loaded assemblies that reference the engine are initialized
    /// first, so types register even if their assembly has not been touched yet.
    /// </summary>
    public static uint Fingerprint
    {
        get
        {
            InitializeLoadedAssemblies();
            ReplicationTypeInfo[] all;
            lock (Gate)
                all = [.. ByType.Values];
            Array.Sort(all, static (a, b) => string.CompareOrdinal(a.Type.FullName, b.Type.FullName));

            var hash = Fnv.Offset;
            foreach (var info in all)
            {
                hash = Fnv.Add(hash, info.Type.FullName ?? info.Type.Name);
                hash = Fnv.Add(hash, info.SchemaHash);
            }

            return hash;
        }
    }

    private static void InitializeLoadedAssemblies()
    {
        var engine = typeof(Node).Assembly.GetName().Name;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (assembly.IsDynamic)
                continue;
            if (assembly == typeof(Node).Assembly
                || assembly.GetReferencedAssemblies().Any(a => string.Equals(a.Name, engine, StringComparison.Ordinal)))
                Serialization.TypeRegistry.EnsureRegistered(assembly);
        }
    }
}

/// <summary>FNV-1a helpers for protocol fingerprints.</summary>
internal static class Fnv
{
    public const uint Offset = 2166136261;
    private const uint Prime = 16777619;

    public static uint Add(uint hash, string text)
    {
        foreach (var c in text)
            hash = (hash ^ c) * Prime;
        return (hash ^ 0xFF) * Prime; // separator
    }

    public static uint Add(uint hash, uint value)
    {
        for (var i = 0; i < 4; i++)
        {
            hash = (hash ^ (value & 0xFF)) * Prime;
            value >>= 8;
        }

        return hash;
    }
}

/// <summary>The replication infos of one concrete node type, base first, with the flattened RPC table.</summary>
internal sealed class ReplicationChain
{
    private readonly RpcInfo[] _rpcs;

    public ReplicationChain(ReplicationTypeInfo[] infos)
    {
        Infos = infos;
        var rpcs = new List<RpcInfo>();
        var states = 0;
        var interpolates = false;
        foreach (var info in infos)
        {
            rpcs.AddRange(info.Rpcs);
            if (info.HasState)
            {
                states++;
                interpolates |= info.Properties.Any(p => p.Interpolate);
            }
        }

        _rpcs = [.. rpcs];
        StateCount = states;
        HasInterpolation = interpolates;
    }

    /// <summary>Infos from the most basic type to the concrete one.</summary>
    public ReplicationTypeInfo[] Infos { get; }

    /// <summary>How many infos carry state (one <see cref="ReplicatedState"/> each).</summary>
    public int StateCount { get; }

    public bool HasInterpolation { get; }

    public int RpcCount => _rpcs.Length;

    public RpcInfo? GetRpc(int wireIndex) => (uint)wireIndex < (uint)_rpcs.Length ? _rpcs[wireIndex] : null;

    /// <summary>A fresh state per info with members, base first.</summary>
    public ReplicatedState[] CreateStates()
    {
        if (StateCount == 0)
            return [];
        var states = new ReplicatedState[StateCount];
        var i = 0;
        foreach (var info in Infos)
            if (info.HasState)
                states[i++] = info.CreateState();
        return states;
    }
}
