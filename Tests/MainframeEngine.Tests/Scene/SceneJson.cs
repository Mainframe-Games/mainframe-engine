using System.Text.Json;

namespace MainframeEngine.Tests.Scene;

/// <summary>Reads node entries from saved scene JSON (format 2: a flat <c>"nodes"</c> list with parent paths).</summary>
internal static class SceneJson
{
    public static JsonElement Parse(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        return doc.RootElement.Clone();
    }

    /// <summary>The root node's entry.</summary>
    public static JsonElement Root(JsonElement file) => file.GetProperty("nodes")[0];

    /// <summary>The entry of the node at <paramref name="path"/> from the root (<c>"."</c> is the root).</summary>
    public static JsonElement Node(JsonElement file, string path)
    {
        foreach (var entry in file.GetProperty("nodes").EnumerateArray().Skip(1))
        {
            var parent = entry.GetProperty("parent").GetString()!;
            var name = entry.GetProperty("name").GetString()!;
            if ((parent == "." ? name : $"{parent}/{name}") == path)
                return entry;
        }

        return path == "." ? Root(file) : throw new KeyNotFoundException($"No node '{path}' in the saved scene.");
    }

    /// <summary>The entries whose parent is <paramref name="parentPath"/>, in order.</summary>
    public static JsonElement[] ChildrenOf(JsonElement file, string parentPath) =>
        file.GetProperty("nodes").EnumerateArray().Skip(1)
            .Where(e => e.GetProperty("parent").GetString() == parentPath)
            .ToArray();
}
