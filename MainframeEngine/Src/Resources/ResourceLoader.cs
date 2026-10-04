using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>
/// Loads <c>.mscene</c> (<see cref="PackedScene"/>) and <c>.mres</c> files by path or UID and caches them by
/// UID, so every user shares one instance (Godot's <c>ResourceLoader</c>). Each <see cref="Load{T}"/> adds a
/// reference; <see cref="Resource.Release"/> drops it and the last release evicts the resource.
/// </summary>
/// <remarks>
/// UIDs resolve through <see cref="AssetDatabase.Current"/>. Relative paths resolve against
/// <see cref="AssetDatabase.ProjectRoot"/> (the app folder for games). Loading is synchronous; the cache is
/// thread-safe.
/// </remarks>
public static class ResourceLoader
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, Resource> Cache = new(StringComparer.Ordinal);

    [ThreadStatic]
    private static HashSet<string>? _loading;

    /// <summary>Loads (or returns the cached) resource at <paramref name="pathOrUid"/> and adds a reference.</summary>
    public static T Load<T>(string pathOrUid) where T : Resource
    {
        var resource = Load(pathOrUid);
        if (resource is T typed)
            return typed;
        resource.Release();
        throw new InvalidCastException($"'{pathOrUid}' is a {resource.GetType().Name}, not a {typeof(T).Name}.");
    }

    /// <summary>Loads (or returns the cached) resource at <paramref name="pathOrUid"/> and adds a reference.</summary>
    public static Resource Load(string pathOrUid)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathOrUid);
        return AssetUid.IsUid(pathOrUid) ? LoadReference(pathOrUid, null, pathOrUid) : LoadReference(null, pathOrUid, pathOrUid);
    }

    /// <summary>True if <paramref name="pathOrUid"/> resolves to an existing file.</summary>
    public static bool Exists(string pathOrUid)
    {
        ArgumentException.ThrowIfNullOrEmpty(pathOrUid);
        var assets = AssetDatabase.Current;
        var path = AssetUid.IsUid(pathOrUid) ? assets.GetPath(pathOrUid) : pathOrUid;
        return path is not null && File.Exists(assets.ToAbsolutePath(path));
    }

    /// <summary>True if the resource with <paramref name="uid"/> is cached.</summary>
    public static bool IsCached(string uid)
    {
        lock (Gate)
            return Cache.ContainsKey(uid);
    }

    /// <summary>Drops every cached resource (calling <see cref="Resource.OnUnloaded"/>), e.g. on engine shutdown.</summary>
    public static void ClearCache()
    {
        Resource[] all;
        lock (Gate)
        {
            all = [.. Cache.Values.Distinct(ReferenceEqualityComparer.Instance).Cast<Resource>()];
            Cache.Clear();
        }

        foreach (var resource in all)
        {
            resource.ClearReferences();
            Unload(resource);
        }
    }

    /// <summary>
    /// Resolves a reference from a file: by UID first (the path is only a hint, so moved files still resolve),
    /// then by path.
    /// </summary>
    internal static Resource LoadReference(string? uid, string? pathHint, string referencedFrom)
    {
        var assets = AssetDatabase.Current;
        string? path = null;
        if (uid is not null)
        {
            lock (Gate)
            {
                if (Cache.TryGetValue(uid, out var cached))
                {
                    cached.AddReference();
                    return cached;
                }
            }

            path = assets.GetPath(uid);
            if (path is null && pathHint is not null && File.Exists(assets.ToAbsolutePath(pathHint)))
            {
                Log.Warning($"[Resources] UID {uid} (from '{referencedFrom}') is not in the asset database; using path '{pathHint}'.");
                path = pathHint;
            }
        }

        path ??= pathHint ?? throw new FileNotFoundException($"'{referencedFrom}' references unknown asset {uid}.");
        var fullPath = assets.ToAbsolutePath(path);
        var pathKey = assets.ToProjectPath(fullPath);

        lock (Gate)
        {
            if (Cache.TryGetValue(pathKey, out var byPath))
            {
                byPath.AddReference();
                return byPath;
            }
        }

        _loading ??= new HashSet<string>(StringComparer.Ordinal);
        if (!_loading.Add(pathKey))
            throw new InvalidDataException($"'{pathKey}' references itself (directly or through other resources).");
        try
        {
            var resource = ReadFile(fullPath, pathKey);
            lock (Gate)
            {
                // Another thread may have loaded it meanwhile; keep the first.
                var key = resource.Uid ?? pathKey;
                if (Cache.TryGetValue(key, out var raced))
                {
                    raced.AddReference();
                    return raced;
                }

                Cache[key] = resource;
                if (resource.Uid is not null)
                    Cache[pathKey] = resource;
                resource.AddReference();
            }

            if (resource.Uid is not null)
                assets.Register(resource.Uid, pathKey);
            return resource;
        }
        finally
        {
            _loading.Remove(pathKey);
        }
    }

    /// <summary>Puts a just-saved resource in the cache under its UID and path (holding no reference).</summary>
    internal static void Register(Resource resource)
    {
        lock (Gate)
        {
            if (resource.Uid is not null)
                Cache[resource.Uid] = resource;
            if (resource.ResourcePath is not null)
                Cache[AssetDatabase.Current.ToProjectPath(resource.ResourcePath)] = resource;
        }
    }

    /// <summary>The cached resource with <paramref name="uid"/> (no reference added), or null.</summary>
    internal static Resource? GetCached(string uid)
    {
        lock (Gate)
            return Cache.GetValueOrDefault(uid);
    }

    internal static void OnReleased(Resource resource)
    {
        lock (Gate)
        {
            if (resource.Uid is not null && Cache.TryGetValue(resource.Uid, out var byUid) && ReferenceEquals(byUid, resource))
                Cache.Remove(resource.Uid);
            if (resource.ResourcePath is not null)
            {
                var key = AssetDatabase.Current.ToProjectPath(resource.ResourcePath);
                if (Cache.TryGetValue(key, out var byPath) && ReferenceEquals(byPath, resource))
                    Cache.Remove(key);
            }
        }

        Unload(resource);
    }

    private static void Unload(Resource resource)
    {
        if (resource.Unloaded)
            return;
        resource.Unloaded = true;
        resource.Dependencies?.ReleaseExternal();
        resource.Dependencies = null;
        resource.OnUnloaded();
    }

    private static Resource ReadFile(string fullPath, string pathKey)
    {
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Resource file not found: '{pathKey}'.", fullPath);

        var bytes = File.ReadAllBytes(fullPath);
        if (fullPath.EndsWith(SceneFormat.SceneExtension, StringComparison.OrdinalIgnoreCase))
        {
            var scene = new PackedScene { ResourcePath = pathKey };
            scene.SetContent(bytes, pathKey);
            return scene;
        }

        if (fullPath.EndsWith(SceneFormat.ResourceExtension, StringComparison.OrdinalIgnoreCase))
            return ReadResourceFile(bytes, pathKey);

        throw new NotSupportedException($"No loader for '{pathKey}' (expected {SceneFormat.SceneExtension} or {SceneFormat.ResourceExtension}).");
    }

    private static Resource ReadResourceFile(byte[] bytes, string pathKey)
    {
        JsonElement root;
        using (var doc = JsonDocument.Parse(bytes, SceneFormat.ReadOptions))
            root = doc.RootElement.Clone();

        var format = root.TryGetProperty("format", out var f) ? f.GetInt32() : SceneFormat.Current;
        root = SceneFormat.UpgradeFile(root, format, pathKey);

        var typeName = root.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (string.IsNullOrEmpty(typeName))
            throw new InvalidDataException($"'{pathKey}' has no \"type\".");
        var version = root.TryGetProperty("v", out var v) ? v.GetInt32() : 1;
        var props = root.TryGetProperty("props", out var p) ? p : default;

        var table = new ResourceTable(SceneDocument.ParseResourceTable(root, pathKey), pathKey);
        Resource resource;
        var info = TypeRegistry.Get(typeName);
        if (info is { IsResource: true, IsAbstract: false })
        {
            resource = (Resource)info.CreateInstance();
            PropertyApplier.Apply(resource, info, props, version, table, pathKey);
        }
        else
        {
            Log.Warning($"[Resources] '{pathKey}': unknown resource type '{typeName}'; kept as MissingResource.");
            resource = new MissingResource(typeName, version, props);
        }

        resource.ResourcePath = pathKey;
        resource.Uid = root.TryGetProperty("uid", out var uid) ? uid.GetString() : null;
        resource.Dependencies = table; // external sub-resources are released when this one unloads
        return resource;
    }
}
