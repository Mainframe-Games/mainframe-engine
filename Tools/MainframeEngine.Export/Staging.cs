using System.Text.Json;
using System.Text.Json.Nodes;

namespace MainframeEngine.Export;

/// <summary>
/// Removes what another platform needs from a publish folder: a checkout build copies the engine's natives of every
/// desktop platform (and both Steamworks shim builds), so an export keeps only the target RID's. The list is the engine's
/// <c>runtimes/&lt;rid&gt;/native</c> files (<see cref="EngineNatives"/>), so a new native is pruned too. A package build
/// already has only the RID's; then nothing is removed.
/// </summary>
public static class NativesPruner
{
    /// <summary>The Steamworks shim folder each platform loads (<c>SteamworksLoader</c>).</summary>
    public static string SteamworksVariant(string rid) => rid.StartsWith("win-", StringComparison.Ordinal) ? "win64" : "posix";

    /// <summary>The Steam API native of a RID's platform.</summary>
    public static string SteamApiNative(string rid) =>
        rid.StartsWith("win-", StringComparison.Ordinal) ? "steam_api64.dll"
        : rid.StartsWith("osx-", StringComparison.Ordinal) ? "libsteam_api.dylib"
        : "libsteam_api.so";

    /// <summary>Prunes <paramref name="publishDirectory"/> for <paramref name="rid"/>; returns the removed paths (relative, '/').</summary>
    public static IReadOnlyList<string> Prune(string publishDirectory, string rid) =>
        Prune(publishDirectory, rid, EngineNatives.Rids.ToDictionary(r => r, r => EngineNatives.FilesOf(r), StringComparer.Ordinal));

    /// <summary>Prunes with an explicit natives table (tests): RID → file names.</summary>
    public static IReadOnlyList<string> Prune(string publishDirectory, string rid, IReadOnlyDictionary<string, IReadOnlyList<string>> natives)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(publishDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(rid);
        ArgumentNullException.ThrowIfNull(natives);
        var keep = new HashSet<string>(natives.TryGetValue(rid, out var own) ? own : [], StringComparer.Ordinal);
        var removed = new List<string>();
        foreach (var (other, files) in natives)
        {
            if (other == rid)
                continue;
            foreach (var file in files)
            {
                if (keep.Contains(file))
                    continue; // osx-arm64 and osx-x64 share their (universal) files
                var path = Path.Combine(publishDirectory, file);
                if (File.Exists(path))
                {
                    File.Delete(path);
                    removed.Add(file);
                }
            }
        }

        var otherVariant = SteamworksVariant(rid) == "win64" ? "posix" : "win64";
        var steamworks = Path.Combine(publishDirectory, "steamworks", otherVariant);
        if (Directory.Exists(steamworks))
        {
            Directory.Delete(steamworks, recursive: true);
            removed.Add("steamworks/" + otherVariant);
        }

        removed.Sort(StringComparer.Ordinal);
        return removed;
    }
}

/// <summary>
/// The <c>project.mfproj</c> an export ships (docs/design/game-export.md#exported-project-file): the project's settings
/// with the presets removed (output paths and signing identities are not the player's business), <c>isDemo</c> and the
/// version set from the preset, and Steam's app ids zeroed when the preset has Steam off.
/// </summary>
public static class ExportedProjectFile
{
    /// <summary>The exported settings (a copy: <paramref name="settings"/> is unchanged).</summary>
    public static ProjectSettings Transform(ProjectSettings settings, ExportPreset preset, string version)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(version);
        var copy = ProjectSettings.Parse(settings.ToJson(), settings.FilePath ?? ProjectSettings.FileName);
        copy.ExportPresets.Clear();
        copy.IsDemo = preset.Demo;
        copy.Version = version;
        if (preset.Options is DesktopExportOptions { Steam: false })
        {
            copy.Steam.AppId = 0;
            copy.Steam.DemoAppId = 0;
            copy.Steam.RestartThroughSteam = false;
        }

        return copy;
    }
}

/// <summary>
/// The references a scene or resource file makes (the read-only half of the editor's <c>ReferenceFixer</c> walk):
/// <c>{"ref"|"instance": uid, "path": hint}</c> objects, resolved through the project's <see cref="AssetDatabase"/> (else
/// the hint), and exact <c>Assets/…</c> path strings.
/// </summary>
public static class ReferenceScanner
{
    private static readonly JsonDocumentOptions ReadOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    /// <summary>The project paths <paramref name="file"/> references (empty when it is not JSON).</summary>
    public static IReadOnlyCollection<string> ReferencesOf(string file, AssetDatabase database)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);
        ArgumentNullException.ThrowIfNull(database);
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllBytes(file), documentOptions: ReadOptions);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        Walk(root, database, found);
        return found;
    }

    /// <summary>Whether <paramref name="path"/> is a file whose references are scanned (<c>.mscene</c>, <c>.mres</c>).</summary>
    public static bool IsScanned(string path) =>
        path.EndsWith(".mscene", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".mres", StringComparison.OrdinalIgnoreCase);

    private static void Walk(JsonNode? node, AssetDatabase database, HashSet<string> found)
    {
        switch (node)
        {
            case JsonObject obj:
                var uid = StringOf(obj["ref"]) ?? StringOf(obj["instance"]);
                var isReference = uid is not null && AssetUid.IsUid(uid);
                if (isReference)
                {
                    var path = database.GetPath(uid!) ?? StringOf(obj["path"]);
                    if (path is not null)
                        found.Add(ContentPaths.UpgradeLegacyPath(path));
                }

                foreach (var (key, child) in obj)
                    if (!(isReference && key == "path")) // a reference's path is only a hint: the UID decides
                        Walk(child, database, found);
                break;
            case JsonArray array:
                foreach (var child in array)
                    Walk(child, database, found);
                break;
            case JsonValue value when value.TryGetValue<string>(out var text) && text.StartsWith(ContentPaths.FolderName + "/", StringComparison.Ordinal):
                found.Add(text);
                break;
        }
    }

    private static string? StringOf(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
