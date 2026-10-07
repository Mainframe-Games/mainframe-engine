using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// Rewrites references after files moved: in every <c>.mscene</c>/<c>.mres</c>/<c>.msong</c> under <c>Content/</c> and in
/// <c>project.mfproj</c>, the <c>path</c> hint of <c>{"ref"|"instance": uid, "path": …}</c> objects whose UID (or old
/// hint) belongs to a moved file, and every string value exactly equal to a moved path (textures, sky panoramas,
/// file-hinted properties, <c>mainScene</c>, autoload scenes, the bus layout, the window icon). Only files that changed
/// are written, atomically, in the writers' style: scenes and resources laid out like the scene writer
/// (<see cref="SceneFormat.FormatJson"/>), <c>project.mfproj</c> like the project writer (2-space indent, <c>\n</c>, non-ASCII
/// unescaped).
/// </summary>
public static class ReferenceFixer
{
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static readonly JsonWriterOptions WriteOptions = new()
    {
        Indented = true,
        NewLine = "\n",
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Fixes references for <paramref name="moved"/> (old project path → new project path, one entry per moved file, and
    /// optionally per moved folder). Call after the move, with <paramref name="database"/> already updated. Returns the
    /// absolute paths of the files rewritten. Files that cannot be parsed or written are skipped (logged).
    /// </summary>
    public static IReadOnlyList<string> Apply(string projectRoot, AssetDatabase database, IReadOnlyDictionary<string, string> moved)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(moved);
        if (moved.Count == 0)
            return [];

        var root = Path.GetFullPath(projectRoot);
        var newPaths = new HashSet<string>(moved.Values, StringComparer.Ordinal);
        var updated = new List<string>();
        foreach (var file in CandidateFiles(root))
        {
            byte[] bytes;
            JsonNode? node;
            try
            {
                bytes = File.ReadAllBytes(file);
                node = JsonNode.Parse(bytes, documentOptions: ReadOptions);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                Log.Warning($"[FileSystem] Skipped references in '{Path.GetFileName(file)}': {e.Message}");
                continue;
            }

            if (node is null || !Fix(node, database, moved, newPaths))
                continue;

            try
            {
                // Songs are written like the project file (2-space indent), not in the scene layout.
                var sceneStyle = !string.Equals(Path.GetFileName(file), ProjectSettings.FileName, StringComparison.OrdinalIgnoreCase) &&
                                 !file.EndsWith(".msong", StringComparison.OrdinalIgnoreCase);
                AtomicFile.WriteAllBytes(file, Serialize(node, sceneStyle, endsWithNewLine: bytes.Length > 0 && bytes[^1] == (byte)'\n'));
                updated.Add(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error($"[FileSystem] Could not update references in '{Path.GetFileName(file)}': {e.Message}");
            }
        }

        return updated;
    }

    private static IEnumerable<string> CandidateFiles(string root)
    {
        var project = Path.Combine(root, ProjectSettings.FileName);
        if (File.Exists(project))
            yield return project;

        var content = Path.Combine(root, AssetDatabase.ContentFolder);
        if (!Directory.Exists(content))
            yield break;
        var files = Directory.EnumerateFiles(content, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
            .Where(f => f.EndsWith(".mscene", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mres", StringComparison.OrdinalIgnoreCase) ||
                        f.EndsWith(".msong", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
        foreach (var file in files)
            yield return file;
    }

    private static bool Fix(JsonNode node, AssetDatabase database, IReadOnlyDictionary<string, string> moved, HashSet<string> newPaths)
    {
        var changed = false;
        switch (node)
        {
            case JsonObject obj:
                var uid = StringOf(obj["ref"]) ?? StringOf(obj["instance"]);
                if (uid is not null && AssetUid.IsUid(uid) && StringOf(obj["path"]) is { } hint)
                {
                    var target = moved.GetValueOrDefault(hint);
                    if (target is null && database.GetPath(uid) is { } registered && newPaths.Contains(registered))
                        target = registered;
                    if (target is not null && !string.Equals(target, hint, StringComparison.Ordinal))
                    {
                        obj["path"] = target;
                        changed = true;
                    }
                }

                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    var child = obj[key];
                    if (StringOf(child) is { } text)
                    {
                        if (moved.TryGetValue(text, out var renamed))
                        {
                            obj[key] = renamed;
                            changed = true;
                        }
                    }
                    else if (child is not null)
                    {
                        changed |= Fix(child, database, moved, newPaths);
                    }
                }

                break;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++)
                {
                    var child = array[i];
                    if (StringOf(child) is { } text)
                    {
                        if (moved.TryGetValue(text, out var renamed))
                        {
                            array[i] = renamed;
                            changed = true;
                        }
                    }
                    else if (child is not null)
                    {
                        changed |= Fix(child, database, moved, newPaths);
                    }
                }

                break;
        }

        return changed;
    }

    private static string? StringOf(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    private static byte[] Serialize(JsonNode node, bool sceneStyle, bool endsWithNewLine)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, WriteOptions))
            node.WriteTo(writer);
        if (sceneStyle)
        {
            var laidOut = SceneFormat.FormatJson(buffer.WrittenSpan);
            buffer.Clear();
            buffer.Write(laidOut);
        }

        if (endsWithNewLine)
            buffer.Write("\n"u8);
        return buffer.WrittenSpan.ToArray();
    }
}
