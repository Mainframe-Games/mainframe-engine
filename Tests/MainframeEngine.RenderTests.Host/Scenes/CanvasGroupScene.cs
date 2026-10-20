using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// <see cref="CanvasGroup"/> (#109), Godot 4.7.2's own check rebuilt: on black, two pairs of opaque white 40 px squares
/// overlapping by half, each pair in a canvas group. The left group fades with <c>Modulate</c> 0.5 (it also reaches the
/// children, so the overlap shows), the right one with <c>SelfModulate</c> 0.5 (no seam). Under a centred
/// <see cref="Camera2D"/> and canvas_items stretch from 200×100.
/// </summary>
public sealed class CanvasGroupScene(HostOptions host) : RenderTestGame(host)
{
    protected override void LoadScene()
    {
        Servers.Get<CanvasServer>()!.ClearColor = new Vector4(0, 0, 0, 1);
        Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        Root.ContentScaleAspect = ContentScaleAspect.Keep;
        Root.ContentScaleSize = new Vector2(200, 100);
        var root = new Node2D { Name = "Canvas" };
        root.AddChild(new Camera2D { Name = "Camera", Position = new Vector2(100, 50) });
        root.AddChild(Pair("Modulated", 0, selfModulate: false));
        root.AddChild(Pair("SelfModulated", 100, selfModulate: true));
        Tree.ChangeScene(root);
    }

    private static CanvasGroup Pair(string name, float x, bool selfModulate)
    {
        var group = new CanvasGroup { Name = name, Position = new Vector2(x, 0) };
        group.AddChild(new Square { Name = "Open", Position = new Vector2(10, 30) });
        group.AddChild(new Square { Name = "Fist", Position = new Vector2(30, 30) });
        if (selfModulate)
            group.SelfModulate = new Vector4(1, 1, 1, 0.5f);
        else
            group.Modulate = new Vector4(1, 1, 1, 0.5f);
        return group;
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }

    private sealed class Square : Node2D
    {
        protected override void OnDraw() => DrawRect(new Rect2(0, 0, 40, 40), Vector4.One);
    }
}
