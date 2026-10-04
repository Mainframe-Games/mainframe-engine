using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

namespace MainframeEngine.Editor;

/// <summary>
/// <c>--smoke &lt;dir&gt;</c>: the editor's end-to-end check, run by the render tests in a real window. With the given scene
/// open (the Sandbox): frame a known mesh and select it by GPU picking at its projected pixel, change its position through
/// the inspector model, undo/redo, save to a temp file, reload and compare (values and a byte-stable re-save), capture the
/// editor window (golden), measure frame times on the scene, then open a 1 000-node scene and measure the managed
/// allocations of idle frames. Writes <c>result.json</c> (the render tests' result format) after shutdown, so validation
/// messages from teardown are counted.
/// </summary>
public sealed class EditorSmokeRun : IEditorAutomation
{
    public const int PerfFrames = 240;
    public const int AllocationWarmup = 120;
    public const int AllocationFrames = 300;
    public const int LargeSceneNodes = 1000;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly List<string> _failures = [];
    private readonly List<object> _captures = [];
    private readonly List<double> _frameTimes = [];
    private Step _step = Step.Start;
    private int _stepFrame;
    private uint _frame;
    private Node3D? _target;
    private long _allocationStart;
    private long? _allocated;
    private long _lastTimestamp;
    private VulkanValidationLog? _validation;
    private (string Name, string Driver, string Tag) _device = ("unknown", "unknown", "unknown");
    private int _renderedFrames;
    private uint _captureFrame;

    private enum Step
    {
        Start,
        Picking,
        Edit,
        Capture,
        Perf,
        LargeScene,
        Allocations,
        Done,
    }

    public EditorSmokeRun(string outputDirectory, string? scenePath, uint captureFrame)
    {
        OutputDirectory = outputDirectory;
        ScenePath = scenePath;
        _captureFrame = captureFrame;
        Directory.CreateDirectory(outputDirectory);
    }

    /// <summary><c>--smoke-splash</c>: only show the splash screen with a fixed status and capture it (its golden).</summary>
    public bool SplashOnly { get; init; }

    public string OutputDirectory { get; }
    public string? ScenePath { get; }

    /// <summary>The frame the golden capture was requested on.</summary>
    public uint CaptureFrameNumber => _captureFrame;

    private void Fail(string message)
    {
        _failures.Add(message);
        Log.Error($"[Smoke] {message}");
    }

    private void Next(Step step)
    {
        _step = step;
        _stepFrame = 0;
    }

    public void OnFrame(EditorApp app, uint frame)
    {
        ArgumentNullException.ThrowIfNull(app);
        _frame = frame;
        _stepFrame++;
        if (SplashOnly)
        {
            RunSplash(app);
            return;
        }

        if (app.Workspace is not { } workspace || workspace.Session.Active is not { } scene)
            return;
        try
        {
            Run(app, workspace, scene);
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Fail($"{_step}: {e}");
            Next(Step.Done);
            app.Quit(ExitCode.Error);
        }
    }

    private void Run(EditorApp app, EditorWorkspace workspace, EditedScene scene)
    {
        switch (_step)
        {
            case Step.Start when _stepFrame >= 10:
                {
                    // A known mesh, framed so it fills the view centre: the Sandbox's "Column", else the first editable mesh.
                    _target = scene.Root.GetNodeOrNull<MeshInstance3D>("Column") is { Mesh: not null } column
                        ? column
                        : FindMesh(scene, scene.Root);
                    if (_target is null)
                    {
                        Fail("The scene has no mesh to pick.");
                        Next(Step.Capture);
                        break;
                    }

                    workspace.Gizmo.Mode = GizmoMode.Translate;
                    workspace.Viewport.FrameSelectionOf(_target);
                    Next(Step.Picking);
                    break;
                }

            case Step.Picking when _stepFrame == 4:
                {
                    var pixels = workspace.Viewport.ViewPixels;
                    var bounds = ((MeshInstance3D)_target!).Mesh!.Bounds.Transform(_target.ModelMatrix);
                    if (!scene.Camera.Project(bounds.Center, pixels.X, pixels.Y, out var pixel))
                        Fail("The target is behind the camera.");
                    workspace.Viewport.PickPixel(scene, pixel);
                    Log.Info($"[Smoke] Picking {_target.Name} at view pixel ({pixel.X:0}, {pixel.Y:0}).");
                    break;
                }

            case Step.Picking when _stepFrame > 4:
                if (ReferenceEquals(scene.Selection.Primary, _target))
                {
                    Log.Info($"[Smoke] Picked {_target!.Name}.");
                    Next(Step.Edit);
                }
                else if (_stepFrame > 60)
                {
                    Fail($"Picking did not select {_target!.Name} (selection: {scene.Selection.Primary?.Name ?? "none"}).");
                    scene.Selection.Set(_target);
                    Next(Step.Edit);
                }

                break;

            case Step.Edit when _stepFrame == 2:
                EditUndoSaveReload(workspace, scene);
                Next(Step.Capture);
                break;

            case Step.Capture when _stepFrame == 2:
                // Deterministic panels for the golden: no timestamps, fixed stats.
                workspace.Output.Clear();
                workspace.Output.Add(OutputLevel.Info, "Smoke: picked, edited, undone, saved and reloaded the scene.");
                workspace.Output.Add(OutputLevel.Warning, "Smoke: a warning line.");
                if (_target is not null)
                    workspace.Viewport.FrameSelectionOf(_target); // the edited (moved) node, centred for the golden
                break;

            case Step.Capture when _stepFrame == 30:
                _captureFrame = _frame;
                app.CaptureFrame();
                Next(Step.Perf);
                break;

            case Step.Perf:
                var now = Stopwatch.GetTimestamp();
                if (_stepFrame > 30)
                    _frameTimes.Add(Stopwatch.GetElapsedTime(_lastTimestamp, now).TotalMilliseconds);
                _lastTimestamp = now;
                if (_frameTimes.Count >= PerfFrames)
                    Next(Step.LargeScene);
                break;

            case Step.LargeScene:
                BuildLargeScene(workspace);
                Next(Step.Allocations);
                break;

            case Step.Allocations when _stepFrame == AllocationWarmup:
                _allocationStart = GC.GetAllocatedBytesForCurrentThread();
                break;

            case Step.Allocations when _stepFrame == AllocationWarmup + AllocationFrames:
                _allocated = GC.GetAllocatedBytesForCurrentThread() - _allocationStart;
                Log.Info($"[Smoke] {_allocated} B allocated over {AllocationFrames} idle frames with {LargeSceneNodes} nodes.");
                Next(Step.Done);
                app.Quit(ExitCode.Ok);
                break;
        }
    }

    private void RunSplash(EditorApp app)
    {
        if (app.Workspace is not { } workspace)
            return;
        switch (_frame)
        {
            case 3:
                workspace.Splash.Show("Loading Sandbox.mscene…", 0.4f);
                break;
            case 20:
                _captureFrame = _frame;
                app.CaptureFrame();
                break;
            case 24:
                Next(Step.Done);
                app.Quit(ExitCode.Ok);
                break;
        }
    }

    private static Node3D? FindMesh(EditedScene scene, Node node)
    {
        if (node is MeshInstance3D { Mesh: not null } mesh && scene.IsEditable(node) && mesh.IsVisibleInTree())
            return mesh;
        foreach (var child in node.Children)
            if (FindMesh(scene, child) is { } found)
                return found;
        return null;
    }

    private void EditUndoSaveReload(EditorWorkspace workspace, EditedScene scene)
    {
        var node = _target!;
        var inspector = workspace.Inspector;
        if (!ReferenceEquals(inspector.Target, node))
            Fail("The inspector does not show the picked node.");
        var rows = inspector.Rows;
        var row = -1;
        for (var i = 0; i < rows.Count; i++)
            if (rows[i].Name == "Position" && ReferenceEquals(rows[i].Target, node))
                row = i;
        if (row < 0)
        {
            Fail("The inspector has no Position row.");
            return;
        }

        var before = node.Position;
        inspector.Commit(row, 1, "3.5");
        Check(node.Position.Y == 3.5f, $"Position.Y is {node.Position.Y} after the inspector edit, expected 3.5.");
        Check(scene.IsDirty, "The scene is not dirty after an edit.");

        scene.History.Undo();
        Check(node.Position == before, $"Undo left Position at {node.Position}, expected {before}.");
        Check(!scene.IsDirty, "The scene is still dirty after undoing its only edit.");
        scene.History.Redo();
        Check(node.Position.Y == 3.5f, "Redo did not re-apply the edit.");

        var path = Path.Combine(OutputDirectory, "smoke_saved.mscene");
        workspace.Session.Save(scene, path);
        Check(!scene.IsDirty, "The scene is dirty after saving.");

        var saved = File.ReadAllBytes(path);
        var reloaded = PackedScene.Parse(saved, path).Instantiate();
        try
        {
            var copy = reloaded.GetNodeOrNull<Node3D>(scene.Root.GetPathTo(node));
            Check(copy is not null && copy.Position.Y == 3.5f, "The reloaded scene does not have the edited position.");
            Check(Count(reloaded) == Count(scene.Root), $"The reloaded scene has {Count(reloaded)} nodes, the edited one {Count(scene.Root)}.");
            var uid = JsonDocument.Parse(saved).RootElement.GetProperty("uid").GetString();
            var resaved = SceneSaver.ToJson(reloaded, uid);
            Check(resaved.AsSpan().SequenceEqual(saved), "Saving the reloaded scene gives different bytes (round trip not stable).");
        }
        finally
        {
            reloaded.Free();
        }
    }

    private void Check(bool condition, string failure)
    {
        if (!condition)
            Fail(failure);
    }

    private static int Count(Node node)
    {
        var count = 1;
        foreach (var child in node.Children)
            count += Count(child);
        return count;
    }

    private static void BuildLargeScene(EditorWorkspace workspace)
    {
        var scene = workspace.Session.NewScene();
        var mesh = new BoxMesh();
        var material = new StandardMaterial3D();
        var root = scene.Root;
        Node3D? group = null;
        for (var i = 0; i < LargeSceneNodes - 1; i++)
        {
            if (i % 50 == 0)
            {
                group = new Node3D { Name = $"Group{i / 50}" };
                root.AddChild(group);
                group.Owner = root;
                continue;
            }

            var box = new MeshInstance3D
            {
                Name = $"Box{i}",
                Mesh = mesh,
                MaterialOverride = material,
                Position = new Vector3(i % 40 - 20, 0, i / 40 - 12),
                Scale = new Vector3(0.4f),
            };
            group!.AddChild(box);
            box.Owner = root;
        }

        scene.History.Clear();
        workspace.SceneTree.Refresh();
        scene.Selection.Set(root.GetChild(3).GetChild(4));
    }

    public void OnFrameCaptured(EditorApp app, FrameCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        var path = Path.Combine(OutputDirectory, $"{(SplashOnly ? "editor-splash" : "editor")}_frame{_captureFrame:D4}.png");
        capture.SavePng(path);
        _captures.Add(new { Frame = _captureFrame, Path = path, capture.Width, capture.Height });
    }

    public void OnClosing(EditorApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        _renderedFrames = app.RenderedFrameCount;
        if (app.Renderer is IVulkanContext vulkan)
        {
            _validation = vulkan.Validation;
            _device = DescribeDevice(vulkan);
        }

        if (_step != Step.Done)
            Fail($"The smoke run stopped at step {_step}.");
    }

    public void OnExited(int exitCode) => WriteResult(exitCode);

    /// <summary>Writes <c>result.json</c> (after <see cref="Engine.Run"/> returned, teardown validation included).</summary>
    public void WriteResult(int exitCode)
    {
        var times = _frameTimes.Order().ToArray();
        var result = new
        {
            Scene = SplashOnly ? "editor-splash" : "editor",
            DeviceName = _device.Name,
            Driver = _device.Driver,
            PlatformTag = _device.Tag,
            ValidationEnabled = _validation?.IsEnabled ?? false,
            ValidationWarnings = _validation?.WarningCount ?? 0,
            ValidationErrors = _validation?.ErrorCount ?? 0,
            ValidationMessages = _validation?.Messages ?? [],
            RenderedFrames = _renderedFrames,
            ExitCode = exitCode,
            SceneCheckFailures = _failures,
            AllocatedBytes = _allocated,
            MeasuredFrames = _allocated is null ? 0 : AllocationFrames,
            Captures = _captures,
            AverageFrameMs = times.Length > 0 ? times.Average() : 0,
            P95FrameMs = times.Length > 0 ? times[(int)Math.Min(times.Length - 1, Math.Ceiling(times.Length * 0.95) - 1)] : 0,
            PerfMeasuredFrames = times.Length,
#if DEBUG
            Configuration = "Debug",
#else
            Configuration = "Release",
#endif
        };
        File.WriteAllText(Path.Combine(OutputDirectory, "result.json"),
            JsonSerializer.Serialize(result, JsonOptions));
    }

    private static unsafe (string, string, string) DescribeDevice(IVulkanContext vulkan)
    {
        var driverProps = new PhysicalDeviceDriverProperties { SType = StructureType.PhysicalDeviceDriverProperties };
        var props = new PhysicalDeviceProperties2 { SType = StructureType.PhysicalDeviceProperties2, PNext = &driverProps };
        vulkan.Vk.GetPhysicalDeviceProperties2(vulkan.PhysicalDevice, &props);
        var name = SilkMarshal.PtrToString((nint)props.Properties.DeviceName) ?? "unknown";
        var driver = driverProps.DriverID.ToString().Replace("DriverID", "", StringComparison.Ordinal);
        var tag = driverProps.DriverID switch
        {
            DriverId.Moltenvk => "moltenvk",
            DriverId.MesaLlvmpipe => "lavapipe",
            _ => driver.ToLowerInvariant(),
        };
        return (name, driver, tag);
    }
}
