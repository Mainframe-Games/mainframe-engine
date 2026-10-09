using MainframeEngine;

namespace Forest;

/// <summary>
/// <c>--write-scenes &lt;dir&gt;</c>: regenerates the Forest's scene files from their builders, and the colour-grading LUT
/// (<see cref="ForestGrade.LutPath"/>) in the project folder two levels above <c>dir</c> (<c>Content/Scenes</c>).
/// </summary>
public static class SceneWriter
{
    public static int WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        var lut = ForestGrade.WriteLut(Path.GetFullPath(Path.Combine(directory, "..", "..")));
        Log.Info($"Wrote {lut}");
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
