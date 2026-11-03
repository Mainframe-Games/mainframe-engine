using Spine;

namespace MainframeEngine;

/// <summary>
/// Reads Spine exports through <see cref="ContentFiles"/>: the vendored runtime's path overloads open files with
/// <see cref="FileStream"/>, so the engine uses its stream overloads instead (the plugin is not modified).
/// </summary>
internal static class SpineContent
{
    /// <summary>An <c>.atlas</c>; its pages load through <paramref name="textureLoader"/> from the atlas's folder.</summary>
    public static Atlas ReadAtlas(string path, TextureLoader textureLoader)
    {
        using var reader = new StreamReader(ContentFiles.OpenRead(path));
        try
        {
            return new Atlas(reader, Path.GetDirectoryName(path) ?? "", textureLoader);
        }
        catch (Exception e) when (e is not IOException)
        {
            throw new InvalidDataException($"Error reading atlas file: {path}", e);
        }
    }

    /// <summary>A skeleton export: a binary <c>.skel</c>, or a <c>.json</c> (by extension).</summary>
    public static SkeletonData ReadSkeletonData(string path, Atlas atlas)
    {
        SkeletonData data;
        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            using var reader = new StreamReader(ContentFiles.OpenRead(path));
            data = new SkeletonJson(atlas).ReadSkeletonData(reader);
        }
        else
        {
            using var stream = ContentFiles.OpenRead(path);
            data = new SkeletonBinary(atlas).ReadSkeletonData(stream);
        }

        data.Name = Path.GetFileNameWithoutExtension(path);
        return data;
    }
}
