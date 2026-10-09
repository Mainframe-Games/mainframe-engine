using System.Text.Json;

namespace MainframeEngine.Serialization;

/// <summary>
/// The <c>"resources"</c> table of a scene or resource file: inline resources are created on first use (and
/// shared by everything in the file that references them), external ones are loaded through
/// <see cref="ResourceLoader"/> and released with the table. With <paramref name="keepKeys"/> (format 2 files) inline
/// resources remember their key (<see cref="Resource.SceneLocalId"/>), so the next save writes them under the same one.
/// </summary>
internal sealed class ResourceTable(Dictionary<string, JsonElement> entries, string source, bool keepKeys) : DeserializationContext
{
    private readonly Dictionary<string, Resource> _resolved = new(StringComparer.Ordinal);
    private readonly List<Resource> _external = [];

    public string Source => source;

    public override Resource GetResource(string key)
    {
        if (_resolved.TryGetValue(key, out var resolved))
            return resolved;
        if (!entries.TryGetValue(key, out var entry))
            throw new InvalidDataException($"'{source}': no resource \"{key}\" in the resources table.");

        Resource resource;
        if (entry.TryGetProperty("ref", out _) || (entry.TryGetProperty("path", out _) && !entry.TryGetProperty("type", out _)))
        {
            var uid = entry.TryGetProperty("ref", out var r) ? r.GetString() : null;
            var path = entry.TryGetProperty("path", out var p) ? p.GetString() : null;
            resource = ResourceLoader.LoadReference(uid, path, source);
            _external.Add(resource);
            _resolved[key] = resource;
            return resource;
        }

        var typeName = entry.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(typeName))
            throw new InvalidDataException($"'{source}': resource \"{key}\" has no \"type\".");
        var props = entry.TryGetProperty("props", out var pr) ? pr : default;
        var version = entry.TryGetProperty("v", out var v) ? v.GetInt32() : 1;

        var info = TypeRegistry.Get(typeName);
        if (info is not { IsResource: true, IsAbstract: false })
        {
            Log.Warning($"[Scene] '{source}': unknown resource type '{typeName}'; kept as MissingResource.");
            var missing = new MissingResource(typeName, version, props);
            if (keepKeys)
                missing.SceneLocalId = key;
            _resolved[key] = missing;
            missing.ResourceReferences = RawProperties.ResolveReferences(props, this);
            return missing;
        }

        resource = (Resource)info.CreateInstance();
        if (keepKeys)
            resource.SceneLocalId = key;
        _resolved[key] = resource; // before applying: self-references resolve to the same object
        PropertyApplier.Apply(resource, info, props, version, this, $"{source} (resource \"{key}\")");
        return resource;
    }

    public override Resource CreateInlineResource(string typeName, JsonElement props, int version)
    {
        var info = TypeRegistry.Get(typeName);
        if (info is not { IsResource: true, IsAbstract: false })
            throw new InvalidDataException($"'{source}': unknown resource type '{typeName}' in a migrated value.");
        var resource = (Resource)info.CreateInstance();
        PropertyApplier.Apply(resource, info, props, version, this, $"{source} (inline {typeName})");
        return resource;
    }

    /// <summary>True when a resource this table created or loaded has a type from <paramref name="assembly"/>.</summary>
    public bool References(System.Reflection.Assembly assembly)
    {
        foreach (var resource in _resolved.Values)
            if (resource.GetType().Assembly == assembly)
                return true;
        return false;
    }

    /// <summary>Drops the references this table took on external resources.</summary>
    public void ReleaseExternal()
    {
        foreach (var r in _external)
            if (r.ReferenceCount > 0)
                r.Release();
        _external.Clear();
    }
}

/// <summary>Applies serialized properties to a node or resource, running version migrations first.</summary>
internal static class PropertyApplier
{
    public static void Apply(object target, NodeTypeInfo info, JsonElement props, int version, DeserializationContext context, string where)
    {
        if (props.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            if (version < info.Version)
                Migrate(info, new PropertyBag(), version, where);
            return;
        }

        if (props.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{where}: \"props\" must be an object.");

        if (version > info.Version)
            Log.Warning($"[Scene] {where}: written by a newer {info.Name} (v{version} > v{info.Version}); unknown properties are ignored.");

        if (version < info.Version)
        {
            var bag = new PropertyBag(props);
            Migrate(info, bag, version, where);
            foreach (var (name, value) in bag.Entries)
                ApplyOne(target, info, name, value, context, where);
            return;
        }

        foreach (var property in props.EnumerateObject())
            ApplyOne(target, info, property.Name, property.Value, context, where);
    }

    /// <summary>Applies already-migrated properties (a removed type's upgrade hands over what it did not consume).</summary>
    public static void Apply(object target, NodeTypeInfo info, PropertyBag bag, DeserializationContext context, string where)
    {
        foreach (var (name, value) in bag.Entries)
            ApplyOne(target, info, name, value, context, where);
    }

    private static void Migrate(NodeTypeInfo info, PropertyBag bag, int fromVersion, string where)
    {
        for (var v = fromVersion; v < info.Version; v++)
        {
            var migration = info.Migrations.FirstOrDefault(m => m.FromVersion == v);
            if (migration is null)
            {
                Log.Warning($"[Scene] {where}: {info.Name} has no migration from v{v}; properties are applied as-is.");
                continue;
            }

            migration.Migrate(bag);
        }
    }

    private static void ApplyOne(object target, NodeTypeInfo info, string name, JsonElement value, DeserializationContext context, string where)
    {
        var property = info.FindProperty(name);
        if (property is null)
        {
            Log.Warning($"[Scene] {where}: {info.Name} has no exported property '{name}'; ignored.");
            return;
        }

        try
        {
            property.Read(value, target, context);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or ArgumentException
                                       or InvalidCastException or OverflowException or KeyNotFoundException)
        {
            throw new InvalidDataException($"{where}: cannot read {info.Name}.{name}: {e.Message}", e);
        }
    }
}
