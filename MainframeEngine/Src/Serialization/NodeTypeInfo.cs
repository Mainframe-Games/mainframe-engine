namespace MainframeEngine.Serialization;

[Flags]
public enum NodeTypeTraits
{
    None = 0,

    /// <summary>The type derives from <see cref="Node"/>.</summary>
    Node = 1,

    /// <summary>The type derives from <see cref="MainframeEngine.Resource"/>.</summary>
    Resource = 2,

    /// <summary>Marked <see cref="ToolAttribute"/>: processes in the editor.</summary>
    Tool = 4,

    /// <summary>Abstract (no factory).</summary>
    Abstract = 8,
}

/// <summary>
/// Everything the scene serializer and the editor need to know about a <see cref="Node"/> or
/// <see cref="MainframeEngine.Resource"/> type: its scene type name, a factory, its <see cref="ExportAttribute"/>
/// properties (typed getter/setter delegates, hints, groups), its <see cref="SignalAttribute"/> events and its
/// serialized version with migrations. Emitted by <c>MainframeEngine.Generators</c> for every node and resource
/// type and registered with <see cref="TypeRegistry"/> from a module initializer.
/// </summary>
public sealed class NodeTypeInfo
{
    private readonly Func<object>? _factory;
    private readonly Lock _lock = new();
    private IReadOnlyList<ExportPropertyInfo>? _allProperties;
    private Dictionary<string, ExportPropertyInfo>? _propertiesByName;
    private IReadOnlyList<SignalInfo>? _allSignals;
    private IReadOnlyList<ExportPropertyInfo>? _translatable;
    private object? _defaultInstance;

    public NodeTypeInfo(
        string name,
        Type type,
        Type? baseType,
        Func<object>? factory,
        ExportPropertyInfo[] properties,
        SignalInfo[] signals,
        MigrationInfo[] migrations,
        int version,
        NodeTypeTraits flags,
        string? icon = null,
        EditorIconFamily iconFamily = EditorIconFamily.Inherit,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(migrations);
        ArgumentOutOfRangeException.ThrowIfLessThan(version, 1);

        Name = name;
        Type = type;
        BaseType = baseType;
        _factory = factory;
        DeclaredProperties = properties;
        DeclaredSignals = signals;
        Migrations = [.. migrations.OrderBy(m => m.FromVersion)];
        Version = version;
        Flags = flags | (factory is null ? NodeTypeTraits.Abstract : NodeTypeTraits.None);
        Icon = string.IsNullOrEmpty(icon) ? null : icon;
        IconFamily = iconFamily;
        Description = string.IsNullOrEmpty(description) ? null : description;

        foreach (var p in properties)
            p.DeclaringType = this;
        foreach (var s in signals)
            s.DeclaringType = this;
    }

    /// <summary>The type name written to scene files (class name unless <see cref="TypeNameAttribute"/>).</summary>
    public string Name { get; }

    public Type Type { get; }

    /// <summary>The CLR base type (its info is <see cref="Base"/> once registered).</summary>
    public Type? BaseType { get; }

    /// <summary>Info of the nearest registered base type.</summary>
    public NodeTypeInfo? Base => BaseType is null ? null : TypeRegistry.GetNearest(BaseType);

    public NodeTypeTraits Flags { get; }

    /// <summary>
    /// The editor icon declared on this type with <see cref="EditorIconAttribute"/> (a Tabler icon name), or null to use
    /// the nearest base type's (the editor resolves inheritance).
    /// </summary>
    public string? Icon { get; }

    /// <summary>The icon colour family declared on this type (<see cref="EditorIconFamily.Inherit"/>: the base type's).</summary>
    public EditorIconFamily IconFamily { get; }

    /// <summary>The type's XML doc <c>&lt;summary&gt;</c> as plain text (the editor's Add Node description), or null.</summary>
    public string? Description { get; }

    public bool IsNode => (Flags & NodeTypeTraits.Node) != 0;
    public bool IsResource => (Flags & NodeTypeTraits.Resource) != 0;
    public bool IsTool => (Flags & NodeTypeTraits.Tool) != 0;
    public bool IsAbstract => (Flags & NodeTypeTraits.Abstract) != 0;

    /// <summary>Current serialized layout version (<see cref="SerializedVersionAttribute"/>, default 1).</summary>
    public int Version { get; }

    /// <summary>Migrations declared on this type, ordered by <see cref="MigrationInfo.FromVersion"/>.</summary>
    public IReadOnlyList<MigrationInfo> Migrations { get; }

    /// <summary>Exported members declared on this type, in declaration order.</summary>
    public IReadOnlyList<ExportPropertyInfo> DeclaredProperties { get; }

    public IReadOnlyList<SignalInfo> DeclaredSignals { get; }

    /// <summary>All exported members, base types first (a redeclared name replaces the base's).</summary>
    public IReadOnlyList<ExportPropertyInfo> Properties
    {
        get
        {
            if (_allProperties is not null)
                return _allProperties;
            lock (_lock)
            {
                if (_allProperties is not null)
                    return _allProperties;
                var list = new List<ExportPropertyInfo>(Base?.Properties ?? []);
                foreach (var p in DeclaredProperties)
                {
                    var existing = list.FindIndex(x => string.Equals(x.Name, p.Name, StringComparison.Ordinal));
                    if (existing >= 0)
                        list[existing] = p;
                    else
                        list.Add(p);
                }

                _propertiesByName = list.ToDictionary(p => p.Name, StringComparer.Ordinal);
                _allProperties = list;
                return list;
            }
        }
    }

    /// <summary>All signals, base types first.</summary>
    public IReadOnlyList<SignalInfo> Signals
    {
        get
        {
            if (_allSignals is not null)
                return _allSignals;
            lock (_lock)
            {
                _allSignals ??= [.. Base?.Signals ?? [], .. DeclaredSignals];
                return _allSignals;
            }
        }
    }

    /// <summary>
    /// Exported members marked <see cref="ExportAttribute.Translatable"/> (base types first): the strings
    /// <c>mf-l10n</c> extracts from scenes and the editor checks against the translation template.
    /// </summary>
    public IReadOnlyList<ExportPropertyInfo> TranslatableProperties
    {
        get
        {
            if (_translatable is not null)
                return _translatable;
            var list = new List<ExportPropertyInfo>();
            foreach (var p in Properties)
            {
                if (p.Hints.Translatable)
                    list.Add(p);
            }

            _translatable = list;
            return list;
        }
    }

    public ExportPropertyInfo? FindProperty(string name)
    {
        _ = Properties;
        return _propertiesByName!.GetValueOrDefault(name);
    }

    public SignalInfo? FindSignal(string name)
    {
        foreach (var s in Signals)
            if (string.Equals(s.Name, name, StringComparison.Ordinal))
                return s;
        return null;
    }

    /// <summary>A new instance (throws for abstract types).</summary>
    public object CreateInstance() =>
        _factory?.Invoke() ?? throw new InvalidOperationException($"'{Name}' is abstract or has no accessible parameterless constructor.");

    /// <summary>
    /// A pristine instance used to tell default from non-default property values when saving. Created once,
    /// never added to a tree. Null for abstract types.
    /// </summary>
    public object? DefaultInstance
    {
        get
        {
            if (_defaultInstance is not null || _factory is null)
                return _defaultInstance;
            lock (_lock)
                return _defaultInstance ??= _factory();
        }
    }

    public override string ToString() => $"{Name} ({Type.FullName})";
}

/// <summary>Upgrades a type's serialized properties from <see cref="FromVersion"/> to the next version.</summary>
public sealed class MigrationInfo(int fromVersion, Action<PropertyBag> migrate)
{
    public int FromVersion { get; } = fromVersion;
    public Action<PropertyBag> Migrate { get; } = migrate ?? throw new ArgumentNullException(nameof(migrate));
}

/// <summary>
/// Inspector hints from <see cref="ExportAttribute"/>.
/// </summary>
public sealed record ExportHints
{
    public static readonly ExportHints None = new();

    public string? Range { get; init; }
    public string? File { get; init; }
    public bool Directory { get; init; }
    public bool Multiline { get; init; }
    public bool Flags { get; init; }
    public Type? NodeType { get; init; }

    /// <summary>Player-facing text extracted for translation (<see cref="ExportAttribute.Translatable"/>).</summary>
    public bool Translatable { get; init; }

    /// <summary>The inspector icon (<see cref="ExportAttribute.Icon"/> or <see cref="EditorIconAttribute"/> on the member), or null.</summary>
    public string? Icon { get; init; }

    /// <summary>The member's XML doc <c>&lt;summary&gt;</c> as plain text (inspector tooltip), or null.</summary>
    public string? Description { get; init; }

    /// <summary>Parses <see cref="Range"/> (<c>"min,max[,step]"</c>).</summary>
    public bool TryGetRange(out double min, out double max, out double step)
    {
        min = max = step = 0;
        if (string.IsNullOrWhiteSpace(Range))
            return false;
        var parts = Range.Split(',', StringSplitOptions.TrimEntries);
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (parts.Length < 2
            || !double.TryParse(parts[0], System.Globalization.NumberStyles.Float, culture, out min)
            || !double.TryParse(parts[1], System.Globalization.NumberStyles.Float, culture, out max))
            return false;
        if (parts.Length > 2 && !double.TryParse(parts[2], System.Globalization.NumberStyles.Float, culture, out step))
            return false;
        return true;
    }
}
