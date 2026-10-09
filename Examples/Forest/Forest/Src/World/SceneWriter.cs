using MainframeEngine;

namespace Forest;

/// <summary><c>--write-scenes &lt;dir&gt;</c>: regenerates the Forest's scene files from their builders.</summary>
public static class SceneWriter
{
    public static int WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        var root = ForestScene.Build();
        try
        {
            var uid = SceneSaver.Save(root, Path.Combine(directory, ForestScene.Id + ".mscene"));
            Log.Info($"Wrote {ForestScene.Id}.mscene ({uid})");
        }
        finally
        {
            root.Free();
        }

        return 0;
    }
}
