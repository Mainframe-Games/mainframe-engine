using System.Globalization;
using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor;

/// <summary>
/// <c>--qa-script &lt;file&gt; --qa-out &lt;dir&gt;</c>: drives the real editor with input pushed through the scene tree's input
/// path (UI first, then the editor's nodes — the same routing as SDL input) and captures frames, for QA where
/// computer-use cannot target the unbundled process. One command per line (<c>#</c> comments):
/// <code>
/// wait 30                     # frames
/// click 120 40 [right|middle] # window points, or: click #element-id (any editor document)
/// drag 600 400 700 420 [left|right|middle] [frames]
/// move 600 400
/// wheel 1                     # over the last mouse position
/// key W [cmd] [shift] [alt]   # press and release (Silk key names)
/// hold W / release W
/// type Box2                   # text into the focused field
/// command file.save           # any EditorCommands id
/// open path/to/scene.mscene
/// menu 2                      # choose item 2 of the open popup menu (or: menu edit.undo)
/// pick Floor                  # list picker: select the row with this label
/// accept / cancel             # the open dialog's OK / Cancel
/// capture name                # saves &lt;out&gt;/name.png
/// log text                    # writes to the output
/// quit
/// </code>
/// </summary>
public sealed class EditorQaScript : IEditorAutomation
{
    private readonly List<string[]> _steps;
    private readonly string _outputDirectory;
    private int _index;
    private int _wait;
    private string? _pendingCapture;
    private Vector2 _mouse;
    private (Vector2 From, Vector2 To, MouseButton Button, int Frames, int Done)? _drag;

    private EditorQaScript(List<string[]> steps, string outputDirectory)
    {
        _steps = steps;
        _outputDirectory = outputDirectory;
        Directory.CreateDirectory(outputDirectory);
    }

    public static EditorQaScript Load(string path, string outputDirectory)
    {
        var steps = new List<string[]>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            var comment = line.IndexOf('#', StringComparison.Ordinal);
            if (comment == 0 || (comment > 0 && !line.StartsWith("click #", StringComparison.Ordinal)))
                line = comment == 0 ? "" : line[..comment].Trim();
            if (line.Length > 0)
                steps.Add(line.Split(' ', 2) is [var verb, var rest] ? [verb, .. rest.Split(' ', StringSplitOptions.RemoveEmptyEntries)] : [line]);
        }

        return new EditorQaScript(steps, outputDirectory);
    }

    public void OnFrame(EditorApp app, uint frame)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.Workspace is not { } workspace)
            return;
        if (_drag is { } drag)
        {
            StepDrag(app, drag);
            return;
        }

        if (_wait > 0)
        {
            _wait--;
            return;
        }

        if (_index >= _steps.Count)
        {
            app.Quit(ExitCode.Ok);
            return;
        }

        var step = _steps[_index++];
        try
        {
            Execute(app, workspace, step);
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Log.Error($"[QA] Step {_index} '{string.Join(' ', step)}' failed: {e.Message}");
        }
    }

    private void Execute(EditorApp app, EditorWorkspace workspace, string[] step)
    {
        Log.Info($"[QA] {string.Join(' ', step)}");
        var tree = app.Tree;
        switch (step[0])
        {
            case "wait":
                _wait = Int(step, 1, 1);
                break;
            case "move":
                _mouse = Point(workspace, step, 1);
                tree.PushInput(new InputEventMouseMotion { Position = _mouse });
                break;
            case "click":
                _mouse = Point(workspace, step, 1);
                var button = ButtonOf(step.Length > (step[1].StartsWith('#') ? 2 : 3) ? step[^1] : "left");
                tree.PushInput(new InputEventMouseMotion { Position = _mouse });
                tree.PushInput(new InputEventMouseButton { Button = button, Pressed = true, Position = _mouse });
                tree.PushInput(new InputEventMouseButton { Button = button, Pressed = false, Position = _mouse });
                break;
            case "drag":
                var from = new Vector2(Float(step, 1), Float(step, 2));
                var to = new Vector2(Float(step, 3), Float(step, 4));
                var dragButton = ButtonOf(step.Length > 5 ? step[5] : "left");
                _drag = (from, to, dragButton, step.Length > 6 ? Int(step, 6, 20) : 20, 0);
                _mouse = from;
                tree.PushInput(new InputEventMouseMotion { Position = from });
                tree.PushInput(new InputEventMouseButton { Button = dragButton, Pressed = true, Position = from });
                break;
            case "gizmo-drag":
                {
                    // gizmo-drag x|y|z|center dx dy [frames]: drags the selected node's handle by (dx, dy) points.
                    var handle = GizmoPoint(workspace, step[1]);
                    var end = handle + new Vector2(Float(step, 2), Float(step, 3));
                    _drag = (handle, end, MouseButton.Left, step.Length > 4 ? Int(step, 4, 20) : 20, 0);
                    _mouse = handle;
                    tree.PushInput(new InputEventMouseMotion { Position = handle });
                    tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = handle });
                    break;
                }

            case "wheel":
                tree.PushInput(new InputEventMouseWheel { Delta = new Vector2(0, Float(step, 1)) });
                break;
            case "key":
                PressKey(tree, step, press: true, release: true);
                break;
            case "hold":
                PressKey(tree, step, press: true, release: false);
                break;
            case "release":
                PressKey(tree, step, press: false, release: true);
                break;
            case "type":
                foreach (var c in string.Join(' ', step[1..]))
                    tree.PushInput(new InputEventText { Character = c });
                break;
            case "command":
                workspace.Commands.Execute(step[1]);
                break;
            case "open":
                workspace.Session.Open(Path.GetFullPath(string.Join(' ', step[1..])));
                break;
            case "menu":
                if (int.TryParse(step[1], CultureInfo.InvariantCulture, out var item))
                    workspace.Popup.Choose(item);
                else
                    workspace.Popup.Choose(step[1]);
                break;
            case "pick":
                workspace.ListPicker.SelectLabel(string.Join(' ', step[1..]));
                break;
            case "accept":
                if (workspace.ListPicker.Visible)
                    workspace.ListPicker.Accept();
                else if (workspace.FilePicker.Visible)
                    workspace.FilePicker.Accept();
                else if (workspace.Message.Current is { } message)
                    workspace.Message.Answer(message.DefaultButton);
                break;
            case "cancel":
                if (workspace.ListPicker.Visible)
                    workspace.ListPicker.Cancel();
                else if (workspace.FilePicker.Visible)
                    workspace.FilePicker.Cancel();
                else if (workspace.Message.Current is { } message)
                    workspace.Message.Answer(message.CancelButton >= 0 ? message.CancelButton : message.Buttons.Count - 1);
                else
                    workspace.Popup.Close();
                break;
            case "capture":
                _pendingCapture = step.Length > 1 ? step[1] : $"frame{_index}";
                app.CaptureFrame();
                break;
            case "log":
                Log.Info($"[QA] {string.Join(' ', step[1..])}");
                break;
            case "state":
                if (workspace.Session.Active is { } active)
                {
                    var c = active.Camera;
                    Log.Info($"[QA] camera pivot {c.Pivot} distance {c.Distance:0.###} yaw {c.Yaw:0.#} pitch {c.Pitch:0.#}; " +
                             $"selection {active.Selection.Primary?.Name ?? "none"}; history {active.History.Position}/{active.History.Actions.Count}; " +
                             $"dirty {active.IsDirty}");
                }

                break;
            case "quit":
                app.Quit(ExitCode.Ok);
                break;
            default:
                Log.Warning($"[QA] Unknown step '{step[0]}'.");
                break;
        }
    }

    private void StepDrag(EditorApp app, (Vector2 From, Vector2 To, MouseButton Button, int Frames, int Done) drag)
    {
        var done = drag.Done + 1;
        var position = Vector2.Lerp(drag.From, drag.To, Math.Min(1f, done / (float)drag.Frames));
        app.Tree.PushInput(new InputEventMouseMotion { Position = position, Relative = position - _mouse });
        _mouse = position;
        if (done >= drag.Frames)
        {
            app.Tree.PushInput(new InputEventMouseButton { Button = drag.Button, Pressed = false, Position = position });
            _drag = null;
            return;
        }

        _drag = drag with { Done = done };
    }

    private static void PressKey(SceneTree tree, string[] step, bool press, bool release)
    {
        if (!Enum.TryParse<Key>(step[1], ignoreCase: true, out var key))
            throw new ArgumentException($"Unknown key '{step[1]}'.");
        var modifiers = new List<Key>();
        foreach (var flag in step.Skip(2))
            modifiers.Add(flag switch
            {
                "cmd" or "ctrl" => OperatingSystem.IsMacOS() ? Key.SuperLeft : Key.ControlLeft,
                "shift" => Key.ShiftLeft,
                "alt" => Key.AltLeft,
                _ => throw new ArgumentException($"Unknown modifier '{flag}'."),
            });
        if (press)
        {
            foreach (var m in modifiers)
                tree.PushInput(new InputEventKey { Key = m, Pressed = true });
            tree.PushInput(new InputEventKey { Key = key, Pressed = true });
        }

        if (release)
        {
            tree.PushInput(new InputEventKey { Key = key, Pressed = false });
            for (var i = modifiers.Count - 1; i >= 0; i--)
                tree.PushInput(new InputEventKey { Key = modifiers[i], Pressed = false });
        }
    }

    /// <summary>A point from "x y" (window points) or "#id" (the centre of an element of any editor document).</summary>
    private static Vector2 Point(EditorWorkspace workspace, string[] step, int at)
    {
        if (step[at].StartsWith('#'))
        {
            var id = step[at][1..];
            foreach (var layer in (ReadOnlySpan<UiLayer>)[workspace.DialogLayer, workspace.PanelLayer])
                foreach (var document in layer.Documents)
                {
                    if (!document.Visible || !document.IsLoaded || document.Document.GetElementById(id) is not { IsNull: false } element)
                        continue;
                    var b = element.Bounds;
                    var scale = MathF.Max(0.01f, workspace.Host.PixelScale);
                    return new Vector2(b.X + b.Width * 0.5f, b.Y + b.Height * 0.5f) / scale;
                }

            throw new ArgumentException($"No visible element #{id}.");
        }

        return new Vector2(Float(step, at), Float(step, at + 1));
    }

    /// <summary>Window point of a gizmo handle of the primary selection (70% along the axis; the centre for "center").</summary>
    private static Vector2 GizmoPoint(EditorWorkspace workspace, string axisName)
    {
        if (workspace.Session.Active is not { Selection.Primary: Node3D node } scene)
            throw new ArgumentException("gizmo-drag needs a selected 3D node.");
        var gizmo = workspace.Gizmo;
        var view = workspace.Viewport.ViewPixels;
        var axes = gizmo.Axes(node.GlobalRotation);
        var origin = node.GlobalPosition;
        var length = TransformGizmo.SizePixels * gizmo.PixelScale * scene.Camera.WorldPerPixel(origin, view.Y);
        var target = axisName switch
        {
            "x" => origin + axes.X * length * 0.7f,
            "y" => origin + axes.Y * length * 0.7f,
            "z" => origin + axes.Z * length * 0.7f,
            _ => origin,
        };
        if (gizmo.Mode == GizmoMode.Rotate && axisName != "center")
        {
            var axis = axisName switch { "x" => axes.X, "y" => axes.Y, _ => axes.Z };
            var (u, _) = TransformGizmo.Perpendiculars(axis);
            target = origin + u * length;
        }

        scene.Camera.Project(target, view.X, view.Y, out var pixel);
        var rect = workspace.Viewport.ViewRect;
        return new Vector2(rect.X, rect.Y) + pixel / workspace.Host.PixelScale;
    }

    private static MouseButton ButtonOf(string name) => name switch
    {
        "right" => MouseButton.Right,
        "middle" => MouseButton.Middle,
        _ => MouseButton.Left,
    };

    private static float Float(string[] step, int at) => float.Parse(step[at], CultureInfo.InvariantCulture);

    private static int Int(string[] step, int at, int fallback) =>
        step.Length > at && int.TryParse(step[at], CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public void OnFrameCaptured(EditorApp app, FrameCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var path = Path.Combine(_outputDirectory, (_pendingCapture ?? "capture") + ".png");
        capture.SavePng(path);
        Log.Info($"[QA] Captured {path}");
    }

    public void OnClosing(EditorApp app)
    {
    }
}
