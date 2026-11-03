using MainframeEngine;

namespace Demo;

public sealed record DemoSceneInfo(string Id, string Title, string Category, string Icon, string Path, Func<Node> Build);

/// <summary>A nav-bar category: the top row's tabs, each holding a few scenes (the second row).</summary>
public sealed record DemoCategoryInfo(string Id, string Title);

/// <summary>The Demo's scenes, in nav-bar order (grouped by category). A scene's root node is named after its id.</summary>
public static class DemoScenes
{
    private static DemoSceneInfo Scene(string id, string title, string category, string icon, Func<Node> build) =>
        new(id, title, category, icon, $"Assets/Scenes/{id}.mscene", build);

    public static IReadOnlyList<DemoCategoryInfo> Categories { get; } =
    [
        new("3d", "3D"),
        new("2d", "2D"),
        new("physics", "Physics"),
        new("animation", "Animation"),
        new("audio", "Audio"),
        new("ui", "UI"),
    ];

    public static IReadOnlyList<DemoSceneInfo> All { get; } =
    [
        Scene("basic_3d", "Basic 3D", "3d", "cube", Basic3DScene.Build),
        Scene("particles", "Particles", "3d", "flame", ParticlesScene.Build),
        Scene("shaders", "Shaders", "3d", "palette", ShadersScene.Build),
        Scene("basic_2d", "Basic 2D", "2d", "square", Basic2DScene.Build),
        Scene("sprites_2d", "Sprites 2D", "2d", "slideshow", Sprites2DScene.Build),
        Scene("tiles_2d", "Tiles 2D", "2d", "grid-pattern", Tiles2DScene.Build),
        Scene("navigation_2d", "Navigation 2D", "2d", "route", Navigation2DScene.Build),
        Scene("physics_2d", "Physics 2D", "physics", "circles", Physics2DScene.Build),
        Scene("physics_3d", "Physics 3D", "physics", "box", Physics3DScene.Build),
        Scene("spine", "Spine", "animation", "run", SpineScene.Build),
        Scene("animation_3d", "Animation 3D", "animation", "bone", Animation3DScene.Build),
        Scene("audio_2d", "Audio 2D", "audio", "volume", Audio2DScene.Build),
        Scene("audio_3d", "Audio 3D", "audio", "headphones", Audio3DScene.Build),
        Scene("sound_fx", "Sound FX", "audio", "music", SoundFxScene.Build),
        Scene("adaptive_music", "Music", "audio", "playlist", AdaptiveMusicScene.Build),
        Scene("ui", "UI", "ui", "layout", UiScene.Build),
    ];

    public static DemoSceneInfo? ById(string id) => All.FirstOrDefault(s => s.Id == id);

    public static DemoSceneInfo? ByRootName(string? name) => name is null ? null : All.FirstOrDefault(s => s.Id == name);

    /// <summary>The index of <paramref name="categoryId"/> in <see cref="Categories"/>, or -1.</summary>
    public static int CategoryIndex(string categoryId)
    {
        for (var i = 0; i < Categories.Count; i++)
        {
            if (Categories[i].Id == categoryId)
                return i;
        }
        return -1;
    }
}
