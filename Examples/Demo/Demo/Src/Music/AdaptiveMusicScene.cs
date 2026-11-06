using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// Adaptive music (docs/design/adaptive-music.md): the Demo's adaptive song (<c>Assets/Music/adaptive_theme.mfsong</c>,
/// made in the editor's music mode and baked with <c>just bake-song</c> to <c>adaptive_theme.mfmusic</c> and its stems)
/// on an <see cref="AdaptiveMusicPlayer"/>. <c>intensity</c> fades the Perc and Lead layers in (the Lead's low-pass
/// opens with it), <c>area</c> moves Explore to Combat at the next bar and back with the Hit stinger;
/// <see cref="MusicVisualizer2D"/> draws the sections, the beats and the layers' gains.
/// </summary>
public static class AdaptiveMusicScene
{
    public const string MusicFile = "Assets/Music/adaptive_theme.mfmusic";

    public static Node Build()
    {
        var root = new Node2D { Name = "adaptive_music" };
        Add(root, root, new Camera2D { Name = "Camera", Current = true, Position = Vector2.Zero });
        Add(root, root, new DemoShape2D
        {
            Name = "Backdrop",
            Kind = DemoShapeKind.Rect,
            Size = new Vector2(1600, 900),
            Color = new Vector4(0.04f, 0.05f, 0.1f, 1),
            ColorBottom = new Vector4(0.1f, 0.04f, 0.12f, 1),
        });
        Add(root, root, new AdaptiveMusicPlayer
        {
            Name = "Music",
            Music = ResourceLoader.Load<AdaptiveMusic>(MusicFile),
            Bus = "Music",
            VolumeDb = -4f,
            Autoplay = true,
        });
        Add(root, root, new MusicVisualizer2D { Name = "Visualizer" });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new AdaptiveMusicPanel { Name = "Panel" });
        return root;
    }

}
