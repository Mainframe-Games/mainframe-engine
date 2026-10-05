using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>A looping emitter sweeps past the camera (the 2D listener): panning and distance attenuation; click for a blip.</summary>
public static class Audio2DScene
{
    public static Node Build()
    {
        var root = new Node2D { Name = "audio_2d" };
        Add(root, root, new Camera2D { Name = "Camera", Current = true, Position = Vector2.Zero });
        Add(root, root, new DemoShape2D
        {
            Name = "Backdrop",
            Kind = DemoShapeKind.Rect,
            Size = new Vector2(1600, 900),
            Color = new Vector4(0.06f, 0.08f, 0.14f, 1),
            ColorBottom = new Vector4(0.12f, 0.06f, 0.18f, 1),
        });
        Add(root, root, new DemoShape2D
        {
            Name = "Listener",
            Kind = DemoShapeKind.Circle,
            Radius = 26f,
            Color = new Vector4(0.89f, 0.91f, 0.94f, 1),
            Outline = new Vector4(1, 1, 1, 0.4f),
            OutlineWidth = 3f,
        });
        var sweeper = Add(root, root, new Sweeper2D { Name = "Sweeper", Position = new Vector2(0, -140) });
        Add(root, sweeper, new DemoShape2D { Name = "Marker", Kind = DemoShapeKind.Circle, Radius = 20f, Color = new Vector4(0.15f, 0.39f, 0.92f, 1) });
        Add(root, sweeper, new AudioPlayer2D
        {
            Name = "Emitter",
            Bus = "SFX",
            VolumeDb = -6f,
            Autoplay = true,
            MaxDistance = 900f,
            Stream = new AudioStream { File = "Content/Audio/ambient_hum.ogg", LoadMode = AudioLoadMode.Stream, Loop = true },
        });
        Add(root, root, new ClickToPlay2D { Name = "Clicks" });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Audio2DPanel { Name = "Panel" });
        return root;
    }
}
