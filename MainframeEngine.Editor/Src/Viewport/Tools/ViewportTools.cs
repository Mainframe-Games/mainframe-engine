using System.Numerics;
using System.Reflection;

namespace MainframeEngine.Editor;

/// <summary>
/// Hosts the <see cref="IEditorViewportTool"/>s (docs/design/editor.md#viewport-tool-api): finds the tool for the active
/// 3D tab's single selected node each frame, activates and deactivates it, keeps one instance per tab and tool type until
/// the tab closes (the save hook reaches every one of them) and forwards the view's input. The viewport controller routes
/// input here after navigation and before the gizmo; the inspector shows the active tool's dock tab.
/// </summary>
public sealed class ViewportTools
{
    private readonly EditorWorkspace? _workspace;
    private readonly Dictionary<EditedScene, List<IEditorViewportTool>> _instances = [];
    private readonly Context _context;
    private int _version;

    public ViewportTools(EditorWorkspace? workspace)
    {
        _workspace = workspace;
        _context = new Context(this);
    }

    /// <summary>The active tool, or null.</summary>
    public IEditorViewportTool? Active { get; private set; }

    /// <summary>The node the active tool edits.</summary>
    public Node? ActiveNode { get; private set; }

    /// <summary>The active tool's registration (dock tab title and icon).</summary>
    public EditorViewportToolAttribute? ActiveInfo { get; private set; }

    /// <summary>The scene the active tool edits.</summary>
    public EditedScene? ActiveScene { get; private set; }

    /// <summary>Changes when the active tool changes (the dock rebuilds on it, with the tool's own <see cref="IEditorViewportTool.DockVersion"/>).</summary>
    public int Version => _version;

    /// <summary>Whether the active tool holds the mouse (a consumed left press not released yet).</summary>
    public bool Captured { get; private set; }

    /// <summary>The cursor label the active tool set (null: none).</summary>
    public string? CursorLabel { get; private set; }

    /// <summary>Changes when <see cref="CursorLabel"/> changes.</summary>
    public int CursorLabelVersion { get; private set; }

    /// <summary>The view's size in view pixels, as the controller last reported it.</summary>
    public Vector2 ViewPixels { get; set; } = new(1, 1);

    /// <summary>Every tool instance of <paramref name="scene"/>, for tests.</summary>
    public IReadOnlyList<IEditorViewportTool> InstancesOf(EditedScene scene) =>
        _instances.TryGetValue(scene, out var list) ? list : [];

    /// <summary>
    /// Per frame: activates the tool of <paramref name="scene"/>'s single selected node (none while
    /// <paramref name="enabled"/> is false: 2D tabs, simulation, other modes).
    /// </summary>
    public void Update(EditedScene? scene, bool enabled, float deltaSeconds = 0f)
    {
        Prune();
        try
        {
            UpdateActive(scene, enabled);
        }
        finally
        {
            Active?.Process(deltaSeconds);
        }
    }

    private void UpdateActive(EditedScene? scene, bool enabled)
    {
        Node? node = null;
        if (enabled && scene is not null && scene.Selection.Count == 1 && scene.Selection.Nodes[0] is { IsFreed: false } selected)
            node = selected;
        if (node is not null && ReferenceEquals(node, ActiveNode) && ReferenceEquals(scene, ActiveScene))
            return;
        Deactivate();
        if (node is null || scene is null)
            return;
        var type = FindToolType(node);
        if (type is null)
            return;
        var tool = InstanceFor(scene, type.Value.Tool);
        if (tool is null || !tool.Handles(node))
            return;
        Active = tool;
        ActiveNode = node;
        ActiveScene = scene;
        ActiveInfo = type.Value.Info;
        _version++;
        tool.Activate(_context, node);
    }

    /// <summary>Deactivates the active tool (a captured drag gets <see cref="ViewportToolInputKind.Cancel"/> first).</summary>
    public void Deactivate()
    {
        if (Active is not { } tool)
            return;
        if (Captured)
            Cancel();
        SetLabel(null);
        Active = null;
        ActiveNode = null;
        ActiveScene = null;
        ActiveInfo = null;
        _version++;
        tool.Deactivate();
    }

    /// <summary>Sends <paramref name="input"/> to the active tool (Captured is filled in here).</summary>
    public ViewportToolResult Send(ViewportToolInput input)
    {
        if (Active is not { } tool)
            return ViewportToolResult.Pass;
        input = input with { Captured = Captured };
        var result = tool.OnInput(input);
        if (input.Kind == ViewportToolInputKind.Press && input.Button == Silk.NET.Input.MouseButton.Left && result == ViewportToolResult.Consumed)
            Captured = true;
        else if (input.Kind == ViewportToolInputKind.Release && input.Button == Silk.NET.Input.MouseButton.Left && Captured)
        {
            Captured = false;
            result = ViewportToolResult.Consumed;
        }

        return result;
    }

    /// <summary>Interrupts a captured drag: the tool gets <see cref="ViewportToolInputKind.Cancel"/>.</summary>
    public void Cancel()
    {
        if (!Captured)
            return;
        Captured = false;
        Active?.OnInput(new ViewportToolInput { Kind = ViewportToolInputKind.Cancel });
    }

    /// <summary>Calls <see cref="IEditorViewportTool.SaveExternalData"/> on every tool instance of <paramref name="scene"/> (a throw fails the save).</summary>
    public void SaveExternalData(EditedScene scene)
    {
        if (_instances.TryGetValue(scene, out var list))
            foreach (var tool in list)
                tool.SaveExternalData();
    }

    /// <summary>Drops every tool instance and node reference (before a code reload: tools may be game types).</summary>
    public void ReleaseReferences()
    {
        Deactivate();
        _instances.Clear();
    }

    private void Prune()
    {
        if (_workspace is null || _instances.Count == 0)
            return;
        var scenes = _workspace.Session.Scenes;
        List<EditedScene>? closed = null;
        foreach (var scene in _instances.Keys)
            if (!scenes.Contains(scene))
                (closed ??= []).Add(scene);
        if (closed is null)
            return;
        foreach (var scene in closed)
        {
            if (ReferenceEquals(scene, ActiveScene))
                Deactivate();
            _instances.Remove(scene);
        }
    }

    private IEditorViewportTool? InstanceFor(EditedScene scene, Type type)
    {
        if (!_instances.TryGetValue(scene, out var list))
            _instances[scene] = list = [];
        foreach (var tool in list)
            if (tool.GetType() == type)
                return tool;
        IEditorViewportTool created;
        try
        {
            created = (IEditorViewportTool)Activator.CreateInstance(type)!;
        }
        catch (Exception e) when (e is TargetInvocationException or MissingMethodException or MemberAccessException)
        {
            Log.Error($"[Editor] Viewport tool {type.Name} could not be created: {e.InnerException?.Message ?? e.Message}");
            return null;
        }

        list.Add(created);
        return created;
    }

    private void SetLabel(string? text)
    {
        if (text == CursorLabel)
            return;
        CursorLabel = text;
        CursorLabelVersion++;
    }

    // ── Discovery ────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly Lock Gate = new();
    private static List<(Type Node, Type Tool, EditorViewportToolAttribute Info)>? _registered;

    /// <summary>The tool type registered for <paramref name="node"/>'s type or its nearest base type, or null.</summary>
    public static (Type Tool, EditorViewportToolAttribute Info)? FindToolType(Node node)
    {
        ArgumentNullException.ThrowIfNull(node);
        lock (Gate)
        {
            _registered ??= Scan();
            for (var t = node.GetType(); t is not null; t = t.BaseType)
                foreach (var entry in _registered)
                    if (entry.Node == t)
                        return (entry.Tool, entry.Info);
            return null;
        }
    }

    /// <summary>Forgets what was scanned (after loading or unloading a game assembly).</summary>
    public static void Reset()
    {
        lock (Gate)
            _registered = null;
    }

    private static List<(Type, Type, EditorViewportToolAttribute)> Scan()
    {
        var result = new List<(Type, Type, EditorViewportToolAttribute)>();
        foreach (var type in EditorPluginTypes.All())
            if (typeof(IEditorViewportTool).IsAssignableFrom(type) && !type.IsAbstract &&
                type.GetCustomAttribute<EditorViewportToolAttribute>() is { } attribute)
                result.Add((attribute.NodeType, type, attribute));
        return result;
    }

    private sealed class Context(ViewportTools tools) : IViewportToolContext
    {
        public EditedScene Scene => tools.ActiveScene ?? throw new InvalidOperationException("No viewport tool is active.");
        public UndoRedo History => Scene.History;
        public EditorCamera Camera => Scene.Camera;
        public Vector2 ViewPixels => tools.ViewPixels;
        public void SetCursorLabel(string? text) => tools.SetLabel(text);
    }
}
