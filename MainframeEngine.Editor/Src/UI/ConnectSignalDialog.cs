using MainframeEngine.Serialization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The connect-signal dialog (Godot's "Connect a Signal to a Method"): the edited scene's nodes on the left, the chosen
/// node's compatible methods on the right, the method name (pre-filled with the Godot-style suggestion), deferred and
/// one-shot flags. Connect commits a <see cref="ConnectSignalAction"/> to the scene's history.
/// </summary>
public sealed class ConnectSignalDialog : EditorDocument
{
    private sealed class NodeRow
    {
        public required Node Node { get; init; }
        public required int Index { get; init; }
        public required string Label { get; init; }
        public required string Icon { get; init; }
        public required string Indent { get; init; }
        public required int Count { get; init; }
        public bool Selected { get; set; }
    }

    private sealed class MethodRow
    {
        public required SignalHandlerChoice Choice { get; init; }
        public required int Index { get; init; }
        public bool Selected { get; set; }
    }

    private static readonly RmlStructType<NodeRow> NodeType = new RmlStructType<NodeRow>()
        .Member("label", static r => r.Label)
        .Member("icon", static r => r.Icon)
        .Member("indent", static r => r.Indent)
        .Member("index", static r => r.Index)
        .Member("count", static r => r.Count)
        .Member("css", static r => r.Selected ? "list-item selected" : "list-item");

    private static readonly RmlStructType<MethodRow> MethodType = new RmlStructType<MethodRow>()
        .Member("label", static r => r.Choice.Signature)
        .Member("index", static r => r.Index)
        .Member("css", static r => r.Selected ? "list-item selected" : "list-item");

    private readonly List<NodeRow> _nodes = [];
    private readonly List<MethodRow> _methods = [];
    private RmlDataModel? _model;
    private EditedScene? _scene;
    private Node? _source;
    private SignalInfo? _signal;
    private int _selectedNode = -1;
    private string _title = "";
    private string _method = "";
    private string _error = "";
    private string _hint = "";
    private bool _deferred;
    private bool _oneShot;

    public ConnectSignalDialog(EditorWorkspace workspace)
        : base(workspace, "connect_signal.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The node the signal will call (selected on the left), or null.</summary>
    public Node? TargetNode => (uint)_selectedNode < (uint)_nodes.Count ? _nodes[_selectedNode].Node : null;

    /// <summary>The method name that Connect will use.</summary>
    public string MethodName
    {
        get => _method;
        set
        {
            _method = value ?? "";
            _model?.Dirty("method");
        }
    }

    /// <summary>The compatible methods of <see cref="TargetNode"/> (labels), for tests and QA scripts.</summary>
    public IEnumerable<string> MethodLabels => _methods.Select(m => m.Choice.Name);

    public bool Deferred
    {
        get => _deferred;
        set
        {
            _deferred = value;
            _model?.Dirty("deferred");
        }
    }

    public bool OneShot
    {
        get => _oneShot;
        set
        {
            _oneShot = value;
            _model?.Dirty("one_shot");
        }
    }

    /// <summary>The error line (why Connect failed), empty when none.</summary>
    public string Error => _error;

    protected override void OnReady()
    {
        _model = CreateDataModel("connect_signal")
            .Bind("title", this, static d => d._title)
            .Bind("error", this, static d => d._error)
            .Bind("hint", this, static d => d._hint)
            .Bind("method", this, static d => d._method, static (d, v) => d._method = v ?? "")
            .Bind("deferred", this, static d => d._deferred, static (d, v) => d._deferred = v)
            .Bind("one_shot", this, static d => d._oneShot, static (d, v) => d._oneShot = v)
            .BindList("nodes", _nodes, NodeType)
            .BindList("methods", _methods, MethodType)
            .Event("pick_node", e => SelectNode(e.GetArgument(0).GetInt32()))
            .Event("pick_method", e => SelectMethod(e.GetArgument(0).GetInt32()))
            .Event("activate_method", e =>
            {
                SelectMethod(e.GetArgument(0).GetInt32());
                Accept();
            })
            .Event("ok", _ => Accept())
            .Event("cancel", _ => Cancel());
    }

    /// <summary>Opens the dialog for <paramref name="source"/>'s <paramref name="signal"/> in <paramref name="scene"/>.</summary>
    public void Show(EditedScene scene, Node source, SignalInfo signal)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _signal = signal ?? throw new ArgumentNullException(nameof(signal));
        _title = $"Connect {source.Name}.{SignalModel.Signature(signal.Name, signal.ParameterTypes)}";
        _error = "";
        _deferred = false;
        _oneShot = false;
        _method = SignalModel.SuggestedMethodName(source, signal);
        _nodes.Clear();
        AddNodes(scene, scene.Root, 0);
        _selectedNode = -1;
        // Godot's default target: the scene root (where game scripts usually live).
        SelectNode(0);
        Visible = true;
        _model?.DirtyAll();
    }

    private void AddNodes(EditedScene scene, Node node, int depth)
    {
        var count = _signal is null ? 0 : SignalModel.CompatibleMethods(node.GetType(), _signal).Count;
        _nodes.Add(new NodeRow
        {
            Node = node,
            Index = _nodes.Count,
            Label = node.Name,
            Icon = "icon icon-sm icon-" + EditorIcons.For(node.GetType()),
            Indent = RmlText.Dp(depth * 14),
            Count = count,
        });
        // Inner nodes of instanced sub-scenes are not part of this scene's file: they cannot hold its connections.
        if (depth > 0 && node.SceneFilePath is not null)
            return;
        foreach (var child in node.Children)
            if (scene.IsEditable(child))
                AddNodes(scene, child, depth + 1);
    }

    /// <summary>Selects target row <paramref name="index"/> and lists its compatible methods.</summary>
    public void SelectNode(int index)
    {
        if ((uint)index >= (uint)_nodes.Count || _signal is null)
            return;
        if ((uint)_selectedNode < (uint)_nodes.Count)
            _nodes[_selectedNode].Selected = false;
        _selectedNode = index;
        _nodes[index].Selected = true;
        _methods.Clear();
        foreach (var choice in SignalModel.CompatibleMethods(_nodes[index].Node.GetType(), _signal))
            _methods.Add(new MethodRow { Choice = choice, Index = _methods.Count, Selected = string.Equals(choice.Name, _method, StringComparison.Ordinal) });
        _hint = _methods.Count == 0
            ? $"{_nodes[index].Node.Name} has no public method taking ({string.Join(", ", _signal.ParameterTypes.Select(t => t.Name))}). Add one to its C# class, build, and try again."
            : $"{_methods.Count} compatible method{(_methods.Count == 1 ? "" : "s")} on {_nodes[index].Node.Name}.";
        _error = "";
        _model?.DirtyAll();
    }

    /// <summary>Selects target <paramref name="node"/> (QA scripts, tests).</summary>
    public bool SelectNode(Node node)
    {
        var index = _nodes.FindIndex(r => ReferenceEquals(r.Node, node));
        SelectNode(index);
        return index >= 0;
    }

    public void SelectMethod(int index)
    {
        if ((uint)index >= (uint)_methods.Count)
            return;
        foreach (var m in _methods)
            m.Selected = false;
        _methods[index].Selected = true;
        _method = _methods[index].Choice.Name;
        _error = "";
        _model?.DirtyAll();
    }

    /// <summary>Connects (through the scene's history) and closes; keeps the dialog open with the reason on failure.</summary>
    public bool Accept()
    {
        if (_scene is not { } scene || _source is not { } source || _signal is not { } signal)
            return false;
        if (TargetNode is not { } target)
        {
            ShowError("Choose the node to connect to.");
            return false;
        }

        var method = _method.Trim();
        if (method.Length == 0)
        {
            ShowError("Enter the method to call.");
            return false;
        }

        var flags = (_deferred ? ConnectFlags.Deferred : ConnectFlags.None) | (_oneShot ? ConnectFlags.OneShot : ConnectFlags.None);
        try
        {
            scene.ConnectSignal(source, signal.Name, target, method, flags);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            ShowError(e.Message);
            return false;
        }

        Close();
        return true;
    }

    private void ShowError(string message)
    {
        _error = message;
        _model?.Dirty("error");
    }

    public void Cancel() => Close();

    private void Close()
    {
        HideAndReleaseFocus();
        _scene = null;
        _source = null;
        _signal = null;
        _nodes.Clear();
        _methods.Clear();
        _model?.DirtyAll();
    }

    protected override void OnAttach(RmlDocument document) => document.AsElement().AddEventListener("keydown", OnKeyDown);

    private void OnKeyDown(RmlEvent e)
    {
        if (!Visible)
            return;
        switch ((RmlKey)e.GetParameter("key_identifier", 0))
        {
            case RmlKey.Escape:
                Cancel();
                break;
            case RmlKey.Return or RmlKey.NumpadEnter:
                Accept();
                break;
        }
    }
}
