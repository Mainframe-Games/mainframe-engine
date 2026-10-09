namespace MainframeEngine;

/// <summary>
/// Ez Tree's 15 tree and bush presets, as <see cref="TreeOptions"/> <c>.mres</c> files in the engine's
/// <c>Content/Trees/Presets/</c> (values copied from Ez Tree's JSON unchanged, plus the engine's <c>Scale</c> 0.3).
/// Listed under Ez Tree's display names, in its order (<c>src/lib/presets/index.js</c>).
/// </summary>
public static class TreePresets
{
    /// <summary>The engine folder holding the preset files.</summary>
    public const string Folder = "Content/Trees/Presets";

    private static readonly (string Name, string File)[] Entries =
    [
        ("Ash Small", "ash_small"),
        ("Ash Medium", "ash_medium"),
        ("Ash Large", "ash_large"),
        ("Aspen Small", "aspen_small"),
        ("Aspen Medium", "aspen_medium"),
        ("Aspen Large", "aspen_large"),
        ("Bush 1", "bush_1"),
        ("Bush 2", "bush_2"),
        ("Bush 3", "bush_3"),
        ("Oak Small", "oak_small"),
        ("Oak Medium", "oak_medium"),
        ("Oak Large", "oak_large"),
        ("Pine Small", "pine_small"),
        ("Pine Medium", "pine_medium"),
        ("Pine Large", "pine_large"),
    ];

    /// <summary>The display names, in Ez Tree's order.</summary>
    public static IReadOnlyList<string> Names { get; } = Array.ConvertAll(Entries, static e => e.Name);

    /// <summary>The file name (without extension) of a preset, e.g. <c>oak_medium</c> for "Oak Medium".</summary>
    public static string FileName(string name) =>
        Array.Find(Entries, e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)).File
        ?? throw new ArgumentException($"Unknown tree preset '{name}'. Presets: {string.Join(", ", Names)}.", nameof(name));

    /// <summary>
    /// The absolute path of a preset's <c>.mres</c>: always the engine's copy next to the application, also while the
    /// editor has a project open.
    /// </summary>
    public static string PathOf(string name) => ContentPaths.Resolve($"{Folder}/{FileName(name)}.mres", ContentPaths.BaseDirectory);

    /// <summary>
    /// A fresh, editable copy of the preset <paramref name="name"/> (<see cref="TreeOptions.Clone"/>): edits never touch
    /// the preset file or other copies.
    /// </summary>
    public static TreeOptions Load(string name)
    {
        var preset = ResourceLoader.Load<TreeOptions>(PathOf(name));
        try
        {
            var copy = preset.Clone();
            copy.ResourceName = Names[Array.FindIndex(Entries, e => e.File == FileName(name))];
            return copy;
        }
        finally
        {
            preset.Release();
        }
    }
}
