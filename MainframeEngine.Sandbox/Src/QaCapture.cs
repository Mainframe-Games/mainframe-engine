using System.Globalization;
using Silk.NET.Maths;
using Silk.NET.SDL;

namespace MainframeEngine.Sandbox;

/// <summary>
/// <c>--qa-capture &lt;dir&gt; [--qa-frames 30,90,180] [--qa-resize WxH@frame] [--qa-minimize frame] [--qa-input frame]</c>:
/// runs the Sandbox deterministically (fixed 60 Hz timestep), saves the listed frames as PNGs into the
/// directory, then exits. The optional steps resize the window (swapchain recreation) and minimise it
/// for ~1.5 s then restore it (the engine must block on events, not spin). Used by <c>just qa</c>.
/// </summary>
public sealed class QaCapture
{
    /// <summary>Frame on which to resize the window to <see cref="ResizeTo"/> (points); 0 = never.</summary>
    public uint ResizeFrame { get; private init; }
    public Vector2D<int> ResizeTo { get; private init; }

    /// <summary>Frame on which to minimise the window (restored ~1.5 s later); 0 = never.</summary>
    public uint MinimizeFrame { get; private init; }

    /// <summary>
    /// First frame of a scripted right-drag (SDL mouse events pushed into the queue): button down, 20 frames
    /// of motion, button up. Exercises SDL input → Silk <c>IMouse</c> → fly-camera look; 0 = never.
    /// </summary>
    public uint InputFrame { get; private init; }

    /// <summary>Frames covered by the scripted right-drag.</summary>
    public const uint InputFrames = 22;

    /// <summary>Pushes one synthetic SDL mouse event for <paramref name="window"/> (QA input script).</summary>
    public static unsafe void PushMouse(Silk.NET.Windowing.IWindow window, EventType type, int x, int y, int dx, int dy)
    {
        var sdl = SdlProvider.SDL.Value;
        var windowId = sdl.GetWindowID(Silk.NET.Windowing.Sdl.SdlWindowing.GetHandle(window));
        var ev = new Event();
        if (type == EventType.Mousemotion)
        {
            ev.Motion = new MouseMotionEvent
            {
                Type = (uint)type,
                WindowID = windowId,
                X = x,
                Y = y,
                Xrel = dx,
                Yrel = dy,
                State = 1u << 2, // SDL_BUTTON_RMASK
            };
        }
        else
        {
            ev.Button = new MouseButtonEvent
            {
                Type = (uint)type,
                WindowID = windowId,
                Button = 3, // SDL_BUTTON_RIGHT
                State = (byte)(type == EventType.Mousebuttondown ? 1 : 0),
                Clicks = 1,
                X = x,
                Y = y,
            };
        }
        sdl.PushEvent(&ev);
    }

    /// <summary>
    /// Wakes the event loop from another thread (SDL_PushEvent is thread-safe). While minimised the
    /// engine blocks in SDL_WaitEvent, so the scripted restore needs an event to run.
    /// </summary>
    public static unsafe void WakeEventLoop()
    {
        var ev = new Event { Type = (uint)EventType.Userevent };
        SdlProvider.SDL.Value.PushEvent(&ev);
    }

    public static IReadOnlyList<uint> DefaultFrames { get; } = [30, 90, 180];

    private readonly uint[] _frames;
    private uint _pendingFrame;

    private QaCapture(string outputDirectory, uint[] frames)
    {
        OutputDirectory = outputDirectory;
        _frames = frames;
    }

    public string OutputDirectory { get; }

    /// <summary>Frames to capture, ascending; frame 1 is the first update.</summary>
    public IReadOnlyList<uint> Frames => _frames;

    /// <summary>Returns null when <c>--qa-capture</c> is absent; throws on malformed arguments.</summary>
    public static QaCapture? FromArgs(IReadOnlyList<string> args)
    {
        string? dir = null;
        uint[] frames = [.. DefaultFrames];
        uint resizeFrame = 0, minimizeFrame = 0, inputFrame = 0;
        var resizeTo = default(Vector2D<int>);

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--qa-capture":
                    dir = i + 1 < args.Count ? args[++i] : throw new ArgumentException("--qa-capture needs a directory.");
                    break;
                case "--qa-frames":
                    var list = i + 1 < args.Count ? args[++i] : throw new ArgumentException("--qa-frames needs a list, e.g. 30,90,180.");
                    frames = ParseFrames(list);
                    break;
                case "--qa-resize":
                    var spec = i + 1 < args.Count ? args[++i] : throw new ArgumentException("--qa-resize needs WxH@frame, e.g. 1280x720@60.");
                    var parts = spec.Split('@', 'x');
                    if (parts.Length != 3)
                        throw new ArgumentException($"Invalid --qa-resize '{spec}'; expected WxH@frame.");
                    resizeTo = new Vector2D<int>(int.Parse(parts[0], CultureInfo.InvariantCulture), int.Parse(parts[1], CultureInfo.InvariantCulture));
                    resizeFrame = uint.Parse(parts[2], CultureInfo.InvariantCulture);
                    break;
                case "--qa-input":
                    inputFrame = i + 1 < args.Count
                        ? uint.Parse(args[++i], CultureInfo.InvariantCulture)
                        : throw new ArgumentException("--qa-input needs a frame number.");
                    break;
                case "--qa-minimize":
                    minimizeFrame = i + 1 < args.Count
                        ? uint.Parse(args[++i], CultureInfo.InvariantCulture)
                        : throw new ArgumentException("--qa-minimize needs a frame number.");
                    break;
            }
        }

        return dir is null
            ? null
            : new QaCapture(Path.GetFullPath(dir), frames)
            {
                ResizeFrame = resizeFrame,
                ResizeTo = resizeTo,
                MinimizeFrame = minimizeFrame,
                InputFrame = inputFrame,
            };
    }

    private static uint[] ParseFrames(string list)
    {
        var frames = list
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => uint.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : throw new ArgumentException($"Invalid frame number '{f}' in --qa-frames."))
            .Distinct()
            .Order()
            .ToArray();

        return frames.Length > 0 ? frames : throw new ArgumentException("--qa-frames is empty.");
    }

    /// <summary>Deterministic, capture-enabled options that stop one frame after the last capture.</summary>
    public EngineOptions Apply(EngineOptions options) => options with
    {
        EnableFrameCapture = true,
        FixedDeltaTime = 1f / 60f,
        VSync = false,
        MaxFrames = (int)Math.Max(_frames[^1], Math.Max(ResizeFrame, Math.Max(MinimizeFrame, InputFrame + InputFrames))) + 1,
    };

    /// <summary>True when <paramref name="frameCount"/> should be captured; remembers it for <see cref="Save"/>.</summary>
    public bool ShouldCapture(uint frameCount)
    {
        if (Array.BinarySearch(_frames, frameCount) < 0)
            return false;

        _pendingFrame = frameCount;
        return true;
    }

    public void Save(FrameCapture capture)
    {
        var path = Path.Combine(OutputDirectory, $"sandbox_frame{_pendingFrame:D4}.png");
        capture.SavePng(path);
        Log.Info($"[QA] Saved {capture.Width}x{capture.Height} capture: {path}");
    }
}
