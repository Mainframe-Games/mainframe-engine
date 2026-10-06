using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MainframeEngine;

/// <summary>
/// <see cref="GameHost"/>'s <c>--headless</c> run (Godot's <c>--headless</c>): the project's scene tree with no window,
/// renderer, canvas, UI or audio device, for dedicated servers. Registers networking (<see cref="Networking.MultiplayerApi"/>),
/// the physics servers and an audio server on the null device (buses and players still work, nothing is heard), runs
/// <see cref="GameSession"/> (input map, autoloads, the main scene) and ticks the tree at the project's
/// <c>maxFps</c>, else the physics rate. Canvas items still run their draws (Godot calls <c>_draw</c> headless too);
/// nothing renders them. Ctrl+C or SIGTERM is a close request (the game may save), then a quit.
/// </summary>
public sealed class HeadlessHost : IDisposable
{
    private readonly EngineOptions _options;
    private readonly FPSCounter _fps = new();
    private PosixSignalRegistration? _sigterm;
    private volatile bool _closeSignalled;
    private bool _quit;
    private ExitCode _exitCode = ExitCode.Ok;
    private bool _disposed;

    public HeadlessHost(ProjectSettings settings, GameHostOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        options ??= new GameHostOptions();
        _options = options.Apply(settings.ToEngineOptions());
        Localization.Tr.Configure(_options.Localization ?? new Localization.LocalizationOptions(), _options.Locale);
        Tree = new SceneTree { PhysicsTicksPerSecond = _options.PhysicsTicksPerSecond };
        Input.Current = Tree.Input;
        Tree.QuitRequested += code => Quit((ExitCode)code);
        GameHost.Project = settings;

        // Disposed in reverse order after the tree is freed, like Engine.OnLoad's servers.
        Networking.MultiplayerApi.Attach(Tree);
        if (_options.Audio.Enabled)
        {
            var audio = _options.Audio;
            audio.Device = AudioDeviceMode.Null;
            try
            {
                Tree.Servers.Register(AudioServer.Create(audio, Tree));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                Log.Error($"[Audio] Audio is unavailable: {e}");
            }
        }

        Tree.Servers.Register(new PhysicsServer3D(_options.Physics3D));
        Tree.Servers.Register(new PhysicsServer2D(_options.Physics2D));

        Session = new GameSession(Tree, settings, options);
        Session.QuitRequested += Quit;
        FrameRate = settings.Window.MaxFps > 0 ? settings.Window.MaxFps : _options.PhysicsTicksPerSecond;
    }

    /// <summary>The tree the game runs in.</summary>
    public SceneTree Tree { get; }

    /// <summary>The project session (autoloads, scene, editor link).</summary>
    public GameSession Session { get; }

    /// <summary>Updates per second the loop is held to.</summary>
    public int FrameRate { get; }

    /// <summary>Updates run so far.</summary>
    public uint Frames { get; private set; }

    /// <summary>Starts the session and runs until <see cref="Quit"/> (or <c>--max-frames</c>); returns the exit code.</summary>
    public ExitCode Run()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Log.Info($"[GameHost] Headless: no window, renderer or audio device; {FrameRate} updates/s.");
        Console.CancelKeyPress += OnCancelKeyPress;
        if (!OperatingSystem.IsWindows())
            _sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSigterm);
        try
        {
            if (!Session.Start())
                return ExitCode.Error;
            var period = Stopwatch.Frequency / (double)Math.Max(1, FrameRate);
            var last = Stopwatch.GetTimestamp();
            var next = last + (long)period;
            while (!_quit)
            {
                if (_closeSignalled)
                {
                    _closeSignalled = false;
                    Tree.NotifyCloseRequested();
                    Quit(ExitCode.Ok);
                }

                var now = Stopwatch.GetTimestamp();
                Step((float)((now - last) / (double)Stopwatch.Frequency));
                last = now;
                if (_options.MaxFrames > 0 && Frames >= _options.MaxFrames)
                    break;
                Wait(ref next, period);
            }

            return _exitCode;
        }
        finally
        {
            Console.CancelKeyPress -= OnCancelKeyPress;
            _sigterm?.Dispose();
            _sigterm = null;
        }
    }

    /// <summary>One update of <paramref name="delta"/> seconds (the fixed delta with <c>--fixed-fps</c>): the session, the tree, the draws.</summary>
    public void Step(float delta)
    {
        _fps.Update();
        var time = new GameTime
        {
            DeltaTime = _options.FixedDeltaTime > 0f ? _options.FixedDeltaTime : delta,
            FrameCount = _fps.TotalFrameCount,
            FramesPerSecond = _fps.Fps,
            FramesTimeMs = _fps.Ms,
        };
        Session.Update(time);
        Tree.Tick(time);
        Tree.FlushCanvasRedraws();
        Frames++;
    }

    /// <summary>Stops the loop after the current update with <paramref name="exitCode"/>.</summary>
    public void Quit(ExitCode exitCode)
    {
        _exitCode = exitCode;
        _quit = true;
    }

    private static void Wait(ref long next, double period)
    {
        var now = Stopwatch.GetTimestamp();
        if (now >= next)
        {
            next = now + (long)period; // behind (a long update): do not try to catch up
            return;
        }

        var ms = (next - now) * 1000.0 / Stopwatch.Frequency;
        if (ms >= 1.0)
            Thread.Sleep(TimeSpan.FromMilliseconds(ms));
        next += (long)period;
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true; // a second Ctrl+C while closing still lands here: the loop ends after its update
        _closeSignalled = true;
    }

    private void OnSigterm(PosixSignalContext context)
    {
        context.Cancel = true;
        _closeSignalled = true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Session.Shutdown(_exitCode);
        Tree.Shutdown();
        Tree.Servers.Dispose();
        ResourceLoader.ClearCache();
        if (ReferenceEquals(Input.Current, Tree.Input))
            Input.Current = null;
        if (ReferenceEquals(GameHost.Project, Session.Settings))
            GameHost.Project = null;
    }
}
