using System.Reflection;
using MainframeEngine.Serialization;

namespace MainframeEngine.L10n.Extraction;

/// <summary>
/// Which scene properties are player-facing text: the <c>[Export(Translatable = true)]</c> members of every registered
/// node and resource type (the engine's, plus game assemblies loaded with <c>--assembly</c>), and extra
/// <c>Type.Property</c> names given with <c>--translatable</c>.
/// </summary>
internal sealed class TranslatablePropertyIndex
{
    private readonly Dictionary<string, HashSet<string>> _byType = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownTypes = new(StringComparer.Ordinal);

    /// <summary>
    /// Builds the index from <see cref="TypeRegistry"/> after loading <paramref name="assemblies"/> (game assemblies;
    /// their dependencies are resolved from their own folder).
    /// </summary>
    public static TranslatablePropertyIndex FromRegistry(IEnumerable<string> assemblies, IEnumerable<string> extra)
    {
        var index = new TranslatablePropertyIndex();
        TypeRegistry.EnsureRegistered(typeof(Node).Assembly);
        foreach (var path in assemblies)
        {
            var full = Path.GetFullPath(path);
            if (!File.Exists(full))
                throw new FileNotFoundException($"assembly not found: {path}", full);
            var assembly = Assembly.LoadFrom(full);
            TypeRegistry.EnsureRegistered(assembly);
        }

        foreach (var info in TypeRegistry.All)
        {
            index._knownTypes.Add(info.Name);
            foreach (var property in info.TranslatableProperties)
                index.Add(info.Name, property.Name);
        }

        foreach (var spec in extra)
        {
            var dot = spec.LastIndexOf('.');
            if (dot <= 0 || dot == spec.Length - 1)
                throw new ArgumentException($"--translatable expects Type.Property, got '{spec}'");
            index.Add(spec[..dot], spec[(dot + 1)..]);
            index._knownTypes.Add(spec[..dot]);
        }

        return index;
    }

    /// <summary>An index from explicit <c>(type, property)</c> pairs only (tests).</summary>
    public static TranslatablePropertyIndex From(params (string Type, string Property)[] pairs)
    {
        var index = new TranslatablePropertyIndex();
        foreach (var (type, property) in pairs)
        {
            index.Add(type, property);
            index._knownTypes.Add(type);
        }

        return index;
    }

    private void Add(string type, string property)
    {
        if (!_byType.TryGetValue(type, out var set))
            _byType[type] = set = new HashSet<string>(StringComparer.Ordinal);
        set.Add(property);
    }

    /// <summary>The type is registered (or was named explicitly), so its properties are known.</summary>
    public bool IsKnown(string type) => _knownTypes.Contains(type);

    public bool IsTranslatable(string type, string property) =>
        _byType.TryGetValue(type, out var set) && set.Contains(property);

    /// <summary>Number of translatable properties across all types.</summary>
    public int PropertyCount => _byType.Values.Sum(static s => s.Count);
}
