using System.Reflection;

namespace MainframeEngine.Serialization;

/// <summary>
/// All registered node and resource types, by scene type name and by CLR type. Generated module initializers
/// (one per assembly compiled with <c>MainframeEngine.Generators</c>) call <see cref="Register"/> when the
/// assembly loads; the editor calls <see cref="UnregisterAssembly"/> before unloading a game assembly and the
/// reloaded assembly registers itself again.
/// </summary>
public static class TypeRegistry
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, NodeTypeInfo> ByName = new(StringComparer.Ordinal);
    private static readonly Dictionary<Type, NodeTypeInfo> ByType = [];
    private static readonly HashSet<System.Reflection.Assembly> Examined = [];
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Assembly, object> ExaminedCollectible = new();
    private static readonly object Marker = new();

    /// <summary>Raised after types are registered or unregistered.</summary>
    public static event Action? Changed;

    /// <summary>
    /// Registers <paramref name="info"/>. A second type with the same name throws, unless it is the same type
    /// from a newer load of its assembly (editor reload), which replaces the old one.
    /// </summary>
    public static void Register(NodeTypeInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        lock (Gate)
        {
            if (ByName.TryGetValue(info.Name, out var existing) && existing.Type != info.Type)
            {
                var sameTypeReloaded = string.Equals(existing.Type.FullName, info.Type.FullName, StringComparison.Ordinal)
                                       && existing.Type.Assembly != info.Type.Assembly;
                if (!sameTypeReloaded)
                    throw new InvalidOperationException(
                        $"Type name '{info.Name}' is used by both {existing.Type.FullName} ({existing.Type.Assembly.GetName().Name}) " +
                        $"and {info.Type.FullName} ({info.Type.Assembly.GetName().Name}); rename one or add [TypeName(\"...\")].");
                ByType.Remove(existing.Type);
            }

            ByName[info.Name] = info;
            ByType[info.Type] = info;
        }

        Changed?.Invoke();
    }

    /// <summary>Removes every type declared in <paramref name="assembly"/> (before unloading it).</summary>
    public static void UnregisterAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Gate)
        {
            foreach (var info in ByType.Values.Where(i => i.Type.Assembly == assembly).ToArray())
            {
                ByType.Remove(info.Type);
                if (ByName.TryGetValue(info.Name, out var byName) && byName == info)
                    ByName.Remove(info.Name);
            }

            Examined.Remove(assembly);
        }

        Changed?.Invoke();
    }

    /// <summary>
    /// The type registered under <paramref name="name"/>, or null. On a miss, loaded assemblies that reference
    /// the engine get their module initializers run (registration happens on first access to an assembly, which
    /// may not have happened yet) and the lookup is retried.
    /// </summary>
    public static NodeTypeInfo? Get(string name)
    {
        lock (Gate)
        {
            if (ByName.TryGetValue(name, out var info))
                return info;
        }

        InitializeLoadedAssemblies();
        lock (Gate)
            return ByName.GetValueOrDefault(name);
    }

    /// <summary>Runs <paramref name="assembly"/>'s module initializer so its types are registered.</summary>
    public static void EnsureRegistered(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
    }

    private static void InitializeLoadedAssemblies()
    {
        var engine = typeof(Node).Assembly.GetName().Name;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            lock (Gate)
            {
                // Each assembly is examined once (a scene with many unknown types would rescan otherwise). Collectible
                // (game) assemblies are remembered weakly so the set never keeps their load context alive.
                if (assembly.IsCollectible)
                {
                    if (ExaminedCollectible.TryGetValue(assembly, out _))
                        continue;
                    ExaminedCollectible.Add(assembly, Marker);
                }
                else if (!Examined.Add(assembly))
                {
                    continue;
                }
            }

            if (assembly.IsDynamic)
                continue;
            if (assembly == typeof(Node).Assembly
                || assembly.GetReferencedAssemblies().Any(a => string.Equals(a.Name, engine, StringComparison.Ordinal)))
                EnsureRegistered(assembly);
        }
    }

    /// <summary>The info generated for exactly <paramref name="type"/>, or null.</summary>
    public static NodeTypeInfo? Get(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        EnsureModuleInitialized(type);
        lock (Gate)
            return ByType.GetValueOrDefault(type);
    }

    /// <summary>The info for <paramref name="type"/> or its nearest registered base type, or null.</summary>
    public static NodeTypeInfo? GetNearest(Type type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            if (Get(t) is { } info)
                return info;
        return null;
    }

    /// <summary>Like <see cref="GetNearest"/> but throws a helpful error when nothing is registered.</summary>
    public static NodeTypeInfo GetRequired(Type type) =>
        GetNearest(type) ?? throw new InvalidOperationException(
            $"{type.FullName} is not registered. Reference MainframeEngine.Generators as an analyzer in its project.");

    /// <summary>Every registered type (a snapshot).</summary>
    public static IReadOnlyList<NodeTypeInfo> All
    {
        get
        {
            lock (Gate)
                return [.. ByName.Values];
        }
    }

    /// <summary>Creates a node of the registered type <paramref name="name"/>.</summary>
    public static Node CreateNode(string name)
    {
        var info = Get(name) ?? throw new InvalidOperationException($"Unknown node type '{name}'.");
        return info.CreateInstance() as Node ?? throw new InvalidOperationException($"'{name}' is not a node type.");
    }

    // A type's module initializer runs on first access to its assembly, which may not have happened yet when a
    // scene names one of its types; touching the module forces it.
    private static void EnsureModuleInitialized(Type type) => EnsureRegistered(type.Assembly);
}
