using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The OS window a tree runs in (Godot's <c>DisplayServer</c> window calls), in window points: title, fullscreen, size,
/// position and the bounds of the screen it is on. <see cref="SceneTree.Window"/> is null without a window (tests, tools).
/// </summary>
public interface IWindowControl
{
    string Title { get; set; }

    bool Fullscreen { get; set; }

    Vector2 Size { get; set; }

    Vector2 Position { get; set; }

    /// <summary>The screen the window is on (position and size in points); empty when unknown.</summary>
    Rect2 ScreenBounds { get; }
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
