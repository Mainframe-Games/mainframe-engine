using System.Numerics;

namespace MainframeEngine;

/// <summary>How the OS window is shown (<see cref="IWindowControl.Mode"/>; Godot's <c>DisplayServer.WindowMode</c>).</summary>
public enum WindowMode
{
    /// <summary>A normal, resizable window.</summary>
    Windowed,

    /// <summary>A window filling the screen's work area, with its title bar.</summary>
    Maximized,

    /// <summary>
    /// Borderless fullscreen at the desktop's resolution (SDL's <c>SDL_WINDOW_FULLSCREEN_DESKTOP</c>, Godot's
    /// <c>WINDOW_MODE_FULLSCREEN</c>): no display mode change, so switching is instant and other windows stay usable.
    /// </summary>
    Fullscreen,

    /// <summary>
    /// Exclusive fullscreen (SDL's <c>SDL_WINDOW_FULLSCREEN</c>): the display switches to the mode closest to the window's
    /// size and refresh rate. There is no mode list yet (the window's current size picks the mode); prefer
    /// <see cref="Fullscreen"/>, which modern compositors favour.
    /// </summary>
    ExclusiveFullscreen,
}

/// <summary>
/// The OS window a tree runs in (Godot's <c>DisplayServer</c> window calls), in window points: title, mode, size,
/// position, the bounds of the screen it is on, VSync and the frame cap. <see cref="SceneTree.Window"/> is null without a
/// window (tests, tools).
/// </summary>
public interface IWindowControl
{
    string Title { get; set; }

    /// <summary>A shortcut for <see cref="Mode"/>: true in either fullscreen mode; setting it picks <see cref="WindowMode.Fullscreen"/> (borderless) or <see cref="WindowMode.Windowed"/>.</summary>
    bool Fullscreen { get; set; }

    /// <summary>Windowed, maximized, borderless or exclusive fullscreen. The swapchain follows the new size on the next frame.</summary>
    WindowMode Mode { get; set; }

    Vector2 Size { get; set; }

    Vector2 Position { get; set; }

    /// <summary>The screen the window is on (position and size in points); empty when unknown.</summary>
    Rect2 ScreenBounds { get; }

    /// <summary>Vertical sync (<see cref="IRenderer.VSync"/>; a change rebuilds the swapchain).</summary>
    bool VSync { get; set; }

    /// <summary>The frame cap (<see cref="Engine.MaxFPS"/>); 0 = unlimited. Has no effect while <see cref="VSync"/> is on.</summary>
    int MaxFps { get; set; }
}

public sealed partial class SceneTree
{
    /// <summary>The window this tree runs in, or null (tests, tools).</summary>
    public IWindowControl? Window { get; internal set; }

    /// <summary>
    /// Raised when the user closes the window, before the tree is freed (Godot's <c>NOTIFICATION_WM_CLOSE_REQUEST</c>):
    /// the last moment to save. Not raised by <see cref="Quit"/>; the close is not cancellable.
    /// </summary>
    public event Action? CloseRequested;

    internal void NotifyCloseRequested() => CloseRequested?.Invoke();
}
