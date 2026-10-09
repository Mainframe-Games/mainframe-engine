using MainframeEngine;

namespace Forest;

/// <summary><c>--write-scenes &lt;dir&gt;</c>: regenerates the Forest's scene files from their builders.</summary>
public static class SceneWriter
{
    public static int WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        Write(directory, ForestScene.Id, ForestScene.Build());
        Write(directory, ForestAssetGallery.SceneId, new ForestAssetGallery { Name = ForestAssetGallery.SceneId });
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
