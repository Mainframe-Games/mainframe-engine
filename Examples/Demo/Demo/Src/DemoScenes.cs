using MainframeEngine;

namespace Demo;

public sealed record DemoSceneInfo(string Id, string Title, string Icon, string Path, Func<Node> Build);

/// <summary>The Demo's tabs, in nav-bar order. A scene's root node is named after its id.</summary>
public static class DemoScenes
{
    private static DemoSceneInfo Scene(string id, string title, string icon, Func<Node> build) =>
        new(id, title, icon, $"Content/Scenes/{id}.mscene", build);

    // Placeholder builders until each feature scene lands.
    private static Node Placeholder(string id) => new Node3D { Name = id };

    public static IReadOnlyList<DemoSceneInfo> All { get; } =
    [
        Scene("basic_3d", "Basic 3D", "cube", Basic3DScene.Build),
        Scene("basic_2d", "Basic 2D", "square", static () => Placeholder("basic_2d")),
        Scene("audio_2d", "Audio 2D", "volume", Audio2DScene.Build),
        Scene("audio_3d", "Audio 3D", "headphones", Audio3DScene.Build),
        Scene("ui", "UI", "layout", UiScene.Build),
        Scene("physics_2d", "Physics 2D", "circles", Physics2DScene.Build),
        Scene("physics_3d", "Physics 3D", "box", Physics3DScene.Build),
        Scene("spine", "Spine", "run", static () => Placeholder("spine")),
    ];

    public static DemoSceneInfo? ById(string id) => All.FirstOrDefault(s => s.Id == id);

    public static DemoSceneInfo? ByRootName(string? name) => name is null ? null : All.FirstOrDefault(s => s.Id == name);
}
