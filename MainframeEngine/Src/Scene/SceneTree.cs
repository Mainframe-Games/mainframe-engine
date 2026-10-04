namespace MainframeEngine;

/// <summary>
/// Owns the root <see cref="SceneViewport"/> and runs the frame for every node inside it (Godot's
/// <c>SceneTree</c>). <see cref="Engine"/> owns one and calls <see cref="Tick"/> once per frame; tests and tools
/// can create their own.
/// </summary>
/// <remarks>
/// <para>One <see cref="Tick"/>:</para>
/// <list type="number">
/// <item>Fixed physics steps: <c>accumulator += min(dt, <see cref="MaxFrameDelta"/>)</c>, then up to
/// <see cref="MaxPhysicsStepsPerFrame"/> steps of <c>1 / <see cref="PhysicsTicksPerSecond"/></c>, each calling
/// <see cref="Node.OnPhysicsProcess"/> (priority, then tree order) and the fixed-step servers. The leftover
/// fraction is <see cref="PhysicsInterpolationFraction"/>.</item>
/// <item><see cref="Node.OnProcess"/> (priority, then tree order) and the tree's timers.</item>
/// <item>Deferred calls, then <see cref="Node.QueueFree"/> deletions.</item>
/// <item>Transform notifications for 2D/3D nodes whose global transform changed (server sync).</item>
/// <item>Per-frame servers.</item>
/// </list>
/// <para>Steady-state ticks do not allocate. Not thread-safe: use from one thread.</para>
/// </remarks>
public sealed partial class SceneTree
{
    private readonly Dictionary<NodeId, Node> _nodesById = [];
    private readonly NodeList _process = new(NodeListKind.Process);
    private readonly NodeList _physics = new(NodeListKind.Physics);
    private readonly NodeList _input = new(NodeListKind.Input);
    private readonly NodeList _unhandledInput = new(NodeListKind.UnhandledInput);
    private readonly Dictionary<string, SceneGroup> _groups = new(StringComparer.Ordinal);
    private readonly List<DeferredCall> _deferred = [];
    private readonly List<Node> _deleteQueue = [];
    private readonly List<ITransformNotifiable> _transformQueue = [];
    private readonly List<SceneTreeTimer> _timers = [];
    private readonly Stack<Node> _orderStack = new();
    private readonly List<Node[]> _callGroupBuffers = [];
    private int _callGroupDepth;

    private int _structureVersion;
    private int _treeOrderVersion = -1;
    private double _accumulator;
    private bool _inTick;
    private bool _shutDown;
    private volatile int _threadId = Environment.CurrentManagedThreadId; // the thread that last ran (or created) this tree
    private volatile bool _localeChangePending;

    public SceneTree(ServerRegistry? servers = null)
    {
        Servers = servers ?? new ServerRegistry();
        Input = new InputState(this);
        Root = new SceneViewport(isTreeRoot: true) { Name = "root" };
        Root.PropagateEnterTree(this, null, 0);
        Root.PropagateReady();
        Localization.Tr.Track(this); // M9: re-translate nodes when the locale changes
    }

    /// <summary>The root viewport (<c>/root</c>); holds the default <see cref="World3D"/> and <see cref="World2D"/>.</summary>
    public SceneViewport Root { get; }

    /// <summary>Engine servers (rendering now; physics, audio and UI in later milestones).</summary>
    public ServerRegistry Servers { get; }

    /// <summary>
    /// Polled input and <see cref="InputMap"/> actions for this tree, fed by <see cref="PushInput"/> (the engine's tree
    /// is also <see cref="MainframeEngine.Input.Current"/>).
    /// </summary>
    public InputState Input { get; }

    /// <summary>True while <see cref="Node.OnPhysicsProcess"/> callbacks and the fixed-step servers run.</summary>
    public bool IsInPhysicsStep { get; private set; }

    /// <summary>The scene set with <see cref="ChangeScene(Node)"/>, a child of <see cref="Root"/>.</summary>
    public Node? CurrentScene { get; private set; }

    /// <summary>Pauses <see cref="ProcessMode.Pausable"/> nodes and the fixed-step servers (physics).</summary>
    public bool Paused { get; set; }

    /// <summary>Fixed physics rate (default 60 Hz).</summary>
    public int PhysicsTicksPerSecond
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 60;

    /// <summary>At most this many physics steps run per frame; the rest of the backlog is dropped.</summary>
    public int MaxPhysicsStepsPerFrame
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 5;

    /// <summary>Frame deltas are clamped to this (seconds) before feeding the physics accumulator.</summary>
    public float MaxFrameDelta { get; set; } = 0.25f;

    /// <summary>
    /// How far (0..1) the current frame is between the last physics step and the next, for interpolating
    /// physics-driven visuals.
    /// </summary>
    public float PhysicsInterpolationFraction { get; private set; }

    /// <summary>Delta time (seconds) of the frame being (or last) processed by <see cref="Tick"/>.</summary>
    public float ProcessDeltaTime { get; private set; }

    /// <summary>Frames processed by <see cref="Tick"/>.</summary>
    public ulong ProcessFrames { get; private set; }

    /// <summary>Fixed physics steps run so far.</summary>
    public ulong PhysicsFrames { get; private set; }

    /// <summary>Nodes currently inside the tree (including the root).</summary>
    public int NodeCount => _nodesById.Count;

    /// <summary>True while <see cref="Tick"/> runs.</summary>
    public bool IsTicking => _inTick;

    /// <summary>Raised at the start of each frame's process step.</summary>
    public event Action? ProcessFrame;

    /// <summary>Raised at the start of each fixed physics step.</summary>
    public event Action? PhysicsFrame;

    /// <summary>Raised after a node entered the tree (before its <see cref="Node.OnEnterTree"/>).</summary>
    public event Action<Node>? NodeAdded;

    /// <summary>Raised when a node leaves the tree (after its <see cref="Node.OnExitTree"/>).</summary>
    public event Action<Node>? NodeRemoved;

    /// <summary>
    /// Raised after <see cref="Localization.Tr.SetLocale"/>, once every node in the tree has run
    /// <see cref="Node.OnLocaleChanged"/> (at the end of the frame when the locale changed during <see cref="Tick"/>).
    /// </summary>
    public event Action? LocaleChanged;

    /// <summary>The node with <paramref name="id"/> if it is inside this tree.</summary>
    public Node? Find(NodeId id) => _nodesById.GetValueOrDefault(id);

    // ------------------------------------------------------------------------------------------------
    // Scenes
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Replaces <see cref="CurrentScene"/> with <paramref name="scene"/> (added under <see cref="Root"/>). The
    /// old scene is freed. During a <see cref="Tick"/> the change is deferred to the end of the frame.
    /// </summary>
    public void ChangeScene(Node scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        if (_inTick)
            CallDeferred(static state =>
            {
                var (tree, node) = ((SceneTree, Node))state!;
                tree.ChangeSceneNow(node);
            }, (this, scene));
        else
            ChangeSceneNow(scene);
    }

    /// <summary>Instantiates <paramref name="scene"/> and makes it the current scene.</summary>
    public void ChangeScene(PackedScene scene)
    {
        ArgumentNullException.ThrowIfNull(scene);
        ChangeScene(scene.Instantiate());
    }

    /// <summary>
    /// Loads a scene by path or UID (<see cref="ResourceLoader"/>) and makes it current. The tree holds the
    /// loader reference (and so the scene's external resources) until the scene is replaced or unloaded.
    /// </summary>
    public void ChangeSceneToFile(string pathOrUid)
    {
        var scene = ResourceLoader.Load<PackedScene>(pathOrUid);
        Node root;
        try
        {
            root = scene.Instantiate();
        }
        catch
        {
            scene.Release();
            throw;
        }

        ChangeScene(root);
        _currentSceneResources.Add((root, scene));
    }

    /// <summary>Frees the current scene.</summary>
    public void UnloadCurrentScene()
    {
        CurrentScene?.Free();
        CurrentScene = null;
        ReleaseSceneResources();
    }

    // Loader references taken by ChangeSceneToFile, released once their scene root is gone.
    private readonly List<(Node Root, PackedScene Scene)> _currentSceneResources = [];

    private void ReleaseSceneResources()
    {
        for (var i = _currentSceneResources.Count - 1; i >= 0; i--)
        {
            var (root, scene) = _currentSceneResources[i];
            if (!root.IsFreed)
                continue;
            _currentSceneResources.RemoveAt(i);
            if (scene.ReferenceCount > 0)
                scene.Release();
        }
    }

    private void ChangeSceneNow(Node scene)
    {
        UnloadCurrentScene();
        Root.AddChild(scene);
        CurrentScene = scene;
    }

    // ------------------------------------------------------------------------------------------------
    // Frame
    // ------------------------------------------------------------------------------------------------

    /// <summary>Runs one frame: physics steps, process, deferred calls and frees, transform sync, servers.</summary>
    public void Tick(in GameTime gameTime)
    {
        ObjectDisposedException.ThrowIf(_shutDown, this);
        if (_inTick)
            throw new InvalidOperationException("SceneTree.Tick is not re-entrant.");

        // M9: a locale change made on another thread is applied here, on the tree's own thread.
        _threadId = Environment.CurrentManagedThreadId;
        if (_localeChangePending)
        {
            _localeChangePending = false;
            PropagateLocaleChanged();
        }

        _inTick = true;
        ProcessDeltaTime = gameTime.DeltaTime;
        try
        {
            var step = 1.0 / PhysicsTicksPerSecond;
            _accumulator += Math.Clamp(gameTime.DeltaTime, 0f, MaxFrameDelta);

            // A tolerance so a frame delta equal to the step (fixed-delta runs) always steps exactly once.
            var tolerance = step * 1e-6;
            var steps = 0;
            var paused = Paused;
            if (!paused && _accumulator + tolerance >= step)
                foreach (var server in Servers.FixedStepServers)
                    server.BeforeFixedSteps();

            while (_accumulator + tolerance >= step && steps < MaxPhysicsStepsPerFrame)
            {
                RunPhysicsStep((float)step);
                _accumulator = Math.Max(0, _accumulator - step);
                steps++;
            }

            if (_accumulator + tolerance >= step)
                _accumulator = 0; // spiral-of-death guard: drop the backlog
            PhysicsInterpolationFraction = (float)Math.Clamp(_accumulator / step, 0, 1);
            if (!paused)
                foreach (var server in Servers.FixedStepServers)
                    server.AfterFixedSteps(PhysicsInterpolationFraction);

            RunProcess(gameTime);
            FlushDeferred();
            FlushTransformNotifications();

            foreach (var server in Servers.FrameServers)
                server.Process(gameTime);

            ProcessFrames++;
        }
        finally
        {
            _inTick = false;
        }
    }

    private void RunPhysicsStep(float delta)
    {
        IsInPhysicsStep = true;
        try
        {
            RunPhysicsStepCore(delta);
        }
        finally
        {
            IsInPhysicsStep = false;
        }
    }

    private void RunPhysicsStepCore(float delta)
    {
        PhysicsFrame?.Invoke();

        var paused = Paused;
        foreach (var node in _physics.Snapshot(this))
        {
            if (ReferenceEquals(node.Tree, this) && !node.IsQueuedForDeletion && node.WantsPhysicsProcess && node.CanProcess(paused))
                node.InvokePhysicsProcess(delta);
        }

        UpdateTimers(delta, physics: true);

        if (!paused)
            foreach (var server in Servers.FixedStepServers)
                server.FixedStep(delta);

        PhysicsFrames++;
    }

    private void RunProcess(in GameTime gameTime)
    {
        ProcessFrame?.Invoke();

        var paused = Paused;
        foreach (var node in _process.Snapshot(this))
        {
            if (ReferenceEquals(node.Tree, this) && !node.IsQueuedForDeletion && node.WantsProcess && node.CanProcess(paused))
                node.InvokeProcess(gameTime);
        }

        UpdateTimers(gameTime.DeltaTime, physics: false);
    }

    /// <summary>
    /// Routes an input event: the <see cref="IInputServer"/>s first (the game UI — a consumed event stops there),
    /// then <see cref="Node.OnInput"/> then <see cref="Node.OnUnhandledInput"/>, each in reverse tree order,
    /// stopping once <see cref="SceneViewport.SetInputAsHandled"/> is called. Nodes that cannot process (pause) are
    /// skipped. Returns true if the event was handled.
    /// </summary>
    public bool PushInput(InputEvent inputEvent)
    {
        ArgumentNullException.ThrowIfNull(inputEvent);
        // Polled state sees every event, even ones the UI consumes below, so a released key never sticks.
        Input.ProcessEvent(inputEvent);
        var paused = Paused;
        Root.BeginInput();

        foreach (var server in Servers.InputServers)
        {
            if (server.HandleInput(inputEvent))
            {
                Root.SetInputAsHandled();
                return true;
            }
        }

        var nodes = _input.Snapshot(this);
        for (var i = nodes.Length - 1; i >= 0 && !Root.IsInputHandled; i--)
        {
            var node = nodes[i];
            if (ReferenceEquals(node.Tree, this) && node.WantsInput && node.CanProcess(paused))
                node.InvokeInput(inputEvent);
        }

        nodes = _unhandledInput.Snapshot(this);
        for (var i = nodes.Length - 1; i >= 0 && !Root.IsInputHandled; i--)
        {
            var node = nodes[i];
            if (ReferenceEquals(node.Tree, this) && node.WantsUnhandledInput && node.CanProcess(paused))
                node.InvokeUnhandledInput(inputEvent);
        }

        return Root.IsInputHandled;
    }

    // ------------------------------------------------------------------------------------------------
    // Deferred calls and deletion
    // ------------------------------------------------------------------------------------------------

    private readonly record struct DeferredCall(Action? Action, Action<object?>? StateAction, object? State)
    {
        public void Invoke()
        {
            if (Action is not null)
                Action();
            else
                StateAction!(State);
        }
    }

    /// <summary>Queues <paramref name="action"/> for the end of the current (or next) frame.</summary>
    public void CallDeferred(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _deferred.Add(new DeferredCall(action, null, null));
    }

    /// <summary>Allocation-free form of <see cref="CallDeferred(Action)"/> for static lambdas.</summary>
    public void CallDeferred(Action<object?> action, object? state)
    {
        ArgumentNullException.ThrowIfNull(action);
        _deferred.Add(new DeferredCall(null, action, state));
    }

    internal void QueueDelete(Node node) => _deleteQueue.Add(node);

    /// <summary>
    /// Runs queued deferred calls (including ones queued while flushing), then frees queued nodes; repeats
    /// until both queues are empty. <see cref="Tick"/> calls this after process.
    /// </summary>
    public void FlushDeferred()
    {
        const int maxCallsPerFlush = 1_000_000;
        var calls = 0;
        while (_deferred.Count > 0 || _deleteQueue.Count > 0)
        {
            // A call (or free) that throws is dropped with the ones before it, so the exception surfaces
            // once instead of every following frame re-running what already ran.
            var i = 0;
            try
            {
                for (; i < _deferred.Count; i++)
                {
                    if (++calls > maxCallsPerFlush)
                    {
                        i = _deferred.Count - 1;
                        throw new InvalidOperationException("Deferred calls keep re-queueing themselves.");
                    }

                    _deferred[i].Invoke();
                }
            }
            finally
            {
                _deferred.RemoveRange(0, Math.Min(i + 1, _deferred.Count));
            }

            i = 0;
            try
            {
                for (; i < _deleteQueue.Count; i++)
                {
                    var node = _deleteQueue[i];
                    if (!node.IsFreed)
                        node.Free();
                }
            }
            finally
            {
                _deleteQueue.RemoveRange(0, Math.Min(i + 1, _deleteQueue.Count));
            }
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Groups
    // ------------------------------------------------------------------------------------------------

    private sealed class SceneGroup
    {
        public readonly List<Node> Nodes = [];
        public int SortedVersion = -1;
        public bool Dirty;
    }

    /// <summary>Nodes in <paramref name="group"/>, in tree order. The list is live: copy it before modifying the tree.</summary>
    public IReadOnlyList<Node> GetNodesInGroup(string group)
    {
        if (!_groups.TryGetValue(group, out var g))
            return [];
        SortGroup(g);
        return g.Nodes;
    }

    public bool HasGroup(string group) => _groups.TryGetValue(group, out var g) && g.Nodes.Count > 0;

    /// <summary>The first node in <paramref name="group"/> (tree order), or null.</summary>
    public Node? GetFirstNodeInGroup(string group)
    {
        var nodes = GetNodesInGroup(group);
        return nodes.Count > 0 ? nodes[0] : null;
    }

    /// <summary>Calls <paramref name="action"/> on every node in <paramref name="group"/> (tree order); safe if the action changes the tree.</summary>
    public void CallGroup(string group, Action<Node> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        CallGroup(group, action, static (node, a) => a(node));
    }

    /// <summary>Allocation-free <see cref="CallGroup(string, Action{Node})"/> with explicit state for static lambdas.</summary>
    public void CallGroup<TState>(string group, TState state, Action<Node, TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!_groups.TryGetValue(group, out var g) || g.Nodes.Count == 0)
            return;
        SortGroup(g);

        // Snapshot into a buffer per nesting depth (a callback may call CallGroup again), reused across frames.
        var count = g.Nodes.Count;
        var depth = _callGroupDepth++;
        if (depth == _callGroupBuffers.Count)
            _callGroupBuffers.Add([]);
        var buffer = _callGroupBuffers[depth];
        if (buffer.Length < count)
            _callGroupBuffers[depth] = buffer = new Node[Math.Max(count, buffer.Length * 2)];
        try
        {
            g.Nodes.CopyTo(buffer);
            for (var i = 0; i < count; i++)
            {
                var node = buffer[i];
                if (ReferenceEquals(node.Tree, this) && node.IsInGroup(group))
                    action(node, state);
            }
        }
        finally
        {
            Array.Clear(buffer, 0, count);
            _callGroupDepth--;
        }
    }

    internal void AddToGroup(string group, Node node)
    {
        if (!_groups.TryGetValue(group, out var g))
            _groups[group] = g = new SceneGroup();
        g.Nodes.Add(node);
        g.Dirty = true;
    }

    internal void RemoveFromGroup(string group, Node node)
    {
        if (!_groups.TryGetValue(group, out var g))
            return;
        // Nodes usually leave in reverse order (bottom-up exit), so search from the end.
        var index = g.Nodes.LastIndexOf(node);
        if (index >= 0)
            g.Nodes.RemoveAt(index);
    }

    private void SortGroup(SceneGroup group)
    {
        if (!group.Dirty && group.SortedVersion == _structureVersion)
            return;
        EnsureTreeOrder();
        group.Nodes.Sort(TreeOrderComparer.Instance);
        group.Dirty = false;
        group.SortedVersion = _structureVersion;
    }

    // ------------------------------------------------------------------------------------------------
    // Timers
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A one-shot timer owned by the tree (Godot's <c>create_timer</c>): <see cref="SceneTreeTimer.Timeout"/>
    /// fires after <paramref name="seconds"/>. Paused with the tree unless <paramref name="processAlways"/>.
    /// </summary>
    public SceneTreeTimer CreateTimer(float seconds, bool processAlways = true, bool processInPhysics = false)
    {
        var timer = new SceneTreeTimer(seconds, processAlways, processInPhysics);
        _timers.Add(timer);
        return timer;
    }

    private void UpdateTimers(float delta, bool physics)
    {
        for (var i = _timers.Count - 1; i >= 0; i--)
        {
            if (i >= _timers.Count)
                continue;
            var timer = _timers[i];
            if (timer.ProcessInPhysics != physics || (Paused && !timer.ProcessAlways))
                continue;
            timer.TimeLeft -= delta;
            if (timer.TimeLeft > 0)
                continue;
            _timers.RemoveAt(i);
            timer.TimeLeft = 0;
            timer.RaiseTimeout();
        }
    }

    // ------------------------------------------------------------------------------------------------
    // Node bookkeeping
    // ------------------------------------------------------------------------------------------------

    internal void OnNodeEntered(Node node)
    {
        if (!_nodesById.TryAdd(node.Id, node))
            throw new InvalidOperationException($"{node.Id} is already inside the tree.");

        UpdateCallbackLists(node);
        foreach (var group in node.Groups)
            AddToGroup(group.Name, node);
        if (node is ITransformNotifiable notifiable)
            notifiable.OnEnteredTree();
        MarkStructureChanged();
        NodeAdded?.Invoke(node);
    }

    internal void OnNodeExited(Node node)
    {
        _nodesById.Remove(node.Id);
        _process.Remove(node);
        _physics.Remove(node);
        _input.Remove(node);
        _unhandledInput.Remove(node);
        foreach (var group in node.Groups)
            RemoveFromGroup(group.Name, node);
        if (ReferenceEquals(node, CurrentScene))
            CurrentScene = null;
        MarkStructureChanged();
        NodeRemoved?.Invoke(node);
    }

    internal void OnNodeCallbacksChanged(Node node) => UpdateCallbackLists(node);

    private void UpdateCallbackLists(Node node)
    {
        _process.Set(node, node.WantsProcess);
        _physics.Set(node, node.WantsPhysicsProcess);
        _input.Set(node, node.WantsInput);
        _unhandledInput.Set(node, node.WantsUnhandledInput);
    }

    internal void QueueTransformNotification(ITransformNotifiable node) => _transformQueue.Add(node);

    private void FlushTransformNotifications()
    {
        // Index loop: a notification may move other nodes and queue more.
        for (var i = 0; i < _transformQueue.Count; i++)
            _transformQueue[i].FlushTransformNotification();
        _transformQueue.Clear();
    }

    internal void MarkStructureChanged() => _structureVersion++;

    internal void MarkProcessOrderDirty()
    {
        _process.MarkDirty();
        _physics.MarkDirty();
    }

    /// <summary>Assigns <see cref="Node.TreeOrder"/> by an iterative depth-first walk when the tree changed.</summary>
    private void EnsureTreeOrder()
    {
        if (_treeOrderVersion == _structureVersion)
            return;

        long order = 0;
        _orderStack.Clear();
        _orderStack.Push(Root);
        while (_orderStack.Count > 0)
        {
            var node = _orderStack.Pop();
            node.TreeOrder = order++;
            var children = node.ChildList;
            if (children is null)
                continue;
            for (var i = children.Count - 1; i >= 0; i--)
                _orderStack.Push(children[i]);
        }

        _treeOrderVersion = _structureVersion;
    }

    /// <summary>
    /// Frees every node (scene first, then the root) so servers and GPU resources are released. The tree
    /// cannot be used afterwards. Called by <see cref="Engine"/> on close.
    /// </summary>
    public void Shutdown()
    {
        if (_shutDown)
            return;

        CurrentScene = null;
        while (Root.ChildCount > 0)
            Root.GetChild(Root.ChildCount - 1).Free();
        FlushDeferred();
        ReleaseSceneResources();
        Root.ExitTreeAsRoot();
        Root.Free();
        _timers.Clear();
        _transformQueue.Clear();
        _shutDown = true;
        Localization.Tr.Untrack(this);
    }

    /// <summary>
    /// The locale changed (<see cref="Localization.Tr"/>): runs <see cref="Node.OnLocaleChanged"/> on every node, parents
    /// first, then <see cref="LocaleChanged"/>. Deferred to the end of the frame while ticking, so nodes never re-translate
    /// in the middle of process callbacks, and to the next <see cref="Tick"/> when the locale changed on another thread
    /// than the one that created the tree.
    /// </summary>
    internal void OnLocaleChanged()
    {
        if (_shutDown)
            return;
        if (Environment.CurrentManagedThreadId != _threadId)
        {
            _localeChangePending = true;
            return;
        }

        if (_inTick)
        {
            CallDeferred(static state => ((SceneTree)state!).PropagateLocaleChanged(), this);
            return;
        }

        PropagateLocaleChanged();
    }

    private void PropagateLocaleChanged()
    {
        if (_shutDown)
            return;
        Root.PropagateLocaleChanged();
        LocaleChanged?.Invoke();
    }

    private sealed class TreeOrderComparer : IComparer<Node>
    {
        public static readonly TreeOrderComparer Instance = new();

        public int Compare(Node? x, Node? y) => x!.TreeOrder.CompareTo(y!.TreeOrder);
    }

    private sealed class PriorityComparer : IComparer<Node>
    {
        public static readonly PriorityComparer Instance = new();

        public int Compare(Node? x, Node? y)
        {
            var byPriority = x!.ProcessPriority.CompareTo(y!.ProcessPriority);
            return byPriority != 0 ? byPriority : x.TreeOrder.CompareTo(y.TreeOrder);
        }
    }

    private enum NodeListKind
    {
        Process,
        Physics,
        Input,
        UnhandledInput,
    }

    /// <summary>
    /// Nodes with one callback enabled: swap-remove membership (each node stores its index), sorted lazily
    /// when membership, priorities or the tree changed, iterated through a reused snapshot so callbacks can
    /// change the tree.
    /// </summary>
    private sealed class NodeList(NodeListKind kind)
    {
        private readonly List<Node> _nodes = [];
        private Node[] _snapshot = [];
        private bool _dirty;
        private int _sortedVersion = -1;

        public void Set(Node node, bool member)
        {
            if (member)
                Add(node);
            else
                Remove(node);
        }

        public void MarkDirty() => _dirty = true;

        private void Add(Node node)
        {
            ref var index = ref IndexOf(node);
            if (index >= 0)
                return;
            index = _nodes.Count;
            _nodes.Add(node);
            _dirty = true;
        }

        public void Remove(Node node)
        {
            ref var index = ref IndexOf(node);
            if (index < 0)
                return;

            var last = _nodes.Count - 1;
            if (index != last)
            {
                var moved = _nodes[last];
                _nodes[index] = moved;
                IndexOf(moved) = index;
            }

            _nodes.RemoveAt(last);
            index = -1;
            _dirty = true;
        }

        public ReadOnlySpan<Node> Snapshot(SceneTree tree)
        {
            if (_dirty || _sortedVersion != tree._structureVersion)
            {
                tree.EnsureTreeOrder();
                _nodes.Sort(kind is NodeListKind.Process or NodeListKind.Physics
                    ? PriorityComparer.Instance
                    : TreeOrderComparer.Instance);
                for (var i = 0; i < _nodes.Count; i++)
                    IndexOf(_nodes[i]) = i;
                _dirty = false;
                _sortedVersion = tree._structureVersion;
            }

            var count = _nodes.Count;
            if (_snapshot.Length < count)
                _snapshot = new Node[Math.Max(count, _snapshot.Length * 2)];
            _nodes.CopyTo(_snapshot);
            // Stale references past `count` are harmless (never read) but keep freed nodes alive; clear them.
            if (_snapshot.Length > count && _snapshot[count] is not null)
                Array.Clear(_snapshot, count, _snapshot.Length - count);
            return new ReadOnlySpan<Node>(_snapshot, 0, count);
        }

        private ref int IndexOf(Node node)
        {
            if (kind == NodeListKind.Process)
                return ref node.ProcessListIndex;
            if (kind == NodeListKind.Physics)
                return ref node.PhysicsListIndex;
            if (kind == NodeListKind.Input)
                return ref node.InputListIndex;
            return ref node.UnhandledInputListIndex;
        }
    }
}

/// <summary>A one-shot timer from <see cref="SceneTree.CreateTimer"/>.</summary>
public sealed class SceneTreeTimer
{
    internal SceneTreeTimer(float seconds, bool processAlways, bool processInPhysics)
    {
        TimeLeft = seconds;
        ProcessAlways = processAlways;
        ProcessInPhysics = processInPhysics;
    }

    /// <summary>Seconds until <see cref="Timeout"/>.</summary>
    public float TimeLeft { get; set; }

    public bool ProcessAlways { get; }
    public bool ProcessInPhysics { get; }

    public event Action? Timeout;

    internal void RaiseTimeout() => Timeout?.Invoke();
}

/// <summary>Implemented by 2D/3D nodes: the tree batches their transform-changed notifications (server sync).</summary>
internal interface ITransformNotifiable
{
    void OnEnteredTree();
    void FlushTransformNotification();
}
