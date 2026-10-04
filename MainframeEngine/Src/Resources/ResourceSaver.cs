using MainframeEngine.Serialization;

namespace MainframeEngine;

/// <summary>
/// Saves node trees as <c>.mscene</c> files (Godot's "Save Scene"): writes the nodes owned by the root, nested
/// instances as references plus overrides, non-default property values, the resources they use and persisted
/// signal connections. A file's UID survives re-saving.
/// </summary>
public static class SceneSaver
{
    /// <summary>Writes the scene rooted at <paramref name="root"/> to <paramref name="path"/>; returns its UID.</summary>
    public static string Save(Node root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrEmpty(path);
        var assets = AssetDatabase.Current;
        var fullPath = assets.ToAbsolutePath(path);
        var projectPath = assets.ToProjectPath(fullPath);
        var uid = ResourceSaver.ExistingUid(fullPath, projectPath) ?? AssetUid.Generate(AssetUid.ScenePrefix);

        var json = new SceneWriter().WriteScene(root, uid);
        ResourceSaver.WriteAtomically(fullPath, json);
        assets.Register(uid, projectPath);
        root.SceneFilePath = projectPath;

        // A cached copy of this scene (instanced elsewhere) picks up the new content.
        if (ResourceLoader.GetCached(uid) is PackedScene cached)
            cached.SetContent(json, projectPath);
        return uid;
    }

    /// <summary>Serializes the scene rooted at <paramref name="root"/> to JSON without touching disk.</summary>
    public static byte[] ToJson(Node root, string? uid = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        return new SceneWriter().WriteScene(root, uid);
    }
}

/// <summary>
/// Saves resources as <c>.mres</c> files. After saving, the resource is external: scenes reference it by UID
/// instead of embedding it.
/// </summary>
public static class ResourceSaver
{
    /// <summary>Writes <paramref name="resource"/> to <paramref name="path"/>; returns its UID.</summary>
    public static string Save(Resource resource, string path)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (resource is PackedScene)
            throw new ArgumentException("Save scenes with SceneSaver.Save(root, path).", nameof(resource));

        var assets = AssetDatabase.Current;
        var fullPath = assets.ToAbsolutePath(path);
        var projectPath = assets.ToProjectPath(fullPath);
        var uid = resource.Uid ?? ExistingUid(fullPath, projectPath) ?? AssetUid.Generate(AssetUid.ResourcePrefix);

        // Write before marking the resource external, so it is written inline as itself, not as a reference.
        var json = new SceneWriter().WriteResource(resource, uid);
        WriteAtomically(fullPath, json);

        resource.ResourcePath = projectPath;
        resource.Uid = uid;
        assets.Register(uid, projectPath);
        ResourceLoader.Register(resource);
        return uid;
    }

    internal static string? ExistingUid(string fullPath, string projectPath) =>
        (File.Exists(fullPath) ? AssetDatabase.ReadEmbeddedUid(fullPath) : null) ?? AssetDatabase.Current.GetUid(projectPath);

    internal static void WriteAtomically(string fullPath, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temp = fullPath + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, fullPath, overwrite: true);
    }
}
