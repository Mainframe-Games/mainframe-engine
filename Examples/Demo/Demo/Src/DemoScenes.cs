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
        Scene("basic_3d", "Basic 3D", "cube", static () => Placeholder("basic_3d")),
        Scene("basic_2d", "Basic 2D", "square", static () => Placeholder("basic_2d")),
        Scene("audio_2d", "Audio 2D", "volume", static () => Placeholder("audio_2d")),
        Scene("audio_3d", "Audio 3D", "headphones", static () => Placeholder("audio_3d")),
        Scene("ui", "UI", "layout", static () => Placeholder("ui")),
        Scene("physics_2d", "Physics 2D", "circles", static () => Placeholder("physics_2d")),
        Scene("physics_3d", "Physics 3D", "box", static () => Placeholder("physics_3d")),
        Scene("spine", "Spine", "run", static () => Placeholder("spine")),
    ];

    public static DemoSceneInfo? ById(string id) => All.FirstOrDefault(s => s.Id == id);

    public static DemoSceneInfo? ByRootName(string? name) => name is null ? null : All.FirstOrDefault(s => s.Id == name);
}
