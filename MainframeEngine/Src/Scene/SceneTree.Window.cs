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

    /// <summary>
    /// Raised when files or folders are dragged from the OS (Finder, Explorer, a file manager) and dropped on the window
    /// (Godot's <c>Window.files_dropped</c>), once per drop with every path. Raised on the main thread while the frame's
    /// events are processed. There is no drag-over event: nothing knows a drag is coming until it lands.
    /// </summary>
    public event Action<FilesDroppedEvent>? FilesDropped;

    /// <summary>Raises <see cref="FilesDropped"/> (the engine, from the window; tests). Nothing happens for no paths.</summary>
    internal void NotifyFilesDropped(IReadOnlyList<string> paths, Vector2 position)
    {
        ArgumentNullException.ThrowIfNull(paths);
        if (paths.Count > 0)
            FilesDropped?.Invoke(new FilesDroppedEvent(paths, position));
    }
}

/// <summary>A drop of OS files on the window (<see cref="SceneTree.FilesDropped"/>).</summary>
/// <param name="Paths">The dropped files and folders: absolute paths, in the order the OS gave them.</param>
/// <param name="Position">
/// Where the pointer was when they were dropped, in window points from the top-left corner: the units of mouse input
/// events. SDL reports no position with a drop, so it is the pointer's position as the drop arrives (on Wayland, where a
/// client cannot read the global pointer, it may be the last known one).
/// </param>
public readonly record struct FilesDroppedEvent(IReadOnlyList<string> Paths, Vector2 Position);
