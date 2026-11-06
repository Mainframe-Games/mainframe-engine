using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

// Viewport tools (IEditorViewportTool, docs/design/editor.md#viewport-tool-api): the active tool sees the view's input
// after navigation (Alt+left, middle and right drags, the plain wheel, keys with Command) and before the gizmo, handles
// and picking. A consumed left press captures the mouse until its release (DragKind.Tool).
public sealed partial class ViewportController
{
    private int _toolLabelVersion = -1;

    private ViewportTools Tools => _workspace.Tools;

    // Per frame (3D tabs): which tool is active, the view size it sees, its overlay and its cursor label.
    private void UpdateTools(EditedScene scene, Vector2 pixels, float deltaSeconds)
    {
        var enabled = !scene.Camera.Is2D && !IsSimulating(scene) && _workspace.Mode == EditorMode.Game;
        Tools.ViewPixels = pixels;
        var before = Tools.Active;
        Tools.Update(scene, enabled, deltaSeconds);
        _workspace.Inspector.TickTool();
        if (_drag == DragKind.Tool && (Tools.Active is null || !ReferenceEquals(before, Tools.Active)))
            _drag = DragKind.None; // deactivated mid-drag (it got Cancel)
        // Moved with the pointer by the input handlers; here only when the text changed (no per-frame strings).
        if (Tools.Active is null || Tools.CursorLabel is null ? _toolLabelVersion != -1 : Tools.CursorLabelVersion != _toolLabelVersion)
            ShowToolLabel();
    }

    private ViewportToolInput ToolInput(EditedScene scene, ViewportToolInputKind kind)
    {
        var pixel = LocalPixel(_mouse);
        var size = ViewPixels;
        var (origin, direction) = scene.Camera.Ray(pixel.X, pixel.Y, size.X, size.Y);
        return new ViewportToolInput
        {
            Kind = kind,
            Pixel = pixel,
            RayOrigin = origin,
            RayDirection = direction,
            Modifiers = _workspace.Modifiers,
            InView = ViewRect.Contains(_mouse.X, _mouse.Y),
        };
    }

    // A left press without Alt goes to the active tool first.
    private bool ToolPress(EditedScene scene, InputEventMouseButton e)
    {
        if (Tools.Active is null || e.Button != MouseButton.Left || (_workspace.Modifiers & EditorModifiers.Alt) != 0)
            return false;
        if (Tools.Send(ToolInput(scene, ViewportToolInputKind.Press) with { Button = e.Button }) != ViewportToolResult.Consumed)
            return false;
        _drag = DragKind.Tool;
        ShowToolLabel();
        return true;
    }

    private void ToolRelease(EditedScene scene, MouseButton button)
    {
        Tools.Send(ToolInput(scene, ViewportToolInputKind.Release) with { Button = button });
        ShowToolLabel();
    }

    // Motion while dragging (captured) or hovering: the tool's ring follows the pointer. True when the tool took it.
    private bool ToolMotion(EditedScene scene)
    {
        if (Tools.Active is null)
            return false;
        var consumed = Tools.Send(ToolInput(scene, ViewportToolInputKind.Motion)) == ViewportToolResult.Consumed;
        ShowToolLabel();
        return consumed;
    }

    // The wheel with Shift or Alt (the plain wheel zooms). True when the tool took it.
    private bool ToolWheel(EditedScene scene, float delta)
    {
        if (Tools.Active is null || scene.Camera.Is2D || (_workspace.Modifiers & (EditorModifiers.Shift | EditorModifiers.Alt)) == 0)
            return false;
        var consumed = Tools.Send(ToolInput(scene, ViewportToolInputKind.Wheel) with { WheelDelta = delta }) == ViewportToolResult.Consumed;
        ShowToolLabel();
        return consumed;
    }

    // Keys without Command while the pointer is over the view (or the tool holds the mouse). True when the tool took it.
    private bool ToolKey(EditedScene scene, InputEventKey key)
    {
        if (Tools.Active is null || scene.Camera.Is2D || (_workspace.Modifiers & EditorModifiers.Command) != 0)
            return false;
        if (!Tools.Captured && !ViewRect.Contains(_mouse.X, _mouse.Y))
            return false;
        var kind = key.Pressed ? ViewportToolInputKind.KeyDown : ViewportToolInputKind.KeyUp;
        var consumed = Tools.Send(ToolInput(scene, kind) with { Key = key.Key }) == ViewportToolResult.Consumed;
        ShowToolLabel();
        // Modifier keys always reach the workspace too (it tracks Shift, Alt and Command from them).
        return consumed && key.Key is not (Key.ShiftLeft or Key.ShiftRight or Key.AltLeft or Key.AltRight or Key.ControlLeft
            or Key.ControlRight or Key.SuperLeft or Key.SuperRight);
    }

    private void CancelTool()
    {
        Tools.Cancel();
        ShowToolLabel();
    }

    // The tool's cursor label beside the pointer, moved with it.
    private void ShowToolLabel()
    {
        var text = Tools.Active is not null ? Tools.CursorLabel : null;
        if (text is null && _toolLabelVersion == -1)
            return;
        var rect = ViewRect;
        _workspace.ViewportPanel.SetHandleLabel(text, _mouse.X - rect.X + 18f, _mouse.Y - rect.Y + 14f);
        _toolLabelVersion = text is null ? -1 : Tools.CursorLabelVersion;
    }
}
