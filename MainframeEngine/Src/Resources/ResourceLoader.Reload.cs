using System.Collections;
using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>What <see cref="ResourceLoader.ReloadInPlace"/> did.</summary>
public enum ResourceReloadStatus : byte
{
    /// <summary>Nothing has the resource loaded (or it is a scene or an imported asset): the next load reads the file.</summary>
    NotCached,

    /// <summary>The file is as it was when it was loaded or last saved (the editor's own save, for one).</summary>
    Unchanged,

    /// <summary>The cached instance now holds the file's values.</summary>
    Reloaded,

    /// <summary>The file could not be read; the cached instance keeps what it had (<see cref="ResourceReloadResult.Error"/>).</summary>
    Failed,
}

/// <summary>The outcome of <see cref="ResourceLoader.ReloadInPlace"/>: the status, the cached resource, and an error.</summary>
public readonly record struct ResourceReloadResult(ResourceReloadStatus Status, Resource? Resource = null, string? Error = null);

public static partial class ResourceLoader
{
    private static readonly Dictionary<string, (long Length, DateTime Modified)> Stamps = new(StringComparer.Ordinal);

    /// <summary>
    /// Re-reads a resource file (<c>.mfres</c>) changed on disk into the resource already loaded from it (G1c, the editor's
    /// live loop): references to it stay valid. Its exported values are copied in through the type registry; inline
    /// resources are updated in place, and lists of named inline resources (a library's animations) are matched by name, so
    /// a player holding an animation keeps it. Changed <see cref="Animation"/>s get <see cref="Animation.NotifyChanged"/>.
    /// A file whose size and modification time match the load or the last save is <see cref="ResourceReloadStatus.Unchanged"/>.
    /// </summary>
    public static ResourceReloadResult ReloadInPlace(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var assets = AssetDatabase.Current;
        var fullPath = assets.ToAbsolutePath(path);
        var pathKey = assets.ToProjectPath(fullPath);
        Resource? cached;
        lock (Gate)
            cached = Cache.GetValueOrDefault(pathKey);
        if (cached is null or PackedScene || !FileExtensions.Matches(fullPath, SceneFormat.ResourceExtension))
            return new ResourceReloadResult(ResourceReloadStatus.NotCached);

        var stamp = ReadStamp(fullPath);
        lock (Gate)
            if (stamp is { } current && Stamps.TryGetValue(pathKey, out var known) && known == current)
                return new ResourceReloadResult(ResourceReloadStatus.Unchanged, cached);

        Resource fresh;
        try
        {
            fresh = ReadResourceFile(ContentFiles.ReadAllBytes(fullPath), pathKey);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException
                                      or KeyNotFoundException or InvalidOperationException or NotSupportedException)
        {
            return new ResourceReloadResult(ResourceReloadStatus.Failed, cached, e.Message);
        }

        if (fresh.GetType() != cached.GetType())
            return new ResourceReloadResult(ResourceReloadStatus.Failed, cached,
                $"'{pathKey}' is now a {fresh.GetType().Name}, not a {cached.GetType().Name}; reopen what uses it.");
        CopyInto(fresh, cached);
        RememberStamp(pathKey, fullPath);
        return new ResourceReloadResult(ResourceReloadStatus.Reloaded, cached);
    }

    /// <summary>
    /// Drops the cached resource at <paramref name="path"/> (an imported model whose <c>.mfmeta</c> changed, say) so the next
    /// <see cref="Load"/> reads the file again. Holders keep the instance they have. Returns false when nothing was cached.
    /// </summary>
    public static bool Evict(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var assets = AssetDatabase.Current;
        var pathKey = assets.ToProjectPath(assets.ToAbsolutePath(path));
        lock (Gate)
        {
            if (!Cache.Remove(pathKey, out var resource))
                return false;
            if (resource.Uid is { } uid && Cache.TryGetValue(uid, out var byUid) && ReferenceEquals(byUid, resource))
                Cache.Remove(uid);
            Stamps.Remove(pathKey);
            return true;
        }
    }

    private static void CopyInto(Resource from, Resource to)
    {
        var info = TypeRegistry.Get(to.GetType()) ?? throw new InvalidOperationException($"{to.GetType().FullName} is not registered.");
        foreach (var property in info.Properties)
        {
            var current = property.GetValue(to);
            var incoming = property.GetValue(from);
            if (current is Resource { IsExternal: false } inlineCurrent && incoming is Resource { IsExternal: false } inlineIncoming &&
                inlineCurrent.GetType() == inlineIncoming.GetType())
            {
                CopyInto(inlineIncoming, inlineCurrent);
                continue;
            }

            if (current is IList { IsFixedSize: false, IsReadOnly: false } list && incoming is IList fresh && NamedInline(list) && NamedInline(fresh))
            {
                var previous = list.Cast<Resource>().GroupBy(r => r.ResourceName).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
                var merged = new List<Resource>(fresh.Count);
                foreach (Resource item in fresh)
                {
                    if (previous.TryGetValue(item.ResourceName, out var kept) && kept.GetType() == item.GetType())
                    {
                        CopyInto(item, kept);
                        merged.Add(kept);
                    }
                    else
                    {
                        merged.Add(item);
                    }
                }

                list.Clear();
                foreach (var item in merged)
                    list.Add(item);
                continue;
            }

            property.CopyValue(from, to);
        }

        if (to is Animation animation)
            animation.NotifyChanged();
    }

    // Every element an inline resource with a name: a list whose items others find by name.
    private static bool NamedInline(IList list)
    {
        foreach (var item in list)
            if (item is not Resource { IsExternal: false, ResourceName.Length: > 0 })
                return false;
        return true;
    }

    private static void RememberStamp(string pathKey, string fullPath)
    {
        if (ReadStamp(fullPath) is not { } stamp)
            return;
        lock (Gate)
            Stamps[pathKey] = stamp;
    }

    private static (long Length, DateTime Modified)? ReadStamp(string fullPath)
    {
        var file = new FileInfo(fullPath); // disk-io: the editor's live loop compares the file on disk with what it loaded
        return file.Exists ? (file.Length, file.LastWriteTimeUtc) : null;
    }
}
