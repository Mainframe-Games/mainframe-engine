using MainframeEngine;
using Silk.NET.Input;

namespace Forest;

/// <summary>
/// The frame-rate readout in the top-right corner: frames per second, the average frame time and the slowest frame of
/// the last half second. F3 toggles it. <see cref="ForestDev"/> adds it at run time unless the run is a screenshot,
/// benchmark or autowalk (clean captures) or <c>++ --no-fps</c> was given. The values are published twice a second as
/// numbers (formatted by RmlUi), so it allocates nothing per frame. It hides itself while the game is being recorded
/// (<see cref="SceneTree.Recorder"/>) and comes back, as the player had it, when the recording stops.
/// </summary>
public sealed class FpsHud : UiDocument
{
    /// <summary>How often the readout updates.</summary>
    public const float RefreshSeconds = 0.5f;

    private float _elapsed;
    private float _maxFrame;
    private int _frames;
    private GameRecorder? _recorder;
    private bool _visibleBeforeRecording = true;

    public FpsHud()
    {
        Source = "Assets/UI/fps.rml";
        AutoFocus = false;
    }

    /// <summary>Frames per second over the last refresh window.</summary>
    public float Fps { get; private set; }

    /// <summary>The average frame time in milliseconds over the last refresh window.</summary>
    public float FrameMs { get; private set; }

    /// <summary>The slowest frame in milliseconds over the last refresh window.</summary>
    public float MaxFrameMs { get; private set; }

    /// <summary>Adds the readout to <paramref name="parent"/> on its own UI layer above the game's.</summary>
    public static FpsHud Attach(Node parent)
    {
        var layer = new UiLayer { Name = "FpsLayer", Layer = 100 };
        var hud = new FpsHud { Name = "Fps" };
        layer.AddChild(hud);
        parent.AddChild(layer);
        return hud;
    }

    protected override void OnReady()
    {
        CreateDataModel("fps")
            .Bind("fps", this, static h => h.Fps)
            .Bind("ms", this, static h => h.FrameMs)
            .Bind("max", this, static h => h.MaxFrameMs);

        // The recorder exists from the end of GameHost.OnLoad, before any scene, and a --record run has already started
        // by now (the first update starts it, then the session): so subscribe for later recordings and also catch up.
        if (Tree?.Recorder is { } recorder)
        {
            _recorder = recorder;
            recorder.Started += OnRecordingStarted;
            recorder.Stopped += OnRecordingStopped;
            if (recorder.IsRecording)
                OnRecordingStarted();
        }
    }

    protected override void OnExitTree()
    {
        if (_recorder is { } recorder)
        {
            recorder.Started -= OnRecordingStarted;
            recorder.Stopped -= OnRecordingStopped;
            _recorder = null;
        }

        base.OnExitTree();
    }

    private void OnRecordingStarted()
    {
        _visibleBeforeRecording = Visible;
        Visible = false;
    }

    private void OnRecordingStopped(RecordingResult result) => Visible = _visibleBeforeRecording;

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Sample(gameTime.DeltaTime) && DataModels.Count > 0)
            DataModels[0].DirtyAll();
    }

    /// <summary>Adds one frame's time; true when a refresh window closed and the values changed.</summary>
    public bool Sample(float deltaSeconds)
    {
        if (deltaSeconds <= 0f)
            return false;
        _elapsed += deltaSeconds;
        _frames++;
        _maxFrame = MathF.Max(_maxFrame, deltaSeconds);
        if (_elapsed < RefreshSeconds)
            return false;

        Fps = _frames / _elapsed;
        FrameMs = 1000f * _elapsed / _frames;
        MaxFrameMs = 1000f * _maxFrame;
        _elapsed = 0f;
        _frames = 0;
        _maxFrame = 0f;
        return true;
    }

    protected override void OnUnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventKey { Pressed: true, Key: Key.F3 })
            Visible = !Visible;
    }
}
