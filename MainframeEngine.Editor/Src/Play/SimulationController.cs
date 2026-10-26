using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// Simulate (editor-viewport-tools G7.5, Alt+F6): runs the active scene's physics and game scripts inside the editor's
/// view, on a copy. Start packs the scene (unsaved edits included) and instantiates it under a twin
/// <see cref="SubViewport"/> with <see cref="SubViewport.Simulation"/> = <see cref="ViewportSimulation.Running"/> (its
/// own world and physics space), rendered through the tab's editor camera and shown instead of the edited view; Stop
/// frees the copy, so the authored scene, its history and the selection are never touched. While it runs the scene's
/// history is locked, the inspector shows the simulated counterparts live and read-only, and Keep Transforms copies the
/// selected counterparts' positions and rotations back as one undo step. A game exception, a tab switch, a code reload
/// or closing the scene stops it.
/// </summary>
public sealed class SimulationController
{
    private const double InspectorRefreshSeconds = 0.1;

    private readonly EditorWorkspace _workspace;
    private readonly HashSet<Resource> _watched = [];
    private readonly HashSet<Resource> _changed = [];
    private readonly Dictionary<Resource, Action> _watchHandlers = [];
    private PackedScene? _packed;
    private double _refresh;

    public SimulationController(EditorWorkspace workspace)
    {
        _workspace = workspace ?? throw new ArgumentNullException(nameof(workspace));
    }

    /// <summary>The scene being simulated, or null.</summary>
    public EditedScene? Scene { get; private set; }

    /// <summary>The twin view the copy runs in (null when not simulating).</summary>
    public SubViewport? Viewport { get; private set; }

    /// <summary>The simulated copy of the scene's root.</summary>
    public Node? Root { get; private set; }

    /// <summary>True while a scene simulates (running or paused).</summary>
    public bool IsActive => Scene is not null;

    /// <summary>True while the simulation is paused (the camera still moves).</summary>
    public bool IsPaused => Viewport?.Simulation == ViewportSimulation.Paused;

    /// <summary>Raised after a simulation started or stopped (the toolbar, inspector and viewport follow).</summary>
    public event Action? Changed;

    /// <summary>True when <paramref name="scene"/> is the one simulating.</summary>
    public bool IsSimulating(EditedScene scene) => ReferenceEquals(scene, Scene);

    /// <summary>The view shown for <paramref name="scene"/> while it simulates, else null (its own view).</summary>
    public SubViewport? ViewportOf(EditedScene scene) => ReferenceEquals(scene, Scene) ? Viewport : null;

    /// <summary>The simulated node at <paramref name="edited"/>'s path (the selection boxes and the live inspector), or null.</summary>
    public Node? CounterpartOf(Node edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        if (Scene is not { } scene || Root is not { IsFreed: false } root)
            return null;
        if (ReferenceEquals(edited, scene.Root))
            return root;
        return scene.Root.IsAncestorOf(edited) ? root.GetNodeOrNull(scene.Root.GetPathTo(edited)) : null;
    }

    /// <summary>The edited node at <paramref name="simulated"/>'s path (a click in the simulated view selects it), or null.</summary>
    public Node? EditedFor(Node simulated)
    {
        ArgumentNullException.ThrowIfNull(simulated);
        if (Scene is not { } scene || Root is not { IsFreed: false } root)
            return null;
        if (ReferenceEquals(simulated, root))
            return scene.Root;
        return root.IsAncestorOf(simulated) ? scene.Root.GetNodeOrNull(root.GetPathTo(simulated)) : null;
    }

    /// <summary>Simulate / Stop (Alt+F6).</summary>
    public void Toggle()
    {
        if (IsActive)
            Stop();
        else
            Start();
    }

    /// <summary>
    /// Starts simulating the active scene. When the game code changed since the last reload, asks to Build &amp; Reload
    /// first (Simulate uses the loaded game assembly). False when nothing started (no scene, a question asked, a failure).
    /// </summary>
    public bool Start()
    {
        if (IsActive || _workspace.Session.Active is not { } scene)
            return false;
        if (_workspace.Project.NeedsRebuild)
        {
            _workspace.Message.Show(new MessageRequest
            {
                Title = "Simulate",
                Message = "Game code changed since the last reload: Build & Reload first?",
                Buttons = ["Build & Reload", "Simulate Anyway", "Cancel"],
                DefaultButton = 0,
                CancelButton = 2,
                Callback = (button, _) =>
                {
                    if (button == 0)
                        _workspace.Project.BuildAndReload();
                    else if (button == 1)
                        StartNow(scene);
                },
            });
            return false;
        }

        return StartNow(scene);
    }

    /// <summary>Starts simulating <paramref name="scene"/> without asking (tests, QA and the dialog's Simulate Anyway).</summary>
    public bool StartNow(EditedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (IsActive || !_workspace.Session.Scenes.Contains(scene) || scene.Viewport.Parent is not { } host || scene.Viewport.Tree is not { } tree)
            return false;
        _workspace.Viewport.CancelInteraction();
        // Game code runs inside the editor: a hang or a native crash would lose unsaved work, so keep a copy first.
        if (scene.IsDirty && UsesGameCode(scene.Root))
            _workspace.WriteRecoveryCopies();

        tree.ClearSimulationFault();
        PackedScene packed;
        try
        {
            packed = PackedScene.Pack(scene.Root);
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            _workspace.Commands.ReportError("Could not simulate the scene", e);
            return false;
        }

        var edited = scene.Viewport;
        var viewport = new SubViewport
        {
            Name = "Simulation",
            Simulation = ViewportSimulation.Running,
            Width = edited.Width,
            Height = edited.Height,
            Disable3D = edited.Disable3D,
            Shadows = true,
            ClearColor = edited.ClearColor,
            CameraOverride = scene.Camera.ActiveCamera,
        };
        host.AddChild(viewport);
        edited.UpdateMode = SubViewportUpdateMode.Disabled; // the edited world stops rendering; the copy is shown instead
        Scene = scene;
        Viewport = viewport;
        _packed = packed;
        scene.History.EndMerge();
        scene.History.IsLocked = true;
        WatchExternalResources(scene);
        try
        {
            var root = packed.Instantiate();
            Root = root;
            viewport.AddChild(root); // the game's OnEnterTree and OnReady run for real here
        }
        catch (Exception e) when (EditorCommands.IsRecoverable(e))
        {
            Log.Error($"[Simulate] The scene failed to start: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            Stop("Simulation stopped: the scene failed to start.");
            return false;
        }

        _refresh = 0;
        Log.Info($"[Editor] Simulating {scene.DisplayName} (Alt+F6 stops; the scene is restored).");
        Changed?.Invoke();
        return true;
    }

    /// <summary>Pauses or resumes the simulation (the camera still moves).</summary>
    public void TogglePause()
    {
        if (Viewport is not { } viewport)
            return;
        viewport.Simulation = viewport.Simulation == ViewportSimulation.Paused ? ViewportSimulation.Running : ViewportSimulation.Paused;
        Changed?.Invoke();
    }

    /// <summary>
    /// Stops the simulation: frees the copy and shows the authored scene again, exactly as it was. <paramref name="reason"/>
    /// (the sentence logged instead of "Simulation stopped.": a fault, a code reload) goes to the Output.
    /// </summary>
    public void Stop(string? reason = null)
    {
        if (Scene is not { } scene)
            return;
        var viewport = Viewport;
        var tree = viewport?.Tree;
        _workspace.Viewport.CancelInteraction();
        Scene = null;
        Viewport = null;
        Root = null;
        scene.History.IsLocked = false;
        if (viewport is { IsFreed: false })
        {
            try
            {
                viewport.Free(); // the game's OnExitTree runs here
            }
            catch (Exception e) when (EditorCommands.IsRecoverable(e))
            {
                Log.Error($"[Simulate] {e.GetType().Name} while stopping: {e.Message}\n{e.StackTrace}");
            }
        }

        if (!scene.Viewport.IsFreed && ReferenceEquals(_workspace.Session.ActiveTab, scene))
            scene.Viewport.UpdateMode = SubViewportUpdateMode.Always;
        if (_packed is { ReferenceCount: > 0 } packed)
            packed.Release();
        _packed = null;
        tree?.ClearSimulationFault();
        ReportChangedResources();
        Log.Info($"[Editor] {reason ?? "Simulation stopped."}");
        Changed?.Invoke();
    }

    /// <summary>
    /// Keep Transforms (Unreal's Keep Simulation Changes for transforms): reads the selected nodes' simulated
    /// <c>Position</c> and <c>RotationDegrees</c>, stops, and applies them to the edited nodes as one undo step. False when
    /// nothing is simulating or nothing moved.
    /// </summary>
    public bool KeepTransforms()
    {
        if (Scene is not { } scene)
            return false;
        var kept = new List<(Node Node, object Position, object Rotation)>();
        foreach (var node in scene.Selection.Nodes)
        {
            switch (CounterpartOf(node))
            {
                case Node3D n3 when node is Node3D:
                    kept.Add((node, n3.Position, n3.RotationDegrees));
                    break;
                case Node2D n2 when node is Node2D:
                    kept.Add((node, n2.Position, n2.RotationDegrees));
                    break;
            }
        }

        Stop();
        var actions = new List<IEditorAction>();
        foreach (var (node, position, rotation) in kept)
        {
            if (node.IsFreed || TypeRegistry.GetNearest(node.GetType()) is not { } info)
                continue;
            if (info.FindProperty("Position") is { } p && !Equals(p.GetValue(node), position))
                actions.Add(SetPropertyAction.Create(node, p, position));
            if (info.FindProperty("RotationDegrees") is { } r && !Equals(r.GetValue(node), rotation))
                actions.Add(SetPropertyAction.Create(node, r, rotation));
        }

        if (actions.Count == 0)
        {
            Log.Info("[Editor] Keep Transforms: the selected nodes did not move.");
            return false;
        }

        scene.History.Commit(new CompositeAction(kept.Count == 1 ? $"Keep Transform of {kept[0].Node.Name}" : $"Keep Transforms of {kept.Count} nodes", [.. actions]));
        return true;
    }

    /// <summary>Per frame (from the workspace): stops on a fault or a tab switch, refreshes the live inspector at 10 Hz.</summary>
    public void Tick(double delta)
    {
        if (Scene is not { } scene)
            return;
        if (Viewport is not { IsFreed: false } viewport || !ReferenceEquals(_workspace.Session.ActiveTab, scene) || scene.Viewport.IsFreed)
        {
            Stop("Simulation stopped: its tab is no longer active.");
            return;
        }

        if (viewport.Tree?.SimulationFault is { } fault)
        {
            Stop($"Simulation stopped by {fault.GetType().Name}: {fault.Message} (see the error above).");
            return;
        }

        _refresh -= delta;
        if (_refresh > 0)
            return;
        _refresh = InspectorRefreshSeconds;
        _workspace.Inspector.RefreshValues();
    }

    // True when the scene holds nodes of the game's (collectible) assembly: its code would run in the editor.
    private static bool UsesGameCode(Node node)
    {
        if (node.GetType().Assembly.IsCollectible)
            return true;
        foreach (var child in node.Children)
            if (UsesGameCode(child))
                return true;
        return false;
    }

    // A script may change a shared external resource (a .mres, a material file): the copy shares them through the
    // loader cache. They are listed on Stop (restoring them needs a reload in place, which does not exist yet).
    private void WatchExternalResources(EditedScene scene)
    {
        _changed.Clear();
        Collect(scene.Root);

        void Collect(Node node)
        {
            Scan(node);
            foreach (var child in node.Children)
                Collect(child);
        }

        void Scan(object target)
        {
            if (TypeRegistry.GetNearest(target.GetType()) is not { } info)
                return;
            foreach (var property in info.Properties)
            {
                if (property.GetValue(target) is not Resource resource)
                    continue;
                if (resource.IsExternal)
                {
                    if (_watched.Add(resource))
                    {
                        void Handler() => _changed.Add(resource);
                        _watchHandlers[resource] = Handler;
                        resource.Changed += Handler;
                    }
                }
                else
                {
                    Scan(resource);
                }
            }
        }
    }

    private void ReportChangedResources()
    {
        foreach (var (resource, handler) in _watchHandlers)
            resource.Changed -= handler;
        _watchHandlers.Clear();
        _watched.Clear();
        if (_changed.Count == 0)
            return;
        var names = string.Join(", ", _changed.Select(r => r.ResourcePath ?? r.GetType().Name));
        Log.Warning($"[Editor] The simulation changed shared resources, which stay changed in the editor: {names}. Reopen them to restore.");
        _changed.Clear();
    }
}
