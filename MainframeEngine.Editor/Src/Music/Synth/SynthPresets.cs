using System.Text.Json;

namespace MainframeEngine.Editor.Music;

/// <summary>A preset the synth's menu lists: a factory one (built in code) or a project <c>.mres</c>.</summary>
/// <param name="Id"><c>factory:&lt;category&gt;/&lt;name&gt;</c> or the resource's UID (else its project path).</param>
public sealed record SynthPresetEntry(string Id, string Category, string Name, string? Path);

/// <summary>
/// The wavetable synth's presets: the factory ones (<see cref="FactoryPresets"/>) and the project's
/// <see cref="WavetablePreset"/> resources (found by type under <c>Content/</c>); patches to and from their JSON.
/// </summary>
public static class SynthPresets
{
    public const string FactoryPrefix = "factory:";

    /// <summary>The default folder for saved presets.</summary>
    public const string Folder = "Presets/Wavetable";

    /// <summary>The patch JSON a preset stores (compact; the song's own writer formats songs).</summary>
    public static string ToJson(SynthPatch patch) => JsonSerializer.Serialize(patch, SongJsonContext.Default.SynthPatch);

    /// <summary>A patch from preset JSON, or null when it is not one.</summary>
    public static SynthPatch? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize(json, SongJsonContext.Default.SynthPatch);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Every factory preset, then the project's (by category, then name).</summary>
    public static List<SynthPresetEntry> All(string? projectRoot)
    {
        var list = FactoryPresets.All.Select(static p => new SynthPresetEntry(FactoryPrefix + p.Category + "/" + p.Name, p.Category, p.Name, null)).ToList();
        list.AddRange(Project(projectRoot).OrderBy(static p => p.Category, StringComparer.OrdinalIgnoreCase).ThenBy(static p => p.Name, StringComparer.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>The project's <see cref="WavetablePreset"/> resources (their root type peeked, not loaded).</summary>
    public static List<SynthPresetEntry> Project(string? projectRoot)
    {
        var content = projectRoot is null ? null : System.IO.Path.Combine(projectRoot, AssetDatabase.ContentFolder);
        var result = new List<SynthPresetEntry>();
        if (content is null || !Directory.Exists(content))
            return result;
        try
        {
            foreach (var file in Directory.EnumerateFiles(content, "*.mres", SearchOption.AllDirectories))
            {
                var info = new FileInfo(file);
                if (FilePeek.Read(file, FileKind.Resource, info.Length, info.LastWriteTimeUtc).RootTypeName != nameof(WavetablePreset))
                    continue;
                if (Load(file) is not { } preset)
                    continue;
                var uid = AssetDatabase.Current.GetUid(AssetDatabase.Current.ToProjectPath(file));
                result.Add(new SynthPresetEntry(uid ?? file, string.IsNullOrWhiteSpace(preset.Category) ? "Project" : preset.Category.Trim(),
                    string.IsNullOrWhiteSpace(preset.ResourceName) ? System.IO.Path.GetFileNameWithoutExtension(file) : preset.ResourceName, file));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warning($"[Music] Could not list the project's synth presets: {e.Message}");
        }

        return result;
    }

    /// <summary>The patch of preset <paramref name="entry"/> (a fresh copy), or null when it cannot be read.</summary>
    public static SynthPatch? Patch(SynthPresetEntry entry)
    {
        if (entry.Path is null)
            return FactoryPresets.All.FirstOrDefault(p => FactoryPrefix + p.Category + "/" + p.Name == entry.Id)?.Make();
        return FromJson(Load(entry.Path)?.Patch);
    }

    // The preset's fields read straight from its .mres (the resource loader caches; a preset edited on disk must show).
    private static WavetablePreset? Load(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var type) || type.GetString() != nameof(WavetablePreset) || !root.TryGetProperty("props", out var props))
                return null;
            string Text(string name) => props.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            return new WavetablePreset { ResourceName = Text("ResourceName"), Category = Text("Category"), Author = Text("Author"), Tags = Text("Tags"), Patch = Text("Patch") };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warning($"[Music] {System.IO.Path.GetFileName(path)} is not a readable synth preset: {e.Message}");
            return null;
        }
    }

    /// <summary>Saves <paramref name="patch"/> as a preset resource at <paramref name="path"/> (a <c>.mres</c>); returns its UID.</summary>
    public static string Save(SynthPatch patch, string path, string name, string category)
    {
        var preset = new WavetablePreset { ResourceName = name, Category = category, Patch = ToJson(patch) };
        return ResourceSaver.Save(preset, path);
    }
}
