using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>Modifier keys for shortcuts (<see cref="Command"/> is Cmd on macOS, Ctrl elsewhere; either is accepted).</summary>
[Flags]
public enum EditorModifiers
{
    None = 0,
    Command = 1,
    Shift = 2,
    Alt = 4,
}

/// <summary>
/// What the editor workspace needs from its process: the window (size, title, closing) and frame statistics.
/// <see cref="EditorApp"/> implements it over the engine window; tests use a headless host.
/// </summary>
public interface IEditorHost
{
    /// <summary>Window size in layout points (= dp of the editor UI layer).</summary>
    Vector2 WindowSize { get; }

    /// <summary>Framebuffer pixels per layout point (2 on Retina, or the fixed <see cref="EngineOptions.ContentScale"/>).</summary>
    float PixelScale { get; }

    /// <summary>
    /// Layout points per mouse-event point (OS window points): 1 unless a fixed content scale differs from the display's
    /// backing scale. Multiply input-event positions by it before comparing them with layout rectangles.
    /// </summary>
    float PointerScale => 1f;

    /// <summary>Smoothed frames per second and frame time (ms), for the toolbar readout.</summary>
    float FramesPerSecond { get; }

    float FrameMilliseconds { get; }

    /// <summary>Modifier keys currently held.</summary>
    EditorModifiers Modifiers { get; }

    /// <summary>
    /// True when <see cref="Modifiers"/> reads the keyboard state directly (a real window); otherwise the workspace also
    /// tracks modifiers from the key events it sees.
    /// </summary>
    bool ReportsModifiers => false;

    /// <summary>Whether the primary mouse button is held (ends file drags released outside every drop target).</summary>
    bool PrimaryMouseDown => false;

    void SetTitle(string title);

    /// <summary>Closes the editor (after the workspace has dealt with unsaved changes).</summary>
    void Quit();
}

/// <summary>A host without a window: fixed size, titles recorded (unit tests, headless smoke runs).</summary>
public sealed class HeadlessEditorHost : IEditorHost
{
    public Vector2 WindowSize { get; set; } = new(1600, 900);
    public float PixelScale { get; set; } = 1f;
    public float FramesPerSecond { get; set; } = 60f;
    public float FrameMilliseconds { get; set; } = 16.6f;
    public EditorModifiers Modifiers { get; set; }
    public string Title { get; private set; } = "";
    public bool QuitRequested { get; private set; }

    public void SetTitle(string title) => Title = title;

    public void Quit() => QuitRequested = true;
}
