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
/// pick Floor                  # tree or list picker: select the row with this label
/// search omni                 # tree or list picker: type into the search field
/// favorite OmniLight3D        # tree picker: star or un-star the row with this label
/// accept / cancel             # the open dialog's OK / Cancel
/// capture name                # saves &lt;out&gt;/name.png
/// log text                    # writes to the output
/// wait-for project|idle|playing|stopped [seconds]   # until a project and scene are open / no build runs / a game runs
/// file-drag Content/x.mscene #element-id   # drags a project file from the FileSystem panel onto an element
/// edit-file path text…        # appends a line to a project file (QA of code reload)
/// replace-in-file path old new   # edits a project file in place
/// timing label                # logs the seconds since the previous timing mark
/// set #element-id text…       # sets a field's value (unbound fields; data-bound ones: use the dialog's own step)
/// new-project folder name     # fills the New Project wizard's location and name
/// add-node Type [Name]        # adds a node under the selection (or the root) of the active scene
/// play-args args…             # Play (F5) with extra game arguments, e.g. --screenshot out.png
/// recent-project folder name [hours]   # lists a project in the Project Manager, opened hours ago (creates its project.mfproj)
/// open-project folder         # opens a project like the Project Manager does
/// camera x y z distance yaw pitch      # the active scene's editor camera: orbit pivot, distance, degrees
/// collapse Node/Path / expand Node/Path  # a scene tree row
/// quit
/// </code>
/// </summary>
public sealed class EditorQaScript : IEditorAutomation
{
    private readonly List<string[]> _steps;
    private readonly string _outputDirectory;
    private int _index;
    private int _wait;
    private Func<EditorWorkspace, bool>? _waitFor;
    private double _waitForDeadline;
    private long _waitStart;
    private long _timingMark = System.Diagnostics.Stopwatch.GetTimestamp();
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
        // QA_PROJECTS: a scratch folder for projects the script creates (<out>/projects, recreated each run).
        var projects = Path.Combine(outputDirectory, "projects");
        if (Directory.Exists(projects))
            Directory.Delete(projects, recursive: true);
        Directory.CreateDirectory(projects);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim().Replace("QA_PROJECTS", projects, StringComparison.Ordinal);
            // A comment is "#" alone or "# …" at the start or after whitespace; "#element-id" arguments are not comments.
            var comment = line == "#" ? 0 : line.StartsWith("# ", StringComparison.Ordinal) ? 0 : line.IndexOf(" # ", StringComparison.Ordinal);
            if (comment >= 0)
                line = line[..comment].Trim();
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
        workspace.TrackKeyModifiers = true; // the script's modifier keys never reach the physical keyboard state
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

        if (_waitFor is { } condition)
        {
            if (!condition(workspace) && System.Diagnostics.Stopwatch.GetElapsedTime(_waitStart).TotalSeconds < _waitForDeadline)
                return;
            if (!condition(workspace))
                Log.Warning("[QA] wait-for timed out.");
            _waitFor = null;
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
        Console.WriteLine($"[QA] {string.Join(' ', step)}"); // stdout only: the editor's Output panel shows what the app logs
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
                if (workspace.TreePicker.Visible)
                    workspace.TreePicker.SelectLabel(string.Join(' ', step[1..]));
                else
                    workspace.ListPicker.SelectLabel(string.Join(' ', step[1..]));
                break;
            case "favorite":
                if (!workspace.TreePicker.ToggleFavoriteLabel(string.Join(' ', step[1..])))
                    Log.Warning($"[QA] No row '{string.Join(' ', step[1..])}' in the tree picker.");
                break;
            case "search":
                if (workspace.TreePicker.Visible)
                    workspace.TreePicker.SetQuery(string.Join(' ', step[1..]));
                else
                    workspace.ListPicker.SetQuery(string.Join(' ', step[1..]));
                break;
            case "accept":
                if (workspace.TreePicker.Visible)
                    workspace.TreePicker.Accept();
                else if (workspace.ListPicker.Visible)
                    workspace.ListPicker.Accept();
                else if (workspace.FilePicker.Visible)
                    workspace.FilePicker.Accept();
                else if (workspace.Message.Current is { } message)
                    workspace.Message.Answer(message.DefaultButton);
                break;
            case "cancel":
                if (workspace.SignalDialog.Visible)
                    workspace.SignalDialog.Cancel();
                else if (workspace.NewProject.Visible)
                    workspace.NewProject.Cancel();
                else if (workspace.TreePicker.Visible)
                    workspace.TreePicker.Cancel();
                else if (workspace.ListPicker.Visible)
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
            case "window-close":
                // What the window's close button / Cmd+Q produce: an SDL quit event through the real event queue.
                unsafe
                {
                    var quit = new Silk.NET.SDL.Event { Type = (uint)Silk.NET.SDL.EventType.Quit };
                    Silk.NET.SDL.SdlProvider.SDL.Value.PushEvent(&quit);
                }

                break;
            case "clear-output":
                workspace.Output.Clear();
                workspace.OutputPanel.Refresh();
                break;
            case "select":
                if (workspace.Session.Active is { } selectScene)
                    selectScene.Selection.Set(selectScene.Root.GetNode(string.Join(' ', step[1..])));
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
            case "wait-for":
                _waitFor = step[1] switch
                {
                    "project" => static w => w.Project.Root is not null && !w.Project.IsBuilding && w.Session.Active is not null && !w.Splash.Visible &&
                                            !w.NewProject.Visible,
                    "idle" => static w => !w.Project.IsBuilding && !w.Play.IsBuilding,
                    "playing" => static w => w.Play.Service.Instances.Any(i => i.State == PlayInstanceState.Running && i.Frame >= 90),
                    "stopped" => static w => !w.Play.IsPlaying,
                    _ => throw new ArgumentException($"Unknown wait-for '{step[1]}'."),
                };
                _waitForDeadline = step.Length > 2 ? Float(step, 2) : 120;
                _waitStart = System.Diagnostics.Stopwatch.GetTimestamp();
                break;
            case "file-drag":
                {
                    var file = Path.Combine(workspace.Session.ProjectRoot ?? "", step[1]);
                    workspace.FileSystem.BeginDrag(file);
                    var target = Point(workspace, step, 2);
                    tree.PushInput(new InputEventMouseMotion { Position = target });
                    tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = target });
                    break;
                }

            case "edit-file":
                File.AppendAllText(Path.Combine(workspace.Session.ProjectRoot ?? "", step[1]), string.Join(' ', step[2..]) + "\n");
                break;
            case "replace-in-file":
                {
                    var file = Path.Combine(workspace.Session.ProjectRoot ?? "", step[1]);
                    var text = File.ReadAllText(file);
                    var old = step[2].Replace("\\s", " ", StringComparison.Ordinal);
                    var replacement = string.Join(' ', step[3..]).Replace("\\n", "\n", StringComparison.Ordinal);
                    if (!text.Contains(old, StringComparison.Ordinal))
                        Log.Warning($"[QA] '{old}' not found in {step[1]}.");
                    File.WriteAllText(file, text.Replace(old, replacement, StringComparison.Ordinal));
                    break;
                }

            case "set":
                {
                    var id = step[1].TrimStart('#');
                    var value = string.Join(' ', step[2..]);
                    var found = false;
                    foreach (var layer in (ReadOnlySpan<UiLayer>)[workspace.DialogLayer, workspace.ProjectLayer, workspace.PanelLayer])
                        foreach (var document in layer.Documents)
                            if (!found && document.Visible && document.IsLoaded && document.Document.GetElementById(id) is { IsNull: false } field)
                            {
                                field.SetValue(value);
                                found = true;
                            }

                    if (!found)
                        Log.Warning($"[QA] No visible field #{id}.");
                    break;
                }

            case "add-node":
                if (workspace.Session.Active is { } addTo)
                {
                    var node = Serialization.TypeRegistry.CreateNode(step[1]);
                    node.Name = step.Length > 2 ? step[2] : step[1];
                    addTo.AddNode(node);
                }

                break;
            case "recent-project":
                {
                    var folder = Path.GetFullPath(step[1]);
                    if (!File.Exists(Path.Combine(folder, ProjectSettings.FileName)))
                        new ProjectSettings { Name = step[2] }.Save(Directory.CreateDirectory(folder).FullName);
                    var hours = step.Length > 3 ? Float(step, 3) : 0f;
                    workspace.RecentProjects.Add(new RecentProject(folder, step[2], DateTime.UtcNow.AddHours(-hours)));
                    if (workspace.ProjectManager.Visible)
                        workspace.ProjectManager.Open(); // re-reads the list
                    break;
                }

            case "open-project":
                workspace.Commands.OpenProject(Path.GetFullPath(string.Join(' ', step[1..])));
                break;
            case "camera":
                if (workspace.Session.Active is { } viewed)
                {
                    viewed.Camera.Pivot = new Vector3(Float(step, 1), Float(step, 2), Float(step, 3));
                    viewed.Camera.Distance = Float(step, 4);
                    viewed.Camera.Yaw = Float(step, 5);
                    viewed.Camera.Pitch = Float(step, 6);
                }

                break;
            case "collapse" or "expand":
                if (workspace.Session.Active?.Root.GetNodeOrNull(string.Join(' ', step[1..])) is { } row)
                {
                    workspace.SceneTree.Model.SetExpanded(row, step[0] == "expand");
                    workspace.SceneTree.Refresh();
                }
                else
                {
                    Log.Warning($"[QA] No node '{string.Join(' ', step[1..])}' in the active scene.");
                }

                break;
            case "play-args":
                workspace.Play.PlayMain(step[1..]);
                break;
            case "new-project":
                workspace.NewProject.Location = step[1];
                workspace.NewProject.ProjectName = step[2];
                break;
            case "timing":
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                Log.Info($"[QA] timing {string.Join(' ', step[1..])}: {System.Diagnostics.Stopwatch.GetElapsedTime(_timingMark, now).TotalSeconds:0.00} s");
                _timingMark = now;
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
            foreach (var layer in (ReadOnlySpan<UiLayer>)[workspace.DialogLayer, workspace.ProjectLayer, workspace.PanelLayer])
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
        Console.WriteLine($"[QA] Captured {path}"); // stdout only: later captures show a clean Output panel
    }

    public void OnClosing(EditorApp app)
    {
    }
}
