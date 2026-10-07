using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace MainframeEngine.Editor;

/// <summary>Command-line options of the editor executable.</summary>
public sealed record EditorAppOptions
{
    public EditorWorkspaceOptions Workspace { get; init; } = new();

    /// <summary>Window size in points; null uses the persisted size (or 1600×960).</summary>
    public Vector2D<int>? WindowSize { get; init; }

    /// <summary>
    /// Fixed pixels per point (<see cref="EngineOptions.ContentScale"/>, <c>--scale</c>): the framebuffer is exactly
    /// <see cref="WindowSize"/> × this on any display (render tests, QA captures). 0 follows the display.
    /// </summary>
    public float ContentScale { get; init; }

    public bool Hidden { get; init; }
    public bool VSync { get; init; } = true;
    public bool EnableValidation { get; init; } = EngineOptions.DefaultEnableValidation;
    public float FixedDeltaTime { get; init; }
    public int MaxFrames { get; init; }
    public bool EnableFrameCapture { get; init; }

    /// <summary>Scripted runs (QA scripts, smoke test) driven frame by frame.</summary>
    public IEditorAutomation? Automation { get; init; }
}

/// <summary>Drives the editor from code: called every frame and with captured frames (QA scripts, smoke tests).</summary>
public interface IEditorAutomation
{
    void OnFrame(EditorApp app, uint frame);

    void OnFrameCaptured(EditorApp app, FrameCapture capture);

    /// <summary>Called before the engine shuts down.</summary>
    void OnClosing(EditorApp app);

    /// <summary>Called after <see cref="Engine.Run"/> returned (the engine is torn down): write results.</summary>
    void OnExited(int exitCode)
    {
    }
}

/// <summary>
/// The editor executable's engine host (<c>EditorApp : Engine</c>, code-driven per ADR 0080): runs the engine's scene
/// tree in <see cref="SceneTree.EditMode"/> with an <see cref="EditorWorkspace"/> under the root, implements
/// <see cref="IEditorHost"/> over the SDL window, and intercepts window-close requests (close button, Cmd+Q) so unsaved
/// scenes are asked about first. F12 toggles the engine's developer overlay.
/// </summary>
public sealed class EditorApp : Engine, IEditorHost
{
    private readonly EditorAppOptions _options;
    private IKeyboard? _keyboard;
    private CloseRequestFilter? _closeFilter;

    public EditorApp(EditorAppOptions options)
        : base(CreateEngineOptions(options))
    {
        _options = options;
    }

    private static EngineOptions CreateEngineOptions(EditorAppOptions options)
    {
        var layout = options.Workspace.LayoutPath is { } path ? EditorLayout.Load(path) : new EditorLayoutSettings();
        var size = options.WindowSize
                   ?? (layout.WindowWidth >= 800 && layout.WindowHeight >= 500
                       ? new Vector2D<int>(layout.WindowWidth, layout.WindowHeight)
                       : new Vector2D<int>(1600, 960));
        return new EngineOptions
        {
            GameName = "Mainframe Editor",
            WindowSize = size,
            ContentScale = options.ContentScale,
            WindowVisible = !options.Hidden,
            VSync = options.VSync,
            EnableValidation = options.EnableValidation,
            FixedDeltaTime = options.FixedDeltaTime,
            MaxFrames = options.MaxFrames,
            EnableFrameCapture = options.EnableFrameCapture,
            IconPath = EditorBrand.WindowIconPath,
            DevOverlayVisible = false,
            // Audio previews (AudioPreview) on the engine's default bus layout: the project's layout is not applied, so a
            // project that mutes or ducks a bus cannot silence previews. Scenes stay silent (edit mode). No device: the
            // null device, as for games.
            Audio = new AudioOptions { Enabled = true, BusLayoutPath = null },
            // Collision shapes are part of what the editor shows.
            DebugCollisionShapes = true,
            Ui = new UiServerOptions
            {
                // The accent overlay (Editor Settings) is checked before the editor's own style sheets.
                SourceContentDirectories = options.Workspace.ThemeOverlayDirectory is { } overlay
                    ? [overlay, .. UiServerOptions.SourceDirectoriesOf(typeof(EditorApp).Assembly)]
                    : UiServerOptions.SourceDirectoriesOf(typeof(EditorApp).Assembly),
                // F8 is Stop (Godot's play keys F5–F8): the RmlUi debugger moves to F9.
                DebuggerKey = Key.F9,
            },
        };
    }

    /// <summary>The workspace (null before load).</summary>
    public EditorWorkspace? Workspace { get; private set; }

    protected override void OnLoad()
    {
        base.OnLoad();
        if (!EditorBrand.VersionsMatch)
            Log.Error($"[Editor] Editor {EditorBrand.AssemblyVersion} is running engine core {EngineInfo.Version}; they ship in lock step. Reinstall the editor.");
        Tree.EditMode = true;
        // Godot's editor rule (ADR 0127): the game's code (loaded collectible by ProjectService) runs only in [Tool] types.
        Tree.EditModeScripts = static type => System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(type.Assembly) is { IsCollectible: true };
        Renderer.SetClearColor(0.082f, 0.09f, 0.11f);
        _keyboard = InputContext.Keyboards.Count > 0 ? InputContext.Keyboards[0] : null;
        _closeFilter = CloseRequestFilter.TryInstall();
        Workspace = new EditorWorkspace(this, _options.Workspace);
        Root.AddChild(Workspace);
        Log.Info($"[Editor] Ready ({LayoutSize.X}x{LayoutSize.Y} pt, {PixelScale:0.#}x).");
    }

    protected override void OnUpdate(in GameTime gameTime)
    {
        // Wall-clock frame time, smoothed (GameTime may carry a fixed delta in scripted runs).
        var now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_lastTimestamp != 0)
        {
            var ms = (float)System.Diagnostics.Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds;
            _lastMs = _lastMs <= 0 ? ms : _lastMs * 0.9f + ms * 0.1f;
            _lastFps = _lastMs > 0 ? 1000f / _lastMs : 0;
        }

        _lastTimestamp = now;
        if (_closeFilter is not null && CloseRequestFilter.TakeRequest())
            Workspace?.OnCloseRequested();

        _options.Automation?.OnFrame(this, gameTime.FrameCount);
    }

    protected override void OnFrameCaptured(FrameCapture capture) => _options.Automation?.OnFrameCaptured(this, capture);

    protected override void OnClose()
    {
        _options.Automation?.OnClosing(this);
        _closeFilter?.Dispose();
        _closeFilter = null;
        base.OnClose();
    }

    // ── IEditorHost ──────────────────────────────────────────────────────────────────────────────────────────────

    Vector2 IEditorHost.WindowSize => LayoutSize;

    /// <summary>The window in layout points (the editor UI's dp): the framebuffer divided by <see cref="PixelScale"/>.</summary>
    private Vector2 LayoutSize
    {
        get
        {
            var framebuffer = FramebufferSize;
            var scale = PixelScale;
            return framebuffer.X > 0 && framebuffer.Y > 0
                ? new Vector2(MathF.Round(framebuffer.X / scale), MathF.Round(framebuffer.Y / scale))
                : new Vector2(Window.Size.X, Window.Size.Y);
        }
    }

    /// <summary>Pixels per layout point: the fixed <see cref="EngineOptions.ContentScale"/>, else the display's (2 on Retina).</summary>
    public float PixelScale => MathF.Max(0.01f, ContentScale);

    /// <summary>Layout points per mouse point (OS window point): 1 unless a fixed content scale differs from the display's.</summary>
    public float PointerScale
    {
        get
        {
            var framebuffer = FramebufferSize;
            var display = Window.Size.X > 0 && framebuffer.X > 0 ? (float)framebuffer.X / Window.Size.X : 1f;
            return display / PixelScale;
        }
    }

    public float FramesPerSecond => _lastFps;

    public float FrameMilliseconds => _lastMs;

    private float _lastFps;
    private float _lastMs;
    private long _lastTimestamp;

    public EditorModifiers Modifiers
    {
        get
        {
            if (_keyboard is not { } k)
                return EditorModifiers.None;
            var modifiers = EditorModifiers.None;
            if (k.IsKeyPressed(Key.ControlLeft) || k.IsKeyPressed(Key.ControlRight) || k.IsKeyPressed(Key.SuperLeft) || k.IsKeyPressed(Key.SuperRight))
                modifiers |= EditorModifiers.Command;
            if (k.IsKeyPressed(Key.ShiftLeft) || k.IsKeyPressed(Key.ShiftRight))
                modifiers |= EditorModifiers.Shift;
            if (k.IsKeyPressed(Key.AltLeft) || k.IsKeyPressed(Key.AltRight))
                modifiers |= EditorModifiers.Alt;
            return modifiers;
        }
    }

    public bool ReportsModifiers => _keyboard is not null;

    public bool PrimaryMouseDown => InputContext.Mice.Count > 0 && InputContext.Mice[0].IsButtonPressed(MouseButton.Left);

    public void SetTitle(string title) => Window.Title = title;

    void IEditorHost.Quit() => Quit(ExitCode.Ok);
}

/// <summary>
/// Intercepts SDL's quit and window-close events (Silk.NET would close the window at once) with an SDL event filter, so
/// the editor can ask about unsaved changes first; <see cref="TakeRequest"/> reports a pending request. Quitting through
/// <see cref="Engine.Quit"/> closes the window directly and is not affected.
/// </summary>
internal sealed unsafe class CloseRequestFilter : IDisposable
{
    private static int _requested;
    private readonly Sdl _sdl;
    private bool _disposed;

    private CloseRequestFilter(Sdl sdl)
    {
        _sdl = sdl;
        _sdl.SetEventFilter(new PfnEventFilter(&Filter), null);
    }

    public static CloseRequestFilter? TryInstall()
    {
        try
        {
            return new CloseRequestFilter(SdlProvider.SDL.Value);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            Log.Warning($"[Editor] Window close requests cannot be intercepted ({e.Message}); unsaved changes are not asked about.");
            return null;
        }
    }

    /// <summary>True once per close request.</summary>
    public static bool TakeRequest() => Interlocked.Exchange(ref _requested, 0) != 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int Filter(void* user, Event* e)
    {
        if (e is null)
            return 1;
        var type = (EventType)e->Type;
        if (type == EventType.Quit || (type == EventType.Windowevent && (WindowEventID)e->Window.Event == WindowEventID.Close))
        {
            Interlocked.Exchange(ref _requested, 1);
            return 0; // dropped: the workspace decides
        }

        return 1;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _sdl.SetEventFilter(default, null);
    }
}
