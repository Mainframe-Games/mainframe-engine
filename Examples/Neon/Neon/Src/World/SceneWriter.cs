using MainframeEngine;

namespace Neon;

/// <summary>
/// <c>--write-scenes &lt;dir&gt;</c>: regenerates Neon's scene files from their builders in <c>dir</c>
/// (<c>Assets/Scenes</c>), with resources referenced as the project's (two levels above <c>dir</c>).
/// </summary>
public static class SceneWriter
{
    public static int WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        var project = Path.GetFullPath(Path.Combine(directory, "..", ".."));
        var assets = AssetDatabase.Current;
        AssetDatabase.Current = new AssetDatabase(project);
        try
        {
            Write(directory, NeonScene.Id, NeonScene.Build());
        }
        finally
        {
            AssetDatabase.Current = assets;
        }

        return 0;
    }

    private static void Write(string directory, string id, Node root)
    {
        try
        {
            var uid = SceneSaver.Save(root, Path.Combine(directory, id + ".mscene"));
            Log.Info($"Wrote {id}.mscene ({uid})");
        }
        finally
        {
            root.Free();
        }
    }
}
