using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>
/// A tool that takes over the 3D view while one node it handles is the only selection (Godot's <c>EditorPlugin</c>
/// <c>_handles</c> + <c>_forward_3d_gui_input</c>; docs/design/editor.md#viewport-tool-api): it sees the view's input
/// before the gizmo and picking, draws into the view's overlay lines and fills the right dock's tool tab. Mark the class
/// <see cref="EditorViewportToolAttribute"/>; it needs a public parameterless constructor. One instance per scene tab
/// and tool type lives until the tab closes, so <see cref="SaveExternalData"/> reaches a tool that was deactivated.
/// </summary>
public interface IEditorViewportTool
{
    /// <summary>Whether the tool edits <paramref name="node"/> (called for the single selected node).</summary>
    bool Handles(Node node);

    /// <summary>The tool takes over the view for <paramref name="node"/>.</summary>
    void Activate(IViewportToolContext context, Node node);

    /// <summary>The node is no longer the single selection (or the tab closed, or the scene started simulating).</summary>
    void Deactivate();

    /// <summary>
    /// One input event over the view. Return <see cref="ViewportToolResult.Consumed"/> to keep it from the gizmo and
    /// picking; a consumed left press captures the mouse until its release (or a <see cref="ViewportToolInputKind.Cancel"/>).
    /// Navigation never arrives here: Alt+left, middle and right drags, the plain wheel and keys with Command.
    /// </summary>
    ViewportToolResult OnInput(in ViewportToolInput input);

    /// <summary>Per frame while active, before drawing (held-still repeats, timers). Default: nothing.</summary>
    void Process(float deltaSeconds)
    {
    }

    /// <summary>Per frame while active: draws into the view's overlay lines (on top, not depth-tested).</summary>
    void DrawOverlay(DebugLines overlay);

    /// <summary>Changes whenever the dock should be rebuilt (<see cref="BuildDockRml"/>).</summary>
    int DockVersion { get; }

    /// <summary>
    /// The dock tab's body. Elements with <c>data-tool-action="name"</c> (and an optional <c>data-tool-value</c>) call
    /// <see cref="OnDockAction"/> when clicked; inputs with <c>data-tool-field="name"</c> call <see cref="OnDockField"/>
    /// when they change. Icon buttons carry <c>data-tooltip</c>s.
    /// </summary>
    string BuildDockRml();

    /// <summary>A click on a <c>data-tool-action</c> element of the dock.</summary>
    void OnDockAction(string action, string? value);

    /// <summary>A committed change of a <c>data-tool-field</c> input of the dock (Enter, focus loss, slider or checkbox).</summary>
    void OnDockField(string field, string value)
    {
    }

    /// <summary>On scene save, before the scene file is written: write data the scene file does not hold. A throw fails the save.</summary>
    void SaveExternalData()
    {
    }
}

/// <summary>Whether a tool used an input event.</summary>
public enum ViewportToolResult : byte
{
    /// <summary>The view handles it as usual (gizmo, picking, box selection).</summary>
    Pass,

    /// <summary>The tool used it.</summary>
    Consumed,
}

/// <summary>What a <see cref="ViewportToolInput"/> is.</summary>
public enum ViewportToolInputKind : byte
{
    Press,
    Release,
    Motion,

    /// <summary>The wheel with Shift or Alt held (the plain wheel zooms the view).</summary>
    Wheel,
    KeyDown,
    KeyUp,

    /// <summary>A captured drag was interrupted (Esc, right click, tab switch): keep what was already applied and end it.</summary>
    Cancel,
}

/// <summary>One input event for a viewport tool: the pointer as view pixels and as a world ray.</summary>
public readonly struct ViewportToolInput
{
    public ViewportToolInputKind Kind { get; init; }
    public MouseButton Button { get; init; }
    public Key Key { get; init; }
    public float WheelDelta { get; init; }

    /// <summary>The pointer in view pixels (origin top-left of the view).</summary>
    public Vector2 Pixel { get; init; }

    public Vector3 RayOrigin { get; init; }

    /// <summary>Normalised.</summary>
    public Vector3 RayDirection { get; init; }

    public EditorModifiers Modifiers { get; init; }

    /// <summary>Whether the pointer is over the view.</summary>
    public bool InView { get; init; }

    /// <summary>Whether the tool holds the mouse (a consumed left press not yet released).</summary>
    public bool Captured { get; init; }

    public bool Shift => (Modifiers & EditorModifiers.Shift) != 0;
    public bool Alt => (Modifiers & EditorModifiers.Alt) != 0;

    /// <summary>Command on macOS, Ctrl elsewhere.</summary>
    public bool Command => (Modifiers & EditorModifiers.Command) != 0;
}

/// <summary>What an active tool can reach.</summary>
public interface IViewportToolContext
{
    /// <summary>The tab the tool edits.</summary>
    EditedScene Scene { get; }

    UndoRedo History { get; }

    EditorCamera Camera { get; }

    /// <summary>The view's size in view pixels.</summary>
    Vector2 ViewPixels { get; }

    /// <summary>The label beside the pointer (null hides it). Set it only when its text changes.</summary>
    void SetCursorLabel(string? text);
}

/// <summary>Registers an <see cref="IEditorViewportTool"/> for <see cref="NodeType"/> and its subclasses.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class EditorViewportToolAttribute(Type nodeType) : Attribute
{
    public Type NodeType { get; } = nodeType ?? throw new ArgumentNullException(nameof(nodeType));

    /// <summary>The dock tab's title (its tooltip).</summary>
    public string Title { get; init; } = "";

    /// <summary>The dock tab's icon (an editor icon atlas name).</summary>
    public string Icon { get; init; } = "tool";
}
