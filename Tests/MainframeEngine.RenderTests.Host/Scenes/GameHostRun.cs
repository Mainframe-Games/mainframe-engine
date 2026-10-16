using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The <c>gamehost</c> run: a real <see cref="GameHost"/> (not an <see cref="Engine"/> subclass that resembles one) set up
/// the way a shipped game runs: settings from a <see cref="ProjectSettings"/>, the default audio device (the null device
/// where there is none, as on CI), real-time deltas under a frame cap, SDL input, the dev overlay registered but hidden,
/// a rotating log file, a game UI layer and a streamed looping sound. Measures the managed bytes the main thread allocates
/// over <c>--alloc warmup:count</c> frames, like <see cref="RenderTestGame"/>, while real SDL events arrive every frame
/// (mouse motion as in mouse look, key presses, window events): they take the whole path, Silk's event pump included.
/// </summary>
public sealed class GameHostRun : GameHost
{
    private readonly HostOptions _host;
    private readonly List<string> _checkFailures = [];
    private long _allocationStart;
    private long? _allocatedBytes;
    private AudioPlayer? _ambience;
    private readonly InputCounter _input = new() { Name = "InputCounter" };

    private GameHostRun(HostOptions host, ProjectSettings settings, GameHostOptions options) : base(settings, options)
    {
        _host = host;
    }

    /// <summary>Set once the host has shut down.</summary>
    public HostResult? Result { get; private set; }

    /// <summary>Runs the game host with a log file in the output folder; returns the result (null if it did not finish).</summary>
    public static (ExitCode Exit, HostResult? Result) Run(HostOptions host)
    {
        var settings = new ProjectSettings { Name = "GameHost allocation gate" };
        settings.Window.Width = host.Width;
        settings.Window.Height = host.Height;
        settings.Window.VSync = false;
        settings.Window.MaxFps = 240; // Silk's frame limiter, as a project with "maxFps" runs
        settings.Localization.DefaultLocale = "en";

        List<string> args = ["--no-vsync"];
        if (host.Hidden)
            args.Add("--hidden");
        if (!host.NoValidation)
            args.Add("--validation");

        // GameHost.Run logs to a rotating file in the user data folder; here it goes to the output folder.
        var fileLog = new FileLogSink(Path.Combine(host.OutputDirectory, "logs"), "gamehost");
        Log.AddSink(fileLog);
        try
        {
            using var game = new GameHostRun(host, settings, GameHostOptions.Parse(args));
            var exit = game.Run();
            return (exit, game.Result);
        }
        finally
        {
            Log.RemoveSink(fileLog);
            fileLog.Dispose();
        }
    }

    protected override void OnLoad()
    {
        base.OnLoad();
        // No main scene (the session starts none); the scene is built in code: a lit mesh, a streamed looping sound on
        // the master bus and the HUD layer, whose bindings are dirtied every frame.
        var scene = new Node3D { Name = "GameHostRun" };
        scene.AddChild(new Camera3D { Name = "Camera", Position = new Vector3(0, 1, 4), Current = true });
        scene.AddChild(new DirectionalLight3D { Name = "Sun", Rotation = Quaternion.CreateFromYawPitchRoll(0.4f, -0.8f, 0f) });
        scene.AddChild(new MeshInstance3D { Name = "Box", Mesh = new BoxMesh() });
        _ambience = new AudioPlayer
        {
            Name = "Ambience",
            Stream = new AudioStream { File = "Content/Audio/ambient_hum.ogg", LoadMode = AudioLoadMode.Stream, Loop = true, LoopEnd = 6f },
            VolumeDb = -30f,
            Autoplay = true,
        };
        scene.AddChild(_ambience);
        scene.AddChild(UiHudScene.CreateHudLayer());
        scene.AddChild(_input);
        Tree.ChangeScene(scene);
    }

    protected override void OnUpdate(in GameTime gameTime)
    {
        base.OnUpdate(gameTime); // starts the session on the first update, then GameSession.Update
        var frame = gameTime.FrameCount;

        // Self-check before the measured window: the sound really plays, so the gate measures live audio.
        if (frame == 60)
        {
            if (Servers.Get<AudioServer>() is not { } audio)
                Fail("No AudioServer registered.");
            else if (_ambience?.Playing != true || audio.Stats.StreamErrors > 0 || audio.Stats.Faulted)
                Fail($"Expected the ambience to play on '{audio.DeviceName}'; {audio.Stats.ActiveVoices} active voices.");
            if (DevOverlay is null)
                Fail("No dev overlay registered.");
        }

        PushInput(frame);

        if (frame == (uint)_host.AllocationWarmupFrames + 1)
        {
            _allocationStart = GC.GetAllocatedBytesForCurrentThread();
            _input.Counting = true;
        }
        else if (frame == (uint)(_host.AllocationWarmupFrames + _host.AllocationMeasuredFrames + 1))
        {
            _allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - _allocationStart;
            _input.Counting = false;
            CheckInputArrived();
            Quit(ExitCode.Ok);
        }
    }

    // From before the measured window to its end, every frame: mouse motion (hovering the HUD, no button), and in turn a
    // press or release of an unbound key and an Exposed window event. They are polled at the next frame's start.
    private void PushInput(uint frame)
    {
        if (frame < (uint)_host.AllocationWarmupFrames - 10)
            return;
        _windowId ??= QaSdl.WindowId(Window);
        var id = _windowId.Value;
        QaSdl.PushMouse(Window, Silk.NET.SDL.EventType.Mousemotion, 100 + (int)(frame % 200), 120, 1, 0, buttons: 0);
        switch (frame % 3)
        {
            case 0:
                QaSdl.PushKey(id, Silk.NET.SDL.KeyCode.KL, Silk.NET.SDL.Scancode.ScancodeL, pressed: frame % 2 == 0);
                break;
            case 1:
                QaSdl.PushWindowEvent(id, Silk.NET.SDL.WindowEventID.Exposed);
                break;
        }
    }

    private uint? _windowId;

    // Every pushed event reached the tree (a frame's events are polled at the start of the next one): the gate measured
    // the input path, not a pump that dropped events.
    private void CheckInputArrived()
    {
        var frames = _host.AllocationMeasuredFrames;
        if (Math.Abs(_input.Motions - frames) > 1)
            Fail($"{_input.Motions} mouse motions reached the tree over {frames} frames; expected one per frame.");
        if (Math.Abs(_input.KeyEvents - frames / 3) > 1)
            Fail($"{_input.KeyEvents} L key events reached the tree over {frames} frames; expected one every 3 frames.");
    }

    // Sees every event before the UI (InputBeforeUi) and counts the pushed ones while the gate measures.
    private sealed class InputCounter : Node
    {
        public InputCounter() => InputBeforeUi = true;

        public bool Counting { get; set; }
        public int Motions { get; private set; }
        public int KeyEvents { get; private set; }

        protected override void OnInput(InputEvent inputEvent)
        {
            if (!Counting)
                return;
            if (inputEvent is InputEventMouseMotion)
                Motions++;
            else if (inputEvent is InputEventKey { Key: Silk.NET.Input.Key.L })
                KeyEvents++;
        }
    }

    private void Fail(string message) => _checkFailures.Add(message);

    protected override void OnClose()
    {
        var vulkan = (IVulkanContext)Renderer;
        var (deviceName, driver, tag, deviceType) = RenderTestGame.DescribeDevice(vulkan);
        var validation = vulkan.Validation;
        var audioDevice = Servers.Get<AudioServer>() is { } audio ? audio.DeviceName : "none";
        base.OnClose();

        Log.Info($"[GameHostRun] Audio device: {audioDevice}");
        Result = new HostResult
        {
            Scene = _host.Scene,
            DeviceName = deviceName,
            DeviceType = deviceType,
            Driver = driver,
            PlatformTag = tag,
            ValidationEnabled = validation.IsEnabled,
            ValidationWarnings = validation.WarningCount,
            ValidationErrors = validation.ErrorCount,
            ValidationMessages = validation.Messages,
            RenderedFrames = RenderedFrameCount,
            AllocatedBytes = _allocatedBytes,
            MeasuredFrames = _allocatedBytes is null ? 0 : _host.AllocationMeasuredFrames,
            Captures = [],
            SceneCheckFailures = _checkFailures,
        };
    }
}
