using MainframeEngine.Serialization;

namespace MainframeEngine.Editor;

/// <summary>
/// One open scene (a tab): its node tree, file, undo history and selection, and the <see cref="SubViewport"/> its world
/// lives in (one edited world per tab). Every edit goes through <see cref="History"/> so it can be undone; the
/// operations here build the right <see cref="IEditorAction"/>s.
/// </summary>
public sealed class EditedScene : IDisposable, IInspectorContext
{
    private readonly List<PackedScene> _heldScenes = [];
    private bool _disposed;

    internal EditedScene(Node root, string? filePath, SubViewport viewport, PackedScene? source)
    {
        Root = root ?? throw new ArgumentNullException(nameof(root));
        FilePath = filePath;
        Viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        if (source is not null)
            _heldScenes.Add(source);
        History.Changed += OnHistoryChanged;
    }

    /// <summary>The scene root (its <see cref="Node.Owner"/> is null; the nodes it owns are saved).</summary>
    public Node Root { get; }

    /// <summary>Absolute path of the <c>.mscene</c>, or null for a new scene never saved.</summary>
    public string? FilePath { get; internal set; }

    /// <summary>The offscreen view the scene's world renders into (the editor viewport shows it).</summary>
    public SubViewport Viewport { get; }

    public UndoRedo History { get; } = new();

    public Selection Selection { get; } = new();

    /// <summary>The editor camera state of this tab (kept when switching tabs).</summary>
    public EditorCamera Camera { get; } = new();

    /// <summary>Unsaved changes (the tab and window titles show <c>*</c>).</summary>
    public bool IsDirty => History.IsDirty || FilePath is null && History.Position > 0;

    /// <summary>The file name, or "Untitled" for a new scene.</summary>
    public string DisplayName => FilePath is null ? $"Untitled{(UntitledNumber > 1 ? $" {UntitledNumber}" : "")}" : Path.GetFileName(FilePath);

    /// <summary>Tab title: the file name with <c>*</c> while dirty.</summary>
    public string Title => IsDirty ? DisplayName + "*" : DisplayName;

    internal int UntitledNumber { get; init; }

    /// <summary>Bumped after every change of the scene (history change); panels refresh when it differs.</summary>
    public int Version { get; private set; }

    /// <summary>Raised after the scene changed (an action was committed, undone or redone, or it was saved).</summary>
    public event Action<EditedScene>? Changed;

    private void OnHistoryChanged()
    {
        Version++;
        Selection.Prune(Root);
        Changed?.Invoke(this);
    }

    /// <summary>True for nodes the user may edit: the root and nodes it owns (instanced sub-scenes' inner nodes are not listed).</summary>
    public bool IsEditable(Node node) =>
        ReferenceEquals(node, Root) || (ReferenceEquals(node.Owner, Root) && Root.IsAncestorOf(node));

    /// <summary>
    /// The node to select for <paramref name="node"/> (a picked mesh may be inside an instanced sub-scene: Godot selects
    /// the instance root): the nearest ancestor-or-self the scene owns, or null when outside the scene.
    /// </summary>
    public Node? SelectableFor(Node? node)
    {
        for (var n = node; n is not null; n = n.Parent)
        {
            if (IsEditable(n))
                return n;
            if (ReferenceEquals(n, Root))
                break;
        }

        return null;
    }

    // ── Edits ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Sets an exported property through the history. <paramref name="mergeKey"/> merges a continuous edit (drag).</summary>
    public void SetProperty(object target, ExportPropertyInfo property, object? value, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(property);
        var old = property.GetValue(target);
        if (Equals(old, value))
            return; // nothing changed (also mid-drag: no entry, no redo branch cut, no dirty flag)
        History.Commit(new SetPropertyAction(target, property, old, value), mergeKey: mergeKey);
    }

    /// <summary>
    /// Sets <paramref name="property"/> on several objects at once (multi-selection) as ONE history entry:
    /// <paramref name="values"/>[i] goes to <paramref name="targets"/>[i]. Nothing is recorded when no value changes.
    /// Every target is part of the entry (unchanged ones too), so a continuous edit with <paramref name="mergeKey"/>
    /// keeps merging into it.
    /// </summary>
    public void SetProperties(IReadOnlyList<object> targets, ExportPropertyInfo property, IReadOnlyList<object?> values, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(property);
        ArgumentNullException.ThrowIfNull(values);
        if (targets.Count != values.Count)
            throw new ArgumentException("One value per target is needed.", nameof(values));
        if (targets.Count == 1)
        {
            SetProperty(targets[0], property, values[0], mergeKey);
            return;
        }

        var actions = new IEditorAction[targets.Count];
        var changed = false;
        for (var i = 0; i < targets.Count; i++)
        {
            var old = property.GetValue(targets[i]);
            changed |= !Equals(old, values[i]);
            actions[i] = new SetPropertyAction(targets[i], property, old, values[i]);
        }

        if (!changed)
            return;
        History.Commit(new CompositeAction($"Set {property.Name} on {targets.Count} nodes", actions), mergeKey: mergeKey);
    }

    /// <summary>Adds <paramref name="node"/> under <paramref name="parent"/> (default: the primary selection, else the root) and selects it.</summary>
    public Node AddNode(Node node, Node? parent = null, int index = -1)
    {
        ArgumentNullException.ThrowIfNull(node);
        parent ??= Selection.Primary is { } selected && IsEditable(selected) ? selected : Root;
        History.Commit(new AddNodeAction(parent, node, Root, index));
        Selection.Set(node);
        return node;
    }

    /// <summary>Instances the scene at <paramref name="path"/> (project-relative or absolute) under <paramref name="parent"/>.</summary>
    public Node InstanceScene(string path, Node? parent = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var full = AssetDatabase.Current.ToAbsolutePath(path);
        if (FilePath is not null && string.Equals(Path.GetFullPath(full), Path.GetFullPath(FilePath), StringComparison.Ordinal))
            throw new InvalidOperationException("A scene cannot instance itself.");
        var scene = ResourceLoader.Load<PackedScene>(AssetDatabase.Current.ToProjectPath(full));
        Node instance;
        try
        {
            instance = scene.Instantiate();
        }
        catch
        {
            scene.Release();
            throw;
        }

        _heldScenes.Add(scene); // the instance uses the scene's resources while it is open
        parent ??= Selection.Primary is { } selected && IsEditable(selected) ? selected : Root;
        History.Commit(new AddNodeAction(parent, instance, Root, name: $"Instance {Path.GetFileNameWithoutExtension(full)}"));
        Selection.Set(instance);
        return instance;
    }

    /// <summary>Deletes <paramref name="nodes"/> (the root is skipped; nodes inside another deleted node are covered by it).</summary>
    public bool Delete(IReadOnlyList<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var roots = TopLevel(nodes);
        if (roots.Count == 0)
            return false;
        var actions = roots.Select(n => (IEditorAction)new RemoveNodeAction(n)).ToArray();
        History.Commit(actions.Length == 1 ? actions[0] : new CompositeAction($"Delete {actions.Length} nodes", actions));
        Selection.Clear();
        return true;
    }

    /// <summary>
    /// Duplicates <paramref name="nodes"/> next to themselves (names made unique: Box → Box2) and selects the copies.
    /// Nested instances, inline resources (copied), persistent groups and connections inside the copied subtree come along.
    /// </summary>
    public IReadOnlyList<Node> Duplicate(IReadOnlyList<Node> nodes)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var roots = TopLevel(nodes);
        if (roots.Count == 0)
            return [];
        var copies = NodeDuplicator.Duplicate(Root, roots);
        var actions = new IEditorAction[roots.Count];
        for (var i = 0; i < roots.Count; i++)
        {
            var original = roots[i];
            actions[i] = new AddNodeAction(original.Parent!, copies[i], Root, name: $"Duplicate {original.Name}", after: original);
        }

        History.Commit(actions.Length == 1 ? actions[0] : new CompositeAction($"Duplicate {actions.Length} nodes", actions));
        Selection.Clear();
        foreach (var copy in copies)
            Selection.Add(copy);
        return copies;
    }

    /// <summary>Moves <paramref name="node"/> under <paramref name="newParent"/> at <paramref name="index"/> (-1: last).</summary>
    public bool Reparent(Node node, Node newParent, int index = -1, bool keepGlobalTransform = true)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(newParent);
        if (ReferenceEquals(node, Root) || !IsEditable(node) || !IsEditable(newParent) ||
            ReferenceEquals(node, newParent) || node.IsAncestorOf(newParent))
            return false;
        if (ReferenceEquals(node.Parent, newParent) && (index < 0 ? node.GetIndex() == newParent.ChildCount - 1 : index == node.GetIndex() || index == node.GetIndex() + 1))
            return false;
        History.Commit(new ReparentAction(node, newParent, index, keepGlobalTransform));
        return true;
    }

    /// <summary>Renames <paramref name="node"/>; false when the name is empty or unchanged.</summary>
    public bool Rename(Node node, string name)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(node.Name, name.Trim(), StringComparison.Ordinal))
            return false;
        History.Commit(new RenameAction(node, name.Trim()));
        return true;
    }

    /// <summary>Moves <paramref name="node"/> up (-1) or down (+1) among its siblings.</summary>
    public bool Move(Node node, int delta)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.Parent is null || !IsEditable(node))
            return false;
        var target = node.GetIndex() + delta;
        if (target < 0 || target >= node.Parent.ChildCount)
            return false;
        History.Commit(new MoveInTreeAction(node, target));
        return true;
    }

    /// <summary>
    /// Connects <paramref name="source"/>'s <paramref name="signal"/> to <paramref name="method"/> on
    /// <paramref name="target"/> (both nodes of this scene) through the history; the scene saves the connection.
    /// Throws <see cref="ArgumentException"/>/<see cref="InvalidOperationException"/> with a user-facing message when
    /// the signal or a compatible method does not exist, or the connection already exists.
    /// </summary>
    public void ConnectSignal(Node source, string signal, Node target, string method, ConnectFlags flags = ConnectFlags.None)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        if (!IsEditable(source) || !IsEditable(target))
            throw new InvalidOperationException("Both nodes must belong to the edited scene (not to an instanced sub-scene).");
        if (source.IsConnected(signal, target, method))
            throw new InvalidOperationException($"{source.Name}.{signal} is already connected to {target.Name}.{method}.");
        // Connect validates the signal and the method; the action's Do runs it.
        History.Commit(new ConnectSignalAction(source, signal, target, method, flags));
    }

    /// <summary>Removes a persisted connection through the history.</summary>
    public bool DisconnectSignal(SignalConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!connection.Source.IsConnected(connection.Signal, connection.Target, connection.Method))
            return false;
        History.Commit(new DisconnectSignalAction(connection));
        return true;
    }

    // Editable nodes of the list that are not the root and not inside another listed node.
    private List<Node> TopLevel(IReadOnlyList<Node> nodes)
    {
        var result = new List<Node>();
        foreach (var node in nodes)
        {
            if (ReferenceEquals(node, Root) || node.Parent is null || !IsEditable(node) || result.Contains(node))
                continue;
            var covered = false;
            foreach (var other in nodes)
                if (!ReferenceEquals(other, node) && other.IsAncestorOf(node) && !ReferenceEquals(other, Root))
                    covered = true;
            if (!covered)
                result.Add(node);
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        History.Changed -= OnHistoryChanged;
        History.Clear();
        if (!Viewport.IsFreed)
            Viewport.Free(); // frees the scene root with it
        if (!Root.IsFreed)
            Root.Free();
        foreach (var scene in _heldScenes)
            if (scene.ReferenceCount > 0)
                scene.Release();
        _heldScenes.Clear();
    }
}
