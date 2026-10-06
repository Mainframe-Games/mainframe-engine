using Silk.NET.SDL;

namespace MainframeEngine.RenderTests.Host;

/// <summary>Synthetic SDL events for the host's <c>--input</c> and <c>--minimize</c> scripts.</summary>
public static class QaSdl
{
    /// <summary>
    /// Pushes one synthetic right-button SDL mouse event for <paramref name="window"/> (button down/up or motion; a motion
    /// holds <paramref name="buttons"/>, SDL's button mask: the right button by default).
    /// </summary>
    public static unsafe void PushMouse(Silk.NET.Windowing.IWindow window, EventType type, int x, int y, int dx, int dy,
        uint buttons = 1u << 2)
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
                State = buttons, // default SDL_BUTTON_RMASK
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

    /// <summary>Pushes a key press or release of <paramref name="key"/> to <paramref name="windowId"/>.</summary>
    public static unsafe void PushKey(uint windowId, KeyCode key, Scancode scancode, bool pressed)
    {
        var ev = new Event();
        ev.Key = new KeyboardEvent
        {
            Type = (uint)(pressed ? EventType.Keydown : EventType.Keyup),
            WindowID = windowId,
            State = (byte)(pressed ? 1 : 0),
            Keysym = new Keysym { Sym = (int)key, Scancode = scancode },
        };
        SdlProvider.SDL.Value.PushEvent(&ev);
    }

    /// <summary>Pushes a window event (<paramref name="id"/>: Exposed, Moved, ...) to <paramref name="windowId"/>.</summary>
    public static unsafe void PushWindowEvent(uint windowId, WindowEventID id)
    {
        var ev = new Event();
        ev.Window = new WindowEvent { Type = (uint)EventType.Windowevent, WindowID = windowId, Event = (byte)id };
        SdlProvider.SDL.Value.PushEvent(&ev);
    }

    /// <summary>
    /// Wakes the event loop from another thread (SDL_PushEvent is thread-safe). While minimised the engine blocks in
    /// SDL_WaitEvent, so the scripted restore needs an event to run.
    /// </summary>
    public static unsafe void WakeEventLoop(uint windowId)
    {
        if (!WakeEnabled)
            return;
        PushWindowEvent(windowId, WindowEventID.Exposed);
    }

    /// <summary>The SDL id of <paramref name="window"/>.</summary>
    public static unsafe uint WindowId(Silk.NET.Windowing.IWindow window) =>
        SdlProvider.SDL.Value.GetWindowID(Silk.NET.Windowing.Sdl.SdlWindowing.GetHandle(window));

    /// <summary>Gate for <see cref="WakeEventLoop"/>; cleared before the timer is disposed and SDL shuts down.</summary>
    public static bool WakeEnabled
    {
        get => Volatile.Read(ref _wakeEnabled);
        set => Volatile.Write(ref _wakeEnabled, value);
    }

    private static bool _wakeEnabled;
}
