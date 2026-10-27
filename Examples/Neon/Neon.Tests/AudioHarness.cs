using System.Numerics;
using MainframeEngine;

namespace Neon.Tests;

/// <summary>
/// A <see cref="ControllerHarness"/> tree with an <see cref="AudioServer"/> on the manual null device and Neon's bus
/// layout (the Forest's harness): each <see cref="Frame"/> ticks the tree at 60 Hz and renders 1/60 s of the master mix
/// (48 kHz stereo) into <see cref="Capture"/>, the engine's own offline path. The stereo's stems go to a scratch folder.
/// </summary>
internal sealed class AudioHarness : IDisposable
{
    public const int Rate = 48000;
    public const int FramesPerTick = Rate / 60;

    /// <summary>The stems' folder for every test run (rendered once, reused by the others).</summary>
    public static readonly string MusicFolder = Path.Combine(Path.GetTempPath(), "neon-music-tests");

    private readonly float[] _block = new float[FramesPerTick * 2];

    public AudioHarness(bool floor = true)
    {
        Controller = new ControllerHarness(floor);
        Server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null }, Tree);
        Server.ApplyBusLayout(LoadLayout());
        Tree.Servers.Register(Server);
    }

    public ControllerHarness Controller { get; }

    public SceneTree Tree => Controller.Tree;

    public Node3D Scene => Controller.Scene;

    public AudioServer Server { get; }

    /// <summary>Everything rendered so far, interleaved stereo (null: frames are rendered but not kept).</summary>
    public List<float>? Capture { get; set; }

    public static AudioBusLayout LoadLayout() =>
        ResourceLoader.Load<AudioBusLayout>(Path.Combine(AppContext.BaseDirectory, "Assets", "Settings", "AudioBusLayout.mres"));

    /// <summary>A current camera at <paramref name="position"/> looking along −Z: the audio listener.</summary>
    public Camera3D AddListener(Vector3 position)
    {
        var camera = new Camera3D { Name = "Listener", Current = true, Position = position };
        Scene.AddChild(camera);
        return camera;
    }

    /// <summary>
    /// A <see cref="NeonAudio"/> in the scene (it finds the player and the apartment), the city's schedule off unless
    /// asked for, the stereo in memory.
    /// </summary>
    public NeonAudio AddAudio(bool cityEvents = false, bool preloadMusic = false, Action<NeonAudio>? configure = null)
    {
        var audio = new NeonAudio
        {
            Name = "Audio",
            CityEventInterval = cityEvents ? 9f : 0f,
            FlyByInterval = cityEvents ? 30f : 0f,
            PreloadMusic = preloadMusic,
            MusicFolder = MusicFolder,
            MusicInMemory = true,
        };
        configure?.Invoke(audio);
        Scene.AddChild(audio);
        return audio;
    }

    /// <summary>The graybox apartment (skips the test when its LFS buffer is a pointer).</summary>
    public NeonApartment AddApartment()
    {
        var bin = Path.Combine(AppContext.BaseDirectory, NeonApartment.BufferPath);
        if (!File.Exists(bin) || File.ReadAllText(bin).StartsWith("version https://git-lfs", StringComparison.Ordinal))
            Assert.Skip("Neon's LFS art is not checked out (git lfs pull).");
        var apartment = NeonApartment.Create();
        Scene.AddChild(apartment);
        return apartment;
    }

    /// <summary>Runs <paramref name="ticks"/> 60 Hz frames, rendering each.</summary>
    public void Run(int ticks, Action<int>? perTick = null)
    {
        for (var i = 0; i < ticks; i++)
        {
            perTick?.Invoke(i);
            Frame();
        }
    }

    public void RunSeconds(float seconds, Action<int>? perTick = null) => Run((int)MathF.Round(seconds * 60f), perTick);

    public void Frame()
    {
        Controller.Run(1);
        Server.RenderNullDevice(FramesPerTick, _block);
        Capture?.AddRange(_block);
    }

    public void Dispose() => Controller.Dispose();
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AudioCollection
{
    public const string Name = "Audio";
}
