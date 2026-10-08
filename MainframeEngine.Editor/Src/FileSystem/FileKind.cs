namespace MainframeEngine.Editor;

/// <summary>What a file in the project is, by extension (folders are <see cref="Folder"/>).</summary>
public enum FileKind
{
    Folder,
    Scene,
    Resource,
    Texture,
    Audio,

    /// <summary>An editor song (<c>.msong</c>, the music editor).</summary>
    Song,
    Model,
    Font,
    Rml,
    Rcss,
    Translation,
    Shader,
    Script,
    Project,
    Data,
    Other,
}

/// <summary>Problems and states the FileSystem panel shows on an entry.</summary>
[Flags]
public enum FileBadges
{
    None = 0,

    /// <summary>The file is open in the editor with unsaved changes.</summary>
    Unsaved = 1,

    /// <summary>A scene or resource references a file that does not exist.</summary>
    MissingDependency = 2,

    /// <summary>The file cannot be read (invalid JSON, an image that does not decode).</summary>
    ImportError = 4,

    /// <summary>A song whose rendered output is older than the song.</summary>
    RenderOutOfDate = 8,
}

/// <summary>Extension → <see cref="FileKind"/>, and which extensions count as asset references.</summary>
internal static class FileKinds
{
    public static FileKind Of(string path, bool isDirectory)
    {
        if (isDirectory)
            return FileKind.Folder;
        if (string.Equals(Path.GetFileName(path), ProjectSettings.FileName, StringComparison.OrdinalIgnoreCase))
            return FileKind.Project;
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".mscene" => FileKind.Scene,
            ".mres" => FileKind.Resource,
            ".msong" => FileKind.Song,
            ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp" or ".hdr" or ".ktx" or ".ktx2" => FileKind.Texture,
            ".wav" or ".ogg" or ".mp3" or ".flac" => FileKind.Audio,
            ".gltf" or ".glb" or ".fbx" or ".obj" or ".dae" => FileKind.Model,
            ".ttf" or ".otf" or ".woff" or ".woff2" => FileKind.Font,
            ".rml" => FileKind.Rml,
            ".rcss" => FileKind.Rcss,
            ".po" or ".pot" or ".mo" => FileKind.Translation,
            ".slang" or ".vert" or ".frag" or ".comp" or ".glsl" or ".hlsl" or ".spv" => FileKind.Shader,
            ".cs" => FileKind.Script,
            ".mfproj" or ".csproj" or ".sln" or ".slnx" or ".props" or ".targets" => FileKind.Project,
            ".json" or ".xml" or ".yaml" or ".yml" or ".csv" or ".txt" or ".md" or ".atlas" or ".skel" => FileKind.Data,
            _ => FileKind.Other,
        };
    }

    /// <summary>True for files a thumbnail can be decoded from (StbImageSharp formats).</summary>
    public static bool HasThumbnail(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".tga" or ".bmp";

    /// <summary>
    /// True for <c>Content/…</c> strings ending in an asset extension: property values that reference a file by path
    /// (textures, sky panoramas, file-hinted strings).
    /// </summary>
    public static bool LooksLikeAssetPath(string value)
    {
        if (!value.StartsWith("Content/", StringComparison.Ordinal) || value.Length > 1024)
            return false;
        var kind = Of(value, isDirectory: false);
        return kind is not (FileKind.Other or FileKind.Folder or FileKind.Project or FileKind.Script);
    }
}
