using System.Text.Json;
using System.Text.Json.Serialization;

namespace MainframeEngine;

/// <summary>A <c>.mfmeta</c> sidecar: the UID and import settings of an asset that cannot store them itself (images, sounds, ...).</summary>
public sealed class AssetMeta
{
    /// <summary>
    /// The sidecar format new sidecars are written with. 2 (ADR 0255): models import PBR unless <c>"shading"</c> says
    /// otherwise; sidecars without a <see cref="Format"/> (1) kept the Blinn-Phong default they were written under.
    /// </summary>
    public const int CurrentFormat = 2;

    /// <summary>The sidecar's format; null in sidecars written before formats existed (format 1).</summary>
    public int? Format { get; set; }

    /// <summary><see cref="Format"/>, with a missing one read as 1.</summary>
    [JsonIgnore]
    public int EffectiveFormat => Format ?? 1;

    public string Uid { get; set; } = string.Empty;

    /// <summary>Importer that processes the asset (<c>texture</c>, <c>audio</c>, ...); null for files used as-is.</summary>
    public string? Importer { get; set; }

    /// <summary>Importer settings (free-form JSON).</summary>
    public Dictionary<string, JsonElement>? Settings { get; set; }
}

/// <summary>The runtime asset index (<c>Assets/assets.index.json</c>): UID → project-relative path.</summary>
public sealed class AssetIndex
{
    public int Format { get; set; } = 1;

    public Dictionary<string, string> Assets { get; set; } = [];
}

// .mfmeta sidecars and the asset index are committed content: "\n" on every OS (byte-identical saves).
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
[JsonSerializable(typeof(float[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class AssetJsonContext : JsonSerializerContext;

/// <summary>
/// Maps asset UIDs to paths. Built by scanning <c>Assets/</c> (scene and resource files carry their own UID;
/// other files have a <c>.mfmeta</c> sidecar, created by the editor) or, in shipped builds, from
/// <c>Assets/assets.index.json</c> (<see cref="WriteIndex"/>). Paths are project-relative with <c>/</c>
/// separators. <see cref="ResourceLoader"/> resolves UIDs through <see cref="Current"/>.
/// </summary>
public sealed class AssetDatabase
{
    public const string ContentFolder = ContentPaths.FolderName;
    public const string IndexFileName = "assets.index.json";
    public const string MetaExtension = FileExtensions.Meta;

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
    /// application folder, which holds <c>Assets/</c>) — never the working directory, so games load the same
    /// files however they are launched. Filled lazily from the index or a scan on the first UID lookup. Tools (the
    /// editor) assign one rooted at the open project.
    /// </summary>
    public static AssetDatabase Current
    {
        get => _current ??= new AssetDatabase(ContentPaths.BaseDirectory);
        set => _current = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Folder that contains <c>Assets/</c>; relative asset paths resolve against it.</summary>
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
    /// Fills the database: from <c>Assets/assets.index.json</c> when present (shipped builds), otherwise by
    /// scanning. <paramref name="createMissingMeta"/> (editor) writes <c>.mfmeta</c> sidecars with new UIDs for
    /// assets that have none.
    /// </summary>
    public void Refresh(bool createMissingMeta = false)
    {
        var index = Path.Combine(ContentDirectory, IndexFileName);
        if (!createMissingMeta && ContentFiles.Exists(index))
            LoadIndex(index);
        else
            Scan(createMissingMeta);
    }

    /// <summary>Scans <c>Assets/</c>: scene/resource UIDs from the files, other UIDs from <c>.mfmeta</c> sidecars.</summary>
    public void Scan(bool createMissingMeta = false)
    {
        lock (_gate)
        {
            _scanned = true;
            if (!ContentFiles.DirectoryExists(ContentDirectory))
                return;

            foreach (var file in ContentFiles.EnumerateFiles(ContentDirectory, "*", recursive: true))
            {
                var name = Path.GetFileName(file);
                if (createMissingMeta && IsStaleTemporaryMeta(file))
                {
                    TryDelete(file);
                    continue;
                }

                if (name.StartsWith('.') || FileExtensions.Matches(name, MetaExtension)
                                         || name.Equals(IndexFileName, StringComparison.OrdinalIgnoreCase) || IsBuildOutput(name)
                                         || IsTemporary(name) || IsInPartialFolder(file))
                    continue;

                var uid = IsSelfDescribing(file) ? ReadEmbeddedUid(file) : ReadOrCreateMeta(file, createMissingMeta)?.Uid;
                if (!string.IsNullOrEmpty(uid))
                    RegisterLocked(uid, ToProjectPath(file));
            }
        }
    }

    /// <summary>
    /// A file being written (<c>x.tmp</c>, <c>x.baking.wav.tmp</c>, <see cref="DurableFile.TempPath"/>): never an asset,
    /// so a scan that runs while it exists (the editor's file watcher during a bake or a save) gives it no <c>.mfmeta</c>.
    /// </summary>
    public static bool IsTemporary(string fileName) => fileName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The suffix of a folder still being written: a recording's <c>&lt;name&gt;.mp4.partial/</c> (its video stream and audio
    /// while it records, ADR 0190), which recordings in the project's <c>Assets/Video/Recordings</c> put under Assets/.
    /// </summary>
    public const string PartialFolderSuffix = ".partial";

    /// <summary>Whether <paramref name="path"/> is inside a <see cref="PartialFolderSuffix"/> folder (never an asset).</summary>
    public static bool IsInPartialFolder(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Replace('\\', '/').Contains(PartialFolderSuffix + "/", StringComparison.OrdinalIgnoreCase);
    }

    // A .mfmeta left for a temporary file that is gone (written by scans before temporary files were skipped).
    private static bool IsStaleTemporaryMeta(string path) =>
        path.EndsWith(".tmp" + MetaExtension, StringComparison.OrdinalIgnoreCase) && !File.Exists(path[..^MetaExtension.Length]); // disk-io: editor scans clean up sidecars on disk

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Assets] Could not delete the stale '{path}': {e.Message}");
        }
    }

    /// <summary>
    /// Files generated next to an asset that are part of it, not assets of their own: a surface shader's SPIR-V, layout and
    /// lock (<c>x.slang.vert.spv</c>, <c>x.slang.frag.spv</c>, a spatial shader's other passes such as
    /// <c>x.slang.depth.vert.spv</c>, <c>x.slang.mflayout.json</c> and <c>x.slang.mfspvlock</c>, see <see cref="SurfaceShaderBuild"/>)
    /// and a light-probe bake's coefficients (<c>x.mfprobes</c>, the data file of <c>x.mfres</c>, see <see cref="LightProbeData"/>).
    /// </summary>
    public static bool IsBuildOutput(string fileName) =>
        FileExtensions.Matches(fileName, FileExtensions.LightProbes)
        || (fileName.EndsWith(".spv", StringComparison.OrdinalIgnoreCase) && fileName.Contains(Shader.Extension + ".", StringComparison.OrdinalIgnoreCase))
        || IsShaderOutput(fileName, SurfaceShaderBuild.LockExtension)
        || IsShaderOutput(fileName, SurfaceShaderLayout.Extension);

    // x.slang + the extension, or + its name before ADR 0249 (x.slang.spvlock, x.slang.layout.json).
    private static bool IsShaderOutput(string fileName, string extension) =>
        fileName.EndsWith(Shader.Extension + extension, StringComparison.OrdinalIgnoreCase)
        || fileName.EndsWith(Shader.Extension + FileExtensions.LegacyOf(extension), StringComparison.OrdinalIgnoreCase);

    /// <summary>Loads an <see cref="AssetIndex"/> file.</summary>
    public void LoadIndex(string indexPath)
    {
        var index = JsonSerializer.Deserialize(ContentFiles.ReadAllBytes(indexPath), AssetJsonContext.Default.AssetIndex)
                    ?? throw new InvalidDataException($"Empty asset index '{indexPath}'.");
        lock (_gate)
        {
            _scanned = true;
            foreach (var (uid, path) in index.Assets)
                RegisterLocked(uid, path);
        }
    }

    /// <summary>Writes the runtime index (default <c>Assets/assets.index.json</c>) for shipped builds.</summary>
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

    /// <summary>Moves an asset file (and its <c>.mfmeta</c>) and updates the mapping; references by UID keep working.</summary>
    public void Move(string fromPath, string toPath)
    {
        var from = ToAbsolutePath(fromPath);
        var to = ToAbsolutePath(toPath);
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
        if (FileExtensions.FindExisting(from, MetaExtension, File.Exists) is { } meta) // disk-io: moves an asset on disk (editor)
            File.Move(meta, to + MetaExtension);

        lock (_gate)
        {
            var oldKey = ToProjectPath(from);
            if (_uidByPath.Remove(oldKey, out var uid))
                RegisterLocked(uid, ToProjectPath(to));
        }
    }

    /// <summary>Reads the asset's <c>.mfmeta</c> sidecar, creating it with a new UID when <paramref name="create"/>.</summary>
    public AssetMeta? ReadOrCreateMeta(string assetPath, bool create)
    {
        var assetFull = ToAbsolutePath(assetPath);
        if (FileExtensions.FindExisting(assetFull, MetaExtension, ContentFiles.Exists) is { } existing)
            return JsonSerializer.Deserialize(ContentFiles.ReadAllBytes(existing), AssetJsonContext.Default.AssetMeta);
        var metaPath = assetFull + MetaExtension;
        if (!create)
            return null;

        var meta = new AssetMeta { Format = AssetMeta.CurrentFormat, Uid = AssetUid.Generate(AssetUid.PrefixForExtension(Path.GetExtension(assetPath))) };
        File.WriteAllBytes(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta, AssetJsonContext.Default.AssetMeta));
        return meta;
    }

    /// <summary>
    /// Writes <paramref name="meta"/> as the <c>.mfmeta</c> sidecar of <paramref name="assetPath"/> (replacing it) and
    /// registers its UID for the path. Tools use it to set import settings (the editor's song render: loop points).
    /// </summary>
    public void WriteMeta(string assetPath, AssetMeta meta)
    {
        ArgumentException.ThrowIfNullOrEmpty(assetPath);
        ArgumentNullException.ThrowIfNull(meta);
        if (string.IsNullOrEmpty(meta.Uid))
            meta.Uid = AssetUid.Generate(AssetUid.PrefixForExtension(Path.GetExtension(assetPath)));
        var metaPath = ToAbsolutePath(assetPath) + MetaExtension;
        // A sidecar that did not exist yet is new content (current format); a replaced one keeps the format it had.
        if (meta.Format is null && FileExtensions.FindExisting(ToAbsolutePath(assetPath), MetaExtension, File.Exists) is null) // disk-io: tools write sidecars on disk
            meta.Format = AssetMeta.CurrentFormat;
        File.WriteAllBytes(metaPath, JsonSerializer.SerializeToUtf8Bytes(meta, AssetJsonContext.Default.AssetMeta));
        if (FileExtensions.LegacyOf(MetaExtension) is { } legacy && File.Exists(ToAbsolutePath(assetPath) + legacy)) // disk-io: tools write sidecars on disk
            File.Delete(ToAbsolutePath(assetPath) + legacy);
        Register(meta.Uid, ToProjectPath(assetPath));
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
    /// <remarks>A path with an extension from before ADR 0249 (<c>x.mscene</c>) whose file is gone resolves to the file's
    /// current name (<c>x.mfscene</c>) when that exists: code and settings written before the editor migrated the project.</remarks>
    public string ToAbsolutePath(string path)
    {
        var full = Path.GetFullPath(ContentPaths.UpgradeLegacyPath(path), ProjectRoot);
        var upgraded = FileExtensions.Upgrade(full);
        return ReferenceEquals(upgraded, full) || ContentFiles.Exists(full) || !ContentFiles.Exists(upgraded) ? full : upgraded;
    }

    internal static bool IsSelfDescribing(string path) =>
        FileExtensions.Matches(path, FileExtensions.Scene) || FileExtensions.Matches(path, FileExtensions.Resource) ||
        FileExtensions.Matches(path, FileExtensions.Song) || FileExtensions.Matches(path, FileExtensions.Music) ||
        path.EndsWith(FileExtensions.Timeline, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the top-level <c>"uid"</c> of a scene/resource file without parsing the rest.</summary>
    internal static string? ReadEmbeddedUid(string path)
    {
        try
        {
            var reader = new Utf8JsonReader(ContentFiles.ReadAllBytes(path), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });
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
            if (ContentFiles.Exists(ToAbsolutePath(existing)) && ContentFiles.Exists(ToAbsolutePath(path)))
                Log.Warning($"[Assets] UID {uid} is used by both '{existing}' and '{path}'; using '{path}'.");
            _uidByPath.Remove(existing);
        }

        _pathByUid[uid] = path;
        _uidByPath[path] = uid;
    }
}
