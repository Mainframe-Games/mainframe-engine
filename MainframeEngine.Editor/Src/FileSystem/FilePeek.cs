using System.Text.Json;

namespace MainframeEngine.Editor;

/// <summary>A reference from a scene/resource file: a UID (<c>ref</c>/<c>instance</c>) with its path hint, or a plain path.</summary>
internal readonly record struct FileReference(string? Uid, string? Path);

/// <summary>
/// What the FileSystem panel needs from a <c>.mscene</c>/<c>.mres</c>/<c>project.mfproj</c>, read in one
/// <see cref="JsonDocument"/> pass: the root type, the UID, the files it references, or the parse error.
/// </summary>
internal sealed class FilePeek
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public required long Length { get; init; }
    public required DateTime ModifiedUtc { get; init; }
    public string? RootTypeName { get; private init; }
    public string? Uid { get; private init; }
    public string? Error { get; private init; }

    /// <summary>A song's <c>render.output</c> (null: the default output).</summary>
    public string? RenderOutput { get; private init; }
    public IReadOnlyList<FileReference> References { get; private init; } = [];

    public bool Matches(long length, DateTime modifiedUtc) => Length == length && ModifiedUtc == modifiedUtc;

    /// <summary>Reads <paramref name="fullPath"/>; never throws for unreadable or invalid files (see <see cref="Error"/>).</summary>
    public static FilePeek Read(string fullPath, FileKind kind, long length, DateTime modifiedUtc)
    {
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(fullPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new FilePeek { Length = length, ModifiedUtc = modifiedUtc, Error = $"Cannot read '{Path.GetFileName(fullPath)}': {e.Message}" };
        }

        try
        {
            using var document = JsonDocument.Parse(bytes, Options);
            var top = document.RootElement;
            if (top.ValueKind != JsonValueKind.Object)
                return new FilePeek { Length = length, ModifiedUtc = modifiedUtc, Error = "The file is not a JSON object." };

            string? typeName = null;
            if (kind == FileKind.Scene && SceneRoot(top) is { } root)
                typeName = root.TryGetProperty("instance", out _) ? "instance" : GetString(root, "type");
            else if (kind == FileKind.Resource)
                typeName = GetString(top, "type");

            var references = new List<FileReference>();
            if (kind is FileKind.Scene or FileKind.Resource)
                CollectReferences(top, references);
            return new FilePeek
            {
                Length = length,
                ModifiedUtc = modifiedUtc,
                RootTypeName = typeName,
                Uid = GetString(top, "uid"),
                RenderOutput = kind == FileKind.Song && top.TryGetProperty("render", out var render) && render.ValueKind == JsonValueKind.Object
                    ? GetString(render, "output")
                    : null,
                References = references,
            };
        }
        catch (JsonException e)
        {
            return new FilePeek { Length = length, ModifiedUtc = modifiedUtc, Error = $"Invalid JSON: {e.Message}" };
        }
    }

    // The root node entry: the first of "nodes" (format 2) or "root" (format 1).
    private static JsonElement? SceneRoot(JsonElement top)
    {
        if (top.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array && nodes.GetArrayLength() > 0
            && nodes[0].ValueKind == JsonValueKind.Object)
            return nodes[0];
        return top.TryGetProperty("root", out var root) && root.ValueKind == JsonValueKind.Object ? root : null;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static void CollectReferences(JsonElement element, List<FileReference> references)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var uid = GetString(element, "ref") ?? GetString(element, "instance");
                var isReference = uid is not null && AssetUid.IsUid(uid);
                if (isReference)
                    references.Add(new FileReference(uid, GetString(element, "path")));
                foreach (var property in element.EnumerateObject())
                {
                    // A reference's path is a hint for its UID, checked above.
                    if (isReference && property.NameEquals("path"))
                        continue;
                    CollectReferences(property.Value, references);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    CollectReferences(item, references);
                break;
            case JsonValueKind.String:
                var text = element.GetString()!;
                if (FileKinds.LooksLikeAssetPath(text))
                    references.Add(new FileReference(null, text));
                break;
        }
    }
}

/// <summary>Keeps <see cref="FilePeek"/>s by path, re-reading a file only when its size or modification time changed.</summary>
internal sealed class FilePeekCache
{
    private readonly Dictionary<string, FilePeek> _peeks = new(StringComparer.Ordinal);

    public FilePeek Get(string fullPath, FileKind kind, long length, DateTime modifiedUtc)
    {
        if (_peeks.TryGetValue(fullPath, out var peek) && peek.Matches(length, modifiedUtc))
            return peek;
        peek = FilePeek.Read(fullPath, kind, length, modifiedUtc);
        _peeks[fullPath] = peek;
        return peek;
    }

    /// <summary>Drops entries for files no longer in <paramref name="live"/>.</summary>
    public void Retain(IReadOnlySet<string> live)
    {
        foreach (var path in _peeks.Keys.Where(p => !live.Contains(p)).ToList())
            _peeks.Remove(path);
    }

    /// <summary>Scenes, resources and <c>project.mfproj</c> are peeked (the latter only for JSON errors).</summary>
    public static bool IsPeeked(string name, FileKind kind) => kind is FileKind.Scene or FileKind.Resource or FileKind.Song
        || string.Equals(name, ProjectSettings.FileName, StringComparison.OrdinalIgnoreCase);
}
