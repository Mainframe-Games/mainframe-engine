using MainframeEngine;

namespace Forest;

/// <summary>
/// <c>--write-scenes &lt;dir&gt;</c>: regenerates the Forest's scene files from their builders and the colour-grading LUT
/// (<see cref="ForestGrade.LutPath"/>) in the project folder two levels above <c>dir</c> (<c>Content/Scenes</c>), and
/// writes the post-processing look (<see cref="ForestLook"/>) when its files are missing (they are tuned in the editor).
/// </summary>
public static class SceneWriter
{
    public static int WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        var project = Path.GetFullPath(Path.Combine(directory, "..", ".."));
        var lut = ForestGrade.WriteLut(project);
        Log.Info($"Wrote {lut}");

        // The resource files are saved and referenced as the project's (UIDs and paths relative to it).
        var assets = AssetDatabase.Current;
        AssetDatabase.Current = new AssetDatabase(project);
        try
        {
            foreach (var written in ForestLook.WriteMissing())
                Log.Info($"Wrote {written}");
            Write(directory, ForestScene.Id, ForestScene.Build());
            Write(directory, ForestAssetGallery.SceneId, new ForestAssetGallery { Name = ForestAssetGallery.SceneId });
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
