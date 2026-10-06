namespace MainframeEngine;

/// <summary>When a <see cref="Timer"/> counts down.</summary>
public enum TimerProcessCallback
{
    /// <summary>Every frame (<see cref="Node.OnProcess"/>).</summary>
    Idle,

    /// <summary>Every fixed physics step.</summary>
    Physics,
}

/// <summary>
/// Counts down <see cref="WaitTime"/> seconds and emits <see cref="Timeout"/> (Godot's <c>Timer</c>). Pauses with
/// the tree according to its <see cref="Node.ProcessMode"/>.
/// </summary>
/// <remarks>
/// Godot's rules (ADR 0135): the time left is a double, it times out once it drops below zero (not at zero), and a
/// repeating timer adds <see cref="WaitTime"/> back, keeping the remainder. So a 1 s timer at 60 FPS fires on frame 60,
/// as in Godot (a float countdown kept 3e-7 s after 60 steps and fired on frame 61).
/// </remarks>
[EditorIcon("clock")]
public class Timer : Node
{
    private TimerProcessCallback _callback;
    private double _timeLeft;
    private bool _running;

    public Timer()
    {
        UpdateProcessing();
    }

    /// <summary>Seconds between start and <see cref="Timeout"/>.</summary>
    [Export(Range = "0.001,4096,0.001")]
    public float WaitTime { get; set; } = 1f;

    /// <summary>Stops after the first timeout instead of restarting.</summary>
    [Export]
    public bool OneShot { get; set; }

    /// <summary>Starts automatically when the node is ready.</summary>
    [Export]
    public bool Autostart { get; set; }

    [Export]
    public TimerProcessCallback ProcessCallback
    {
        get => _callback;
        set
        {
            _callback = value;
            UpdateProcessing();
        }
    }

    /// <summary>Pauses the countdown without stopping the timer.</summary>
    public bool Paused { get; set; }

    /// <summary>Seconds left; 0 when stopped.</summary>
    public float TimeLeft => _running ? (float)Math.Max(_timeLeft, 0) : 0f;

    public bool IsStopped => TimeLeft <= 0;

    [Signal]
    public event Action? Timeout;

    /// <summary>Starts (or restarts) the countdown; a positive <paramref name="time"/> replaces <see cref="WaitTime"/>.</summary>
    public void Start(float time = -1)
    {
        if (time > 0)
            WaitTime = time;
        _timeLeft = WaitTime;
        _running = true;
    }

    public void Stop()
    {
        _timeLeft = -1;
        _running = false;
    }

    protected override void OnReady()
    {
        base.OnReady();
        if (Autostart)
            Start();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_callback == TimerProcessCallback.Idle)
            Advance(gameTime.DeltaTime);
    }

    protected override void OnPhysicsProcess(float delta)
    {
        if (_callback == TimerProcessCallback.Physics)
            Advance(delta);
    }

    private void Advance(double delta)
    {
        if (Paused || !_running)
            return;
        _timeLeft -= delta;
        if (_timeLeft >= 0)
            return;

        if (OneShot)
            Stop();
        else
            _timeLeft += WaitTime;
        Timeout?.Invoke();
    }

    private void UpdateProcessing()
    {
        SetProcess(_callback == TimerProcessCallback.Idle);
        SetPhysicsProcess(_callback == TimerProcessCallback.Physics);
    }
}
