using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>One ZzFX preset of the Sound FX scene: saved as <c>Content/Audio/Sfx/&lt;Id&gt;.mres</c> from a fixed seed.</summary>
public sealed record SoundFxPreset(string Id, string Name, Func<Random, ZzfxParameters> Make, int Seed)
{
    public string Path => $"{SoundFxScene.SfxFolder}/{Id}.mres";

    /// <summary>The preset's parameters (the same for every call: fixed seed).</summary>
    public ZzfxParameters Parameters => Make(new Random(Seed));
}

/// <summary>
/// ZzFX sounds synthesised at run time (presets, randomize, mutate) drawn as a waveform, and a looping song rendered by
/// the editor's music editor (<c>Content/Music/demo_loop.msong</c> → <c>demo_loop.ogg</c>) on the Music bus.
/// </summary>
public static class SoundFxScene
{
    public const string SfxFolder = "Content/Audio/Sfx";
    public const string MusicFile = "Content/Music/demo_loop.ogg";

    public static IReadOnlyList<SoundFxPreset> Presets { get; } =
    [
        new("coin", "Coin", ZzfxPresets.Coin, 3),
        new("laser", "Laser", ZzfxPresets.Laser, 3),
        new("explosion", "Explosion", ZzfxPresets.Explosion, 3),
        new("hit", "Hit", ZzfxPresets.Hit, 3),
        new("jump", "Jump", ZzfxPresets.Jump, 3),
        new("blip", "Blip", ZzfxPresets.Blip, 3),
        new("power_up", "Power-up", ZzfxPresets.PowerUp, 3),
    ];

    public static Node Build()
    {
        var root = new Node2D { Name = "sound_fx" };
        Add(root, root, new Camera2D { Name = "Camera", Current = true, Position = Vector2.Zero });
        Add(root, root, new DemoShape2D
        {
            Name = "Backdrop",
            Kind = DemoShapeKind.Rect,
            Size = new Vector2(1600, 900),
            Color = new Vector4(0.05f, 0.07f, 0.12f, 1),
            ColorBottom = new Vector4(0.1f, 0.05f, 0.16f, 1),
        });
        Add(root, root, new DemoShape2D
        {
            Name = "Screen",
            Kind = DemoShapeKind.Rect,
            Position = new Vector2(SoundWave2D.Center.X, SoundWave2D.Center.Y),
            Size = new Vector2(SoundWave2D.Width + 60, SoundWave2D.Height * 2 + 60),
            Color = new Vector4(0.02f, 0.03f, 0.06f, 0.85f),
            Outline = new Vector4(0.45f, 0.62f, 1f, 0.25f),
            OutlineWidth = 2f,
        });
        Add(root, root, new SoundWave2D { Name = "Wave" });
        // The panel swaps in the sound it plays; the coin keeps the player valid in the editor.
        Add(root, root, new AudioPlayer
        {
            Name = "Sfx",
            Bus = "SFX",
            VolumeDb = -4f,
            MaxPolyphony = 4,
            Stream = new ZzfxStream { ResourceName = Presets[0].Name, Parameters = Presets[0].Parameters },
        });
        Add(root, root, new AudioPlayer
        {
            Name = "Music",
            Bus = "Music",
            VolumeDb = -6f,
            Stream = new AudioStream { File = MusicFile, LoadMode = AudioLoadMode.Stream, Loop = true },
        });
        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new SoundFxPanel { Name = "Panel" });
        return root;
    }

    /// <summary><c>--write-scenes</c>: saves every preset as a <see cref="ZzfxStream"/> <c>.mres</c> into <paramref name="directory"/>.</summary>
    public static void WriteSounds(string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (var preset in Presets)
        {
            var stream = new ZzfxStream { ResourceName = preset.Name, Parameters = preset.Parameters };
            var uid = ResourceSaver.Save(stream, System.IO.Path.Combine(directory, preset.Id + ".mres"));
            Log.Info($"Wrote {preset.Id}.mres ({uid})");
        }
    }
}
