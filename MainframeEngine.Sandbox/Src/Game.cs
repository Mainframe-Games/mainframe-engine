using System.Globalization;
using System.Numerics;
using ImGuiNET;
using MainframeEngine.Gizmos;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using MouseButton = Silk.NET.Input.MouseButton;

namespace MainframeEngine.Sandbox;

/// <summary>
/// The Sandbox: loads <see cref="MainScene"/> into the engine's scene tree (which processes and renders it),
/// adds the debug grid, and draws an ImGui debug overlay. This class only handles window-level input (cursor
/// mode, Escape), the overlay and the <c>--qa-*</c> scripts; the fly camera is a scene node (<see cref="FlyCamera"/>).
/// </summary>
public sealed class Game(in EngineOptions options) : Engine(options)
{
    public const string MainScene = "Content/Scenes/Sandbox.mscene";

    public static EngineOptions DefaultOptions => new()
    {
        GameName = "Mainframe Engine Sandbox",
        RenderingBackend = RenderingBackend.Vulkan,
        WindowSize = new Vector2D<int>(1920, 1080),
        IconPath = "Content/Branding/mg_300_circle.png"
    };

    private static readonly int[] FpsPresets = [0, 30, 60, 120, 144, 240];
    private static readonly string[] FpsLabels = ["Unlimited", "30", "60", "120", "144", "240"];

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

        // The scene (fly camera, sky, SpineBoy, floor, spinning box, five shadow-casting lights) is data:
        // Content/Scenes/Sandbox.mscene, generated from SandboxSceneBuilder. The tree processes and renders it.
        Tree.ChangeSceneToFile(MainScene);

        // Editor-style reference grid: added at runtime (not saved with the scene); draws before the scene's
        // visuals (RenderPriority -100).
        Root.AddChild(new Grid3D { Name = "Grid" });

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

        ImGui.SetNextWindowPos(Vector2.Zero, ImGuiCond.Always, new Vector2(0, 0));
        if (ImGui.Begin("Game Window", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.Value("FrameCount", gameTime.FrameCount);
            ImGui.Value("DeltaTime", gameTime.DeltaTime);
            ImGui.Value("FPS", gameTime.FramesPerSecond);
            ImGui.Value("Ms", gameTime.FramesTimeMs);

            var vsync = Renderer.VSync;
            if (ImGui.Checkbox("VSync", ref vsync))
                Renderer.VSync = vsync;

            var isFullScreen = Window.WindowState is WindowState.Fullscreen;
            if (ImGui.Checkbox("FullScreen", ref isFullScreen))
                Window.WindowState = isFullScreen ? WindowState.Fullscreen : WindowState.Normal;

            var currentFps = MaxFPS;
            var selectedIndex = Array.IndexOf(FpsPresets, currentFps);
            if (selectedIndex < 0) selectedIndex = 0;
            if (ImGui.Combo("Max FPS", ref selectedIndex, FpsLabels, FpsLabels.Length))
                MaxFPS = FpsPresets[selectedIndex];

            ImGui.SeparatorText("Camera");
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

            ImGui.SeparatorText("Scene");
            ImGui.Value("Nodes", Tree.NodeCount);

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
        StopQaWakeTimer();
        base.OnClose(); // frees the scene tree and its GPU objects
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

        if (key == Key.Escape)
            Quit(ExitCode.Ok);
    }

    private void OnMouseDown(IMouse mouse, MouseButton button)
    {
        if (button is MouseButton.Right)
            _mouse.Cursor.CursorMode = CursorMode.Raw;
    }

    private void OnMouseUp(IMouse mouse, MouseButton button)
    {
        if (button is MouseButton.Right)
            _mouse.Cursor.CursorMode = CursorMode.Normal;
    }
}
