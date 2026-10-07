using MainframeEngine;

namespace Demo;

/// <summary>
/// <c>--write-scenes &lt;dir&gt;</c>: regenerates every Demo scene file from its builder, and the Sound FX presets
/// (<c>&lt;dir&gt;/../Audio/Sfx/*.mres</c>).
/// </summary>
public static class SceneWriter
{
    public static int WriteAll(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var scene in DemoScenes.All)
        {
            var root = scene.Build();
            try
            {
                var uid = SceneSaver.Save(root, Path.Combine(directory, scene.Id + ".mscene"));
                Log.Info($"Wrote {scene.Id}.mscene ({uid})");
            }
            finally
            {
                root.Free();
            }
        }

        SoundFxScene.WriteSounds(Path.GetFullPath(Path.Combine(directory, "..", "Audio", "Sfx")));
        return 0;
    }
}
