using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MainframeEngine.Gizmos;
using MainframeEngine.Localization;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using MouseButton = Silk.NET.Input.MouseButton;

namespace MainframeEngine.Sandbox;

/// <summary>
/// The Sandbox: loads <see cref="MainScene"/> into the engine's scene tree (which processes and renders it),
/// adds the debug grid and the RmlUi HUD (<see cref="SandboxHud"/>: stats and live scene settings; a widget-library
/// demo window), and keeps the ImGui windows as the developer overlay (F12). This class only handles window-level
/// input (cursor mode, Escape), the overlay and the <c>--qa-*</c> scripts; the fly camera is a scene node
/// (<see cref="FlyCamera"/>).
/// </summary>
public sealed class Game(in EngineOptions options) : Engine(options)
{
    public const string MainScene = "Content/Scenes/Sandbox.mscene";

    public static EngineOptions DefaultOptions => new()
    {
        GameName = "Mainframe Engine Sandbox",
        RenderingBackend = RenderingBackend.Vulkan,
        WindowSize = new Vector2D<int>(1920, 1080),
        IconPath = "Content/Branding/mg_300_circle.png",
        DevOverlayVisible = false, // the RmlUi HUD is the game UI; F12 shows the ImGui developer windows
        // Debug builds load UI documents straight from the project's Content/ folder, so hot reload needs no rebuild.
        Ui = new UiServerOptions { SourceContentDirectories = UiServerOptions.SourceDirectoriesOf(typeof(Game).Assembly) },
    };

    // M9: overlay labels in the current language, rebuilt only when it changes (zero allocations per frame).
    private readonly OverlayText _text = new();
    private string[] _locales = [];
    private string[] _localeNames = [];
    private WelcomeBanner? _banner;

    /// <summary>Set by <c>--qa-capture</c>: frames to screenshot before the engine exits.</summary>
    public QaCapture? QaCapture { get; init; }

    /// <summary>Set by <c>--server</c> / <c>--client host</c>: the multiplayer demo (M5).</summary>
    public NetworkDemo? Network { get; init; }

    /// <summary>Set by <c>--qa-audio</c>: plays a test melody through the device, checks the audio server, exits.</summary>
    public QaAudio? QaAudio { get; init; }

    private IMouse _mouse = null!;

    /// <summary>The scene's camera (null until the scene is loaded).</summary>
    private Camera3D? Camera => Root.ActiveCamera3D;

    protected override void OnLoad()
    {
        base.OnLoad();

        Renderer.SetClearColor(0.18f, 0.31f, 0.31f); // DarkSlateGray

        var keyboard = InputContext.Keyboards[0];
        keyboard.KeyDown += OnKeyDown;

        _mouse = InputContext.Mice[0];
        _mouse.MouseDown += OnMouseDown;
        _mouse.MouseUp += OnMouseUp;

        // M9: the language menu lists every compiled catalog (Content/locale/<locale>/LC_MESSAGES/messages.mo).
        _locales = [.. Tr.GetAvailableLocales()];
        _localeNames = [.. _locales.Select(LocaleId.DisplayName)];
        _text.Refresh();
        Tr.LocaleChanged += OnLocaleChanged;

        // The scene (fly camera, sky, SpineBoy, floor, spinning box, five shadow-casting lights) is data:
        // Content/Scenes/Sandbox.mscene, generated from SandboxSceneBuilder. The tree processes and renders it.
        Tree.ChangeSceneToFile(MainScene);

        // Editor-style reference grid: added at runtime (not saved with the scene); draws before the scene's
        // visuals (RenderPriority -100).
        Root.AddChild(new Grid3D { Name = "Grid" });

        // M8 game UI: the HUD layer, and a menu layer above it with the widget-library demo (hidden until asked for).
        var widgetDemo = new UiDocument { Name = "WidgetDemo", Source = "Content/UI/widgets/demo.rml", Visible = false };
        var credits = new UiDocument { Name = "Credits", Source = "Content/UI/credits.rml", Visible = false };
        var menus = new UiLayer { Name = "Menus", Layer = 10 };
        menus.AddChild(widgetDemo);
        menus.AddChild(credits);
        var hud = new UiLayer { Name = "Hud", Layer = 0 };
        hud.AddChild(new SandboxHud { Name = "SandboxHud", Game = this, WidgetDemo = widgetDemo, Credits = credits });
        Root.AddChild(hud);
        Root.AddChild(menus);

        // M5: host or join (both ends have loaded the same level, so spawned boxes go to the same parent path).
        Network?.Start(this);
    }

    protected override void OnUpdate(in GameTime gameTime)
    {
        if (QaCapture?.ShouldCapture(gameTime.FrameCount) == true)
            CaptureFrame();
        RunQaScript(gameTime.FrameCount);
        Network?.Update(gameTime);
        if (QaAudio?.Update(Servers.Get<AudioServer>()) is { } audioResult)
            Quit(audioResult);
    }

    protected override void OnImGui(in GameTime gameTime)
    {
        var camera = Camera;
        if (camera is not null)
            Root.World3D.Lights.DrawLightGizmos(camera.RenderCamera);

        // Developer overlay (F12). Frame stats, VSync and Max FPS moved to the RmlUi HUD.
        ImGui.SetNextWindowPos(new Vector2(0, 120), ImGuiCond.FirstUseEver, new Vector2(0, 0));
        if (ImGui.Begin(_text.Window, ImGuiWindowFlags.AlwaysAutoResize))
        {
            if (!Node.IsInstanceValid(_banner))
                _banner = Tree.CurrentScene?.GetNodeOrNull<WelcomeBanner>("Welcome");
            if (_banner is { } banner)
            {
                ImGui.TextUnformatted(banner.DisplayTitle);
                ImGui.TextUnformatted(banner.DisplayHint);
                ImGui.Separator();
            }

            ImGui.Value("DeltaTime", gameTime.DeltaTime);

            var isFullScreen = Window.WindowState is WindowState.Fullscreen;
            if (ImGui.Checkbox(_text.FullScreen, ref isFullScreen))
                Window.WindowState = isFullScreen ? WindowState.Fullscreen : WindowState.Normal;

            if (Ui is { } ui)
            {
                var debugger = ui.DebuggerVisible;
                if (ImGui.Checkbox(_text.UiDebugger, ref debugger))
                    ui.DebuggerVisible = debugger;
            }

            ImGui.SeparatorText(_text.Language);
            var localeIndex = Array.IndexOf(_locales, Tr.CurrentLocale);
            if (ImGui.Combo(_text.LanguageCombo, ref localeIndex, _localeNames, _localeNames.Length) && localeIndex >= 0)
                Tr.SetLocale(_locales[localeIndex]); // nodes re-translate (WelcomeBanner), the overlay refreshes

            ImGui.SeparatorText(_text.Camera);
            if (camera is not null)
            {
                // Formatted into stack buffers: interpolated strings would allocate every frame.
                Span<char> text = stackalloc char[64];
                var p = camera.GlobalPosition;
                if (text.TryWrite(CultureInfo.InvariantCulture, $"Position: <{p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}>", out var written))
                    ImGui.TextUnformatted(text[..written]);
                var f = camera.GlobalForward;
                if (text.TryWrite(CultureInfo.InvariantCulture, $"Forward: <{f.X:0.00}, {f.Y:0.00}, {f.Z:0.00}>", out written))
                    ImGui.TextUnformatted(text[..written]);
            }

            ImGui.SeparatorText(_text.Scene);
            ImGui.TextUnformatted(_text.NodeCount(Tree.NodeCount));

            ImGui.SeparatorText("Physics");
            if (Servers.Get<PhysicsServer3D>() is { } physics)
            {
                var drawShapes = physics.DebugDrawEnabled;
                if (ImGui.Checkbox("Collision shapes", ref drawShapes))
                    physics.DebugDrawEnabled = drawShapes;
                if (physics.FindSpace(Root.World3D) is { } space)
                {
                    ImGui.Value("Bodies", space.ObjectCount);
                    ImGui.Value("Awake", space.ActiveBodyCount);
                }
            }

            Network?.DrawImGui();
            if (Servers.Get<AudioServer>() is { } audio)
                AudioImGui.DrawMixer(audio);
        }
        ImGui.End();

        if (camera is not null)
            ImGuiCoordGizmo.DrawCoordinateGizmo(camera.RenderCamera);
        RendererDebugWindow.Draw(Renderer, Servers.Render); // exposure, mesh draw stats, GPU memory, uploads, pipeline cache
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        QaCapture?.Save(capture);
    }

    protected override void OnClose()
    {
        Tr.LocaleChanged -= OnLocaleChanged;
        StopQaWakeTimer();
        base.OnClose(); // frees the scene tree and its GPU objects
    }

    private void OnLocaleChanged(object? sender, LocaleChangedEventArgs e) => _text.Refresh();

    /// <summary>
    /// The overlay's translated labels. ImGui identifies widgets by label, so each label keeps a fixed id after "###"
    /// and a language switch does not reset widget state. Strings are rebuilt on locale change, never per frame.
    /// </summary>
    private sealed class OverlayText
    {
        private int _nodeCount = -1;
        private string _nodeCountText = string.Empty;

        public string Window { get; private set; } = string.Empty;
        public string FullScreen { get; private set; } = string.Empty;
        public string UiDebugger { get; private set; } = string.Empty;
        public string Language { get; private set; } = string.Empty;
        public string LanguageCombo { get; private set; } = string.Empty;
        public string Camera { get; private set; } = string.Empty;
        public string Scene { get; private set; } = string.Empty;

        public void Refresh()
        {
            Window = Tr._("Developer") + "###Developer";
            FullScreen = Tr._("Full screen") + "###FullScreen";
            UiDebugger = Tr._("UI debugger (F8)") + "###UiDebugger";
            Language = Tr._("Language");
            LanguageCombo = "###Language";
            Camera = Tr.P("overlay section", "Camera");
            Scene = Tr.P("overlay section", "Scene");
            _nodeCount = -1;
        }

        /// <summary>"N nodes in the tree" in the current language; re-formatted only when the count changes.</summary>
        public string NodeCount(int count)
        {
            if (count != _nodeCount)
            {
                _nodeCount = count;
                _nodeCountText = Tr.N("{0} node in the scene tree", "{0} nodes in the scene tree", count);
            }

            return _nodeCountText;
        }
    }

    // --qa-resize / --qa-minimize: scripted window changes for `just qa`.
    private long _qaMinimizedAt;
    private int _qaRenderedAtMinimize;
    private int _qaUpdatesWhileMinimized;
    private System.Threading.Timer? _qaWakeTimer;

    private void RunQaScript(uint frame)
    {
        if (QaCapture is not { } qa)
            return;

        if (qa.InputFrame > 0 && frame >= qa.InputFrame && frame < qa.InputFrame + QaCapture.InputFrames)
        {
            var step = (int)(frame - qa.InputFrame);
            var x = Window.Size.X / 2 + step * 8;
            var y = Window.Size.Y / 2;
            if (step == 0)
            {
                Log.Info($"[QA] Input: right-drag starts, camera forward {Camera?.GlobalForward}");
                QaCapture.PushMouse(Window, Silk.NET.SDL.EventType.Mousebuttondown, x, y, 0, 0);
            }
            else if (step == (int)QaCapture.InputFrames - 1)
            {
                QaCapture.PushMouse(Window, Silk.NET.SDL.EventType.Mousebuttonup, x, y, 0, 0);
            }
            else if (step == (int)QaCapture.InputFrames - 2)
            {
                Log.Info($"[QA] Input: right-drag ends, camera forward {Camera?.GlobalForward}, cursor {_mouse.Cursor.CursorMode}");
            }
            else
            {
                QaCapture.PushMouse(Window, Silk.NET.SDL.EventType.Mousemotion, x, y, 8, 0);
            }
        }

        if (frame == qa.ResizeFrame)
        {
            Window.Size = qa.ResizeTo;
            Log.Info($"[QA] Resized to {qa.ResizeTo.X}x{qa.ResizeTo.Y} pt");
        }

        if (frame == qa.MinimizeFrame && _qaMinimizedAt == 0)
        {
            Window.WindowState = WindowState.Minimized;
            _qaMinimizedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            _qaRenderedAtMinimize = RenderedFrameCount;
            _qaUpdatesWhileMinimized = 0;
            QaCapture.WakeEnabled = true;
            _qaWakeTimer = new System.Threading.Timer(static _ => QaCapture.WakeEventLoop(), null, 100, 100);
            Log.Info("[QA] Minimised");
            return;
        }

        if (_qaMinimizedAt != 0)
        {
            _qaUpdatesWhileMinimized++;
            if (System.Diagnostics.Stopwatch.GetElapsedTime(_qaMinimizedAt).TotalSeconds >= 1.5)
            {
                Log.Info($"[QA] Restoring after 1.5 s minimised: {_qaUpdatesWhileMinimized} updates, " +
                         $"{RenderedFrameCount - _qaRenderedAtMinimize} frames rendered while minimised");
                StopQaWakeTimer();
                _qaMinimizedAt = 0;
                Window.WindowState = WindowState.Normal;
            }
        }
    }

    // Stops the wake timer and waits for an in-flight callback, so no SDL call races SDL shutdown.
    private void StopQaWakeTimer()
    {
        QaCapture.WakeEnabled = false;
        if (_qaWakeTimer is null)
            return;
        using (var done = new ManualResetEvent(false))
        {
            if (_qaWakeTimer.Dispose(done))
                done.WaitOne();
        }
        _qaWakeTimer = null;
    }

    public void OnKeyDown(IKeyboard keyboard, Key key, int arg3)
    {
        if (key is Key.AltLeft)
        {
            _mouse.Cursor.CursorMode = _mouse.Cursor.CursorMode is CursorMode.Raw
                ? CursorMode.Normal
                : CursorMode.Raw;
        }

        // Escape quits, unless it is closing something in the UI (a text field has focus).
        if (key == Key.Escape && Ui is not { TextInputActive: true })
            Quit(ExitCode.Ok);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        // Right-drag looks around, unless the press landed on the HUD.
        if (button is MouseButton.Right && Ui is not { IsPointerOverUi: true })
            _mouse.Cursor.CursorMode = CursorMode.Raw;
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button is MouseButton.Right)
            _mouse.Cursor.CursorMode = CursorMode.Normal;
    }
}
