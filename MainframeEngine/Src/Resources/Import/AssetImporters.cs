namespace MainframeEngine;

/// <summary>
/// Turns a source file (image, model, ...) into a <see cref="Resource"/> when <see cref="ResourceLoader"/> loads it,
/// using the import settings of the file's <c>.meta</c> sidecar (<see cref="AssetMeta.Settings"/>).
/// </summary>
public interface IAssetImporter
{
    /// <summary>Name stored in <see cref="AssetMeta.Importer"/> (<c>texture</c>, <c>model</c>, ...).</summary>
    string Name { get; }

    /// <summary>Lower-case file extensions handled, with the dot.</summary>
    IReadOnlyList<string> Extensions { get; }

    /// <summary>Imports <paramref name="fullPath"/> (project path <paramref name="projectPath"/>) with <paramref name="meta"/>'s settings.</summary>
    Resource Import(string fullPath, string projectPath, AssetMeta? meta);
}

/// <summary>The importers <see cref="ResourceLoader"/> uses for files other than <c>.mscene</c>/<c>.mres</c>.</summary>
public static class AssetImporters
{
    private static readonly Lock Gate = new();
    private static readonly Dictionary<string, IAssetImporter> ByExtension = new(StringComparer.OrdinalIgnoreCase);

    static AssetImporters()
    {
        Register(new TextureImporter());
        Register(new ModelImporter());
    }

    /// <summary>Registers (or replaces) the importer for its extensions.</summary>
    public static void Register(IAssetImporter importer)
    {
        ArgumentNullException.ThrowIfNull(importer);
        lock (Gate)
        {
            foreach (var extension in importer.Extensions)
                ByExtension[extension] = importer;
        }
    }

    /// <summary>The importer for <paramref name="path"/>'s extension, or null.</summary>
    public static IAssetImporter? Find(string path)
    {
        var extension = Path.GetExtension(path);
        lock (Gate)
            return ByExtension.GetValueOrDefault(extension);
    }

    /// <summary>Every handled extension.</summary>
    public static IReadOnlyCollection<string> Extensions
    {
        get
        {
            lock (Gate)
                return [.. ByExtension.Keys];
        }
    }
}

/// <summary>Images (PNG, JPEG, TGA, BMP) → <see cref="Texture2D"/> with <see cref="TextureImportSettings"/>.</summary>
public sealed class TextureImporter : IAssetImporter
{
    public string Name => TextureImportSettings.ImporterName;

    public IReadOnlyList<string> Extensions { get; } = [".png", ".jpg", ".jpeg", ".tga", ".bmp"];

    public Resource Import(string fullPath, string projectPath, AssetMeta? meta) =>
        Texture2D.FromFile(fullPath, TextureImportSettings.FromMeta(meta, projectPath));
}
