using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine;

/// <summary>A <c>.meta</c> sidecar: the UID and import settings of an asset that cannot store them itself (images, sounds, ...).</summary>
public sealed class AssetMeta
{
    public string Uid { get; set; } = string.Empty;

    /// <summary>Importer that processes the asset (<c>texture</c>, <c>audio</c>, ...); null for files used as-is.</summary>
    public string? Importer { get; set; }

    /// <summary>Importer settings (free-form JSON).</summary>
    public Dictionary<string, JsonElement>? Settings { get; set; }
}

/// <summary>The runtime asset index (<c>Content/assets.index.json</c>): UID → project-relative path.</summary>
public sealed class AssetIndex
{
    public int Format { get; set; } = 1;

    public Dictionary<string, string> Assets { get; set; } = [];
}

// .meta sidecars and the asset index are committed content: "\n" on every OS (byte-identical saves).
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AssetMeta))]
[JsonSerializable(typeof(AssetIndex))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(float))]
internal sealed partial class AssetJsonContext : JsonSerializerContext;

/// <summary>
/// Maps asset UIDs to paths. Built by scanning <c>Content/</c> (scene and resource files carry their own UID;
/// other files have a <c>.meta</c> sidecar, created by the editor) or, in shipped builds, from
/// <c>Content/assets.index.json</c> (<see cref="WriteIndex"/>). Paths are project-relative with <c>/</c>
/// separators. <see cref="ResourceLoader"/> resolves UIDs through <see cref="Current"/>.
/// </summary>
public sealed class AssetDatabase
{
    public const string ContentFolder = "Content";
    public const string IndexFileName = "assets.index.json";
    public const string MetaExtension = ".meta";

    private static AssetDatabase? _current;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _pathByUid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _uidByPath = new(StringComparer.Ordinal);
    private bool _scanned;

    public AssetDatabase(string projectRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(projectRoot);
        ProjectRoot = Path.GetFullPath(projectRoot);
    }

    /// <summary>
    /// The database the loader uses. Defaults to one rooted at <see cref="ContentPaths.BaseDirectory"/> (the
    /// application folder, which holds <c>Content/</c>) — never the working directory, so games load the same
    /// files however they are launched. Filled lazily from the index or a scan on the first UID lookup. Tools (the
    /// editor) assign one rooted at the open project.
    /// </summary>
    public static AssetDatabase Current
    {
        get => _current ??= new AssetDatabase(ContentPaths.BaseDirectory);
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Folder that contains <c>Content/</c>; relative asset paths resolve against it.</summary>
    public string ProjectRoot { get; }

    public string ContentDirectory => Path.Combine(ProjectRoot, ContentFolder);

    /// <summary>Known assets, UID → project-relative path (a snapshot).</summary>
    public IReadOnlyDictionary<string, string> Entries
    {
        get
        {
            lock (_gate)
                return new Dictionary<string, string>(_pathByUid, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Fills the database: from <c>Content/assets.index.json</c> when present (shipped builds), otherwise by
    /// scanning. <paramref name="createMissingMeta"/> (editor) writes <c>.meta</c> sidecars with new UIDs for
    /// assets that have none.
    /// </summary>
    public void Refresh(bool createMissingMeta = false)
    {
        var index = Path.Combine(ContentDirectory, IndexFileName);
        if (!createMissingMeta && File.Exists(index))
            LoadIndex(index);
        else
            Scan(createMissingMeta);
    }

    /// <summary>Scans <c>Content/</c>: scene/resource UIDs from the files, other UIDs from <c>.meta</c> sidecars.</summary>
    public void Scan(bool createMissingMeta = false)
    {
        lock (_gate)
        {
            _scanned = true;
            if (!Directory.Exists(ContentDirectory))
                return;

            foreach (var file in Directory.EnumerateFiles(ContentDirectory, "*", SearchOption.AllDirectories))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith('.') || name.EndsWith(MetaExtension, StringComparison.OrdinalIgnoreCase)
                                         || name.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var uid = IsSelfDescribing(file) ? ReadEmbeddedUid(file) : ReadOrCreateMeta(file, createMissingMeta)?.Uid;
                if (!string.IsNullOrEmpty(uid))
                    RegisterLocked(uid, ToProjectPath(file));
            }
        }
    }

    /// <summary>Loads an <see cref="AssetIndex"/> file.</summary>
    public void LoadIndex(string indexPath)
    {
        var index = JsonSerializer.Deserialize(File.ReadAllBytes(indexPath), AssetJsonContext.Default.AssetIndex)
                    ?? throw new InvalidDataException($"Empty asset index '{indexPath}'.");
        lock (_gate)
        {
            _scanned = true;
            foreach (var (uid, path) in index.Assets)
                RegisterLocked(uid, path);
        }
    }

    /// <summary>Writes the runtime index (default <c>Content/assets.index.json</c>) for shipped builds.</summary>
    public void WriteIndex(string? indexPath = null)
    {
        indexPath ??= Path.Combine(ContentDirectory, IndexFileName);
        var index = new AssetIndex();
        lock (_gate)
        {
            foreach (var (uid, path) in _pathByUid.OrderBy(e => e.Value, StringComparer.Ordinal))
                index.Assets[uid] = path;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(indexPath))!);
        File.WriteAllBytes(indexPath, JsonSerializer.SerializeToUtf8Bytes(index, AssetJsonContext.Default.AssetIndex));
    }

    /// <summary>The path registered for <paramref name="uid"/> (scanning once on the first miss).</summary>
    public string? GetPath(string uid)
    {
        lock (_gate)
        {
            if (_pathByUid.TryGetValue(uid, out var path))
                return path;
            if (_scanned)
                return null;
        }

        Refresh();
        lock (_gate)
            return _pathByUid.GetValueOrDefault(uid);
    }

    /// <summary>The UID registered for <paramref name="path"/>, or null.</summary>
    public string? GetUid(string path)
    {
        var key = ToProjectPath(path);
        lock (_gate)
            return _uidByPath.GetValueOrDefault(key);
    }

    /// <summary>Records that <paramref name="uid"/> lives at <paramref name="path"/> (on save and load).</summary>
    public void Register(string uid, string path)
    {
        if (!AssetUid.IsUid(uid))
            throw new ArgumentException($"'{uid}' is not a valid asset UID.", nameof(uid));
        lock (_gate)
            RegisterLocked(uid, ToProjectPath(path));
    }

    /// <summary>Forgets <paramref name="uid"/>.</summary>
    public void Unregister(string uid)
    {
        lock (_gate)
        {
            if (_pathByUid.Remove(uid, out var path))
                _uidByPath.Remove(path);
        }
    }

    /// <summary>Moves an asset file (and its <c>.meta</c>) and updates the mapping; references by UID keep working.</summary>
    public void Move(string fromPath, string toPath)
    {
        var from = ToAbsolutePath(fromPath);
        var to = ToAbsolutePath(toPath);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
        if (File.Exists(from + MetaExtension))
            File.Move(from + MetaExtension, to + MetaExtension);

        lock (_gate)
        {
            var oldKey = ToProjectPath(from);
            if (_uidByPath.Remove(oldKey, out var uid))
                RegisterLocked(uid, ToProjectPath(to));
        }
    }

    /// <summary>Reads the asset's <c>.meta</c> sidecar, creating it with a new UID when <paramref name="create"/>.</summary>
    public AssetMeta? ReadOrCreateMeta(string assetPath, bool create)
    {
        var metaPath = ToAbsolutePath(assetPath) + MetaExtension;
        if (File.Exists(metaPath))
            return JsonSerializer.Deserialize(File.ReadAllBytes(metaPath), AssetJsonContext.Default.AssetMeta);
        if (!create)
            return null;

        var meta = new AssetMeta { Uid = AssetUid.Generate(AssetUid.PrefixForExtension(Path.GetExtension(assetPath))) };
        File.WriteAllBytes(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta, AssetJsonContext.Default.AssetMeta));
        return meta;
    }

    /// <summary>Project-relative path with <c>/</c> separators (absolute when outside the project).</summary>
    public string ToProjectPath(string path)
    {
        var full = Path.GetFullPath(path, ProjectRoot);
        var relative = Path.GetRelativePath(ProjectRoot, full);
        var result = relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? full : relative;
        return result.Replace('\\', '/');
    }

    /// <summary>Absolute path for a project-relative (or absolute) path.</summary>
    public string ToAbsolutePath(string path) => Path.GetFullPath(path, ProjectRoot);

    internal static bool IsSelfDescribing(string path) =>
        path.EndsWith(".mscene", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mres", StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the top-level <c>"uid"</c> of a scene/resource file without parsing the rest.</summary>
    internal static string? ReadEmbeddedUid(string path)
    {
        try
        {
            var reader = new Utf8JsonReader(File.ReadAllBytes(path), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                return null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isUid = reader.ValueTextEquals("uid"u8);
                reader.Read();
                if (isUid)
                    return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
                reader.Skip();
            }
        }
        catch (JsonException e)
        {
            Log.Warning($"[Assets] '{path}' is not valid JSON: {e.Message}");
        }

        return null;
    }

    private void RegisterLocked(string uid, string path)
    {
        if (_pathByUid.TryGetValue(uid, out var existing) && !string.Equals(existing, path, StringComparison.Ordinal))
        {
            if (File.Exists(ToAbsolutePath(existing)) && File.Exists(ToAbsolutePath(path)))
                Log.Warning($"[Assets] UID {uid} is used by both '{existing}' and '{path}'; using '{path}'.");
            _uidByPath.Remove(existing);
        }

        _pathByUid[uid] = path;
        _uidByPath[path] = uid;
    }
}
