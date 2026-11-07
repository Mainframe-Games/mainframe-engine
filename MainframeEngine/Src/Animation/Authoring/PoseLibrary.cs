using System.Text.Json;

namespace MainframeEngine;

/// <summary>A clip <see cref="PoseLibrary.Write"/> refused to replace.</summary>
public sealed class PoseLibraryException(string message) : Exception(message);

/// <summary>What <see cref="PoseLibrary.Write"/> did.</summary>
public sealed class PoseLibraryWriteResult
{
    /// <summary>The library file did not exist and was created.</summary>
    public bool CreatedLibrary { get; init; }

    /// <summary>A clip of the same name was replaced.</summary>
    public bool ReplacedClip { get; init; }

    /// <summary>The library was added to the model's <c>.mfmeta</c> <c>libraries</c>.</summary>
    public bool Linked { get; init; }

    public required IReadOnlyList<string> Notes { get; init; }
}

/// <summary>
/// Writes baked clips into library <c>.mfres</c> files and links libraries to models (G1c). A clip that changed since it was
/// baked (its <see cref="Animation.BakedHash"/> no longer matches its keys, or it was never baked) is only replaced when
/// asked, so a re-bake never overwrites a person's hand fixes by accident.
/// </summary>
public static class PoseLibrary
{
    /// <summary>
    /// Adds <paramref name="clip"/> to the library at <paramref name="libraryPath"/> (created as the default library when
    /// missing) and saves it; with <paramref name="link"/>, adds the library to <paramref name="modelPath"/>'s
    /// <c>.mfmeta</c> <c>libraries</c> when it is not there yet. <paramref name="importedClips"/> (the model's own clip names)
    /// lets it note a baked clip that hides an imported one.
    /// </summary>
    /// <exception cref="PoseLibraryException">A clip of that name changed since it was baked and <paramref name="replace"/> is false.</exception>
    public static PoseLibraryWriteResult Write(Animation clip, string libraryPath, string modelPath, bool replace = false,
        bool link = true, IEnumerable<string>? importedClips = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentException.ThrowIfNullOrEmpty(libraryPath);
        ArgumentException.ThrowIfNullOrEmpty(clip.ResourceName);
        var notes = new List<string>();
        var created = !ResourceLoader.Exists(libraryPath);
        var library = created ? new AnimationLibrary { ResourceName = "" } : ResourceLoader.Load<AnimationLibrary>(libraryPath);

        var existing = library.Get(clip.ResourceName);
        if (existing is not null && !replace && !IsUntouchedBake(existing))
        {
            throw new PoseLibraryException(existing.BakedHash.Length == 0
                ? $"'{clip.ResourceName}' in {libraryPath} was not made by the baker; replace it explicitly (--replace) or bake under another name."
                : $"'{clip.ResourceName}' in {libraryPath} changed since it was baked (hand edits); edit it instead, replace it explicitly (--replace), or bake under another name.");
        }

        library.Add(clip.ResourceName, clip);
        ResourceSaver.Save(library, libraryPath);

        if (importedClips?.Contains(clip.ResourceName) == true)
            notes.Add($"The model imports its own '{clip.ResourceName}'; the baked clip plays instead (linked libraries come first).");

        var linked = link && Link(modelPath, AssetDatabase.Current.ToProjectPath(libraryPath));
        return new PoseLibraryWriteResult { CreatedLibrary = created, ReplacedClip = existing is not null, Linked = linked, Notes = notes };
    }

    /// <summary>True when <paramref name="clip"/> was baked and nobody changed its keys since.</summary>
    public static bool IsUntouchedBake(Animation clip) =>
        clip.BakedHash.Length > 0 && clip.BakedHash == AnimationHash.Compute(clip);

    /// <summary>
    /// Adds <paramref name="libraryPath"/> to <paramref name="modelPath"/>'s <c>.mfmeta</c> <c>libraries</c> (creating the
    /// sidecar when missing), changing nothing else in it. Returns false when it was already linked.
    /// </summary>
    public static bool Link(string modelPath, string libraryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(modelPath);
        ArgumentException.ThrowIfNullOrEmpty(libraryPath);
        var assets = AssetDatabase.Current;
        var meta = assets.ReadOrCreateMeta(modelPath, create: true)!;
        meta.Importer ??= ModelImportSettings.ImporterName;
        var settings = meta.Settings ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var libraries = settings.TryGetValue("libraries", out var current) && current.ValueKind == JsonValueKind.Array
            ? current.EnumerateArray().Select(e => e.GetString() ?? "").Where(p => p.Length > 0).ToList()
            : [];
        if (libraries.Contains(libraryPath, StringComparer.Ordinal))
            return false;
        libraries.Add(libraryPath);
        settings["libraries"] = JsonSerializer.SerializeToElement(libraries.ToArray(), AssetJsonContext.Default.StringArray);
        meta.Settings = settings;
        assets.WriteMeta(modelPath, meta);
        return true;
    }
}
