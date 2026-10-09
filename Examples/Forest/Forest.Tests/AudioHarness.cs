using System.Numerics;
using MainframeEngine;

namespace Forest.Tests;

/// <summary>
/// A <see cref="ControllerHarness"/> tree with an <see cref="AudioServer"/> on the manual null device and the Forest's
/// bus layout: each <see cref="Frame"/> ticks the tree at 60 Hz and renders 1/60 s of the master mix (48 kHz stereo)
/// into <see cref="Capture"/>, the engine's own offline path.
/// </summary>
internal sealed class AudioHarness : IDisposable
{
    public const int Rate = 48000;
    public const int FramesPerTick = Rate / 60;

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
        ResourceLoader.Load<AudioBusLayout>(Path.Combine(AppContext.BaseDirectory, "Content", "Settings", "AudioBusLayout.mres"));

    /// <summary>A current camera at <paramref name="position"/> looking along −Z: the audio listener.</summary>
    public Camera3D AddListener(Vector3 position)
    {
        var camera = new Camera3D { Name = "Listener", Current = true, Position = position };
        Scene.AddChild(camera);
        return camera;
    }

    /// <summary>The Forest test stream: a 50 m straight river along −Z at x = 0, 6 m wide, dropping 1 m.</summary>
    public River3D AddRiver(float drop = 1f, float length = 50f)
    {
        var curve = new Curve3D { BakeInterval = 0.25f };
        var start = new Vector3(0, 0, length / 2);
        var end = new Vector3(0, -drop, -length / 2);
        curve.AddPoint(start, @out: (end - start) / 3);
        curve.AddPoint(end, @in: (start - end) / 3);
        for (var i = 0; i < curve.PointCount; i++)
        {
            curve.SetPointWidth(i, 6f);
            curve.SetPointDepth(i, 0.6f);
        }

        var river = new River3D { Name = "River", Curve = curve };
        Scene.AddChild(river);
        return river;
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
