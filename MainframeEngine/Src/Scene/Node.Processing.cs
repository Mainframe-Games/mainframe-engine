using System.Collections.Concurrent;
using System.Reflection;

namespace MainframeEngine;

/// <summary>When a node's process callbacks run relative to <see cref="SceneTree.Paused"/>.</summary>
public enum ProcessMode
{
    /// <summary>Same as the parent (the tree root is <see cref="Pausable"/>).</summary>
    Inherit,

    /// <summary>Runs while the tree is not paused.</summary>
    Pausable,

    /// <summary>Runs only while the tree is paused (pause menus).</summary>
    WhenPaused,

    /// <summary>Runs whether or not the tree is paused.</summary>
    Always,

    /// <summary>Never runs.</summary>
    Disabled,
}

public partial class Node
{
    [Flags]
    private enum Callbacks : byte
    {
        None = 0,
        Process = 1,
        PhysicsProcess = 2,
        Input = 4,
        UnhandledInput = 8,
    }

    private static readonly ConcurrentDictionary<Type, Callbacks> OverriddenCallbacks = new();

    private Callbacks _enabledCallbacks;
    private ProcessMode _processMode;
    private ProcessMode _resolvedProcessMode = ProcessMode.Pausable;
    private int _processPriority;

    // Bookkeeping owned by SceneTree.
    internal int ProcessListIndex = -1;
    internal int PhysicsListIndex = -1;
    internal int InputListIndex = -1;
    internal int UnhandledInputListIndex = -1;
    internal long TreeOrder;

    /// <summary>Whether process callbacks run while the tree is paused; see <see cref="MainframeEngine.ProcessMode"/>.</summary>
    [Export]
    public ProcessMode ProcessMode
    {
        get => _processMode;
        set
        {
            if (_processMode == value)
                return;
            _processMode = value;
            if (_tree is not null)
                PropagateProcessMode();
        }
    }

    /// <summary>Process order: lower runs first; equal priorities run in tree order.</summary>
    [Export]
    public int ProcessPriority
    {
        get => _processPriority;
        set
        {
            if (_processPriority == value)
                return;
            _processPriority = value;
            _tree?.MarkProcessOrderDirty();
        }
    }

    /// <summary>The effective mode after resolving <see cref="ProcessMode.Inherit"/>.</summary>
    public ProcessMode ResolvedProcessMode => _resolvedProcessMode;

    /// <summary>True if the node would run process callbacks this frame (inside a tree, not blocked by pause).</summary>
    public bool CanProcess() => _tree is not null && CanProcess(_tree.Paused);

    internal bool CanProcess(bool paused) => _resolvedProcessMode switch
    {
        ProcessMode.Pausable => !paused,
        ProcessMode.WhenPaused => paused,
        ProcessMode.Always => true,
        _ => false,
    };

    /// <summary>
    /// Enables or disables <see cref="OnProcess"/>. By default it is enabled exactly when the type overrides
    /// <see cref="OnProcess"/>.
    /// </summary>
    public void SetProcess(bool enable) => SetCallback(Callbacks.Process, enable);

    public bool IsProcessing => (_enabledCallbacks & Callbacks.Process) != 0;

    /// <summary>Enables or disables <see cref="OnPhysicsProcess"/> (default: enabled when overridden).</summary>
    public void SetPhysicsProcess(bool enable) => SetCallback(Callbacks.PhysicsProcess, enable);

    public bool IsPhysicsProcessing => (_enabledCallbacks & Callbacks.PhysicsProcess) != 0;

    /// <summary>Enables or disables <see cref="OnInput"/> (default: enabled when overridden).</summary>
    public void SetProcessInput(bool enable) => SetCallback(Callbacks.Input, enable);

    public bool IsProcessingInput => (_enabledCallbacks & Callbacks.Input) != 0;

    /// <summary>Enables or disables <see cref="OnUnhandledInput"/> (default: enabled when overridden).</summary>
    public void SetProcessUnhandledInput(bool enable) => SetCallback(Callbacks.UnhandledInput, enable);

    public bool IsProcessingUnhandledInput => (_enabledCallbacks & Callbacks.UnhandledInput) != 0;

    /// <summary>Called every frame (after the fixed physics steps) while the node can process.</summary>
    protected virtual void OnProcess(in GameTime gameTime)
    {
    }

    /// <summary>Called once per fixed step (<see cref="SceneTree.PhysicsTicksPerSecond"/>) while the node can process.</summary>
    protected virtual void OnPhysicsProcess(float delta)
    {
    }

    /// <summary>Input in reverse tree order (children before parents); call <see cref="SceneViewport.SetInputAsHandled"/> to stop it.</summary>
    protected virtual void OnInput(InputEvent inputEvent)
    {
    }

    /// <summary>Input nobody handled in <see cref="OnInput"/>, in reverse tree order.</summary>
    protected virtual void OnUnhandledInput(InputEvent inputEvent)
    {
    }

    internal void InvokeProcess(in GameTime gameTime) => OnProcess(gameTime);
    internal void InvokePhysicsProcess(float delta) => OnPhysicsProcess(delta);
    internal void InvokeInput(InputEvent inputEvent) => OnInput(inputEvent);
    internal void InvokeUnhandledInput(InputEvent inputEvent) => OnUnhandledInput(inputEvent);

    internal bool WantsProcess => (_enabledCallbacks & Callbacks.Process) != 0;
    internal bool WantsPhysicsProcess => (_enabledCallbacks & Callbacks.PhysicsProcess) != 0;
    internal bool WantsInput => (_enabledCallbacks & Callbacks.Input) != 0;
    internal bool WantsUnhandledInput => (_enabledCallbacks & Callbacks.UnhandledInput) != 0;

    /// <summary>
    /// Queues <paramref name="action"/> to run at the end of the frame (after process, before frees). The node
    /// must be inside a tree.
    /// </summary>
    public void CallDeferred(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        RequireTree().CallDeferred(action);
    }

    /// <summary>Allocation-free form of <see cref="CallDeferred(Action)"/> for static lambdas.</summary>
    public void CallDeferred(Action<object?> action, object? state)
    {
        ArgumentNullException.ThrowIfNull(action);
        RequireTree().CallDeferred(action, state);
    }

    private SceneTree RequireTree() =>
        _tree ?? throw new InvalidOperationException($"'{Name}' is not inside a tree; deferred calls need one.");

    private void InitializeProcessFlags()
    {
        _enabledCallbacks = OverriddenCallbacks.GetOrAdd(GetType(), static type => FindOverrides(type));
    }

    private void SetCallback(Callbacks callback, bool enable)
    {
        var before = _enabledCallbacks;
        _enabledCallbacks = enable ? before | callback : before & ~callback;
        if (before != _enabledCallbacks)
            _tree?.OnNodeCallbacksChanged(this);
    }

    private void ResolveProcessMode()
    {
        _resolvedProcessMode = _processMode != ProcessMode.Inherit
            ? _processMode
            : _parent?._resolvedProcessMode ?? ProcessMode.Pausable;
    }

    private void PropagateProcessMode()
    {
        ResolveProcessMode();
        for (var i = 0; _children is not null && i < _children.Count; i++)
        {
            var child = _children[i];
            if (child._processMode == ProcessMode.Inherit)
                child.PropagateProcessMode();
        }
    }

    // Overrides are found once per type with reflection (not a hot path; cached).
    private static Callbacks FindOverrides(Type type)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        var result = Callbacks.None;
        if (IsOverridden(type.GetMethod(nameof(OnProcess), flags, [typeof(GameTime).MakeByRefType()])))
            result |= Callbacks.Process;
        if (IsOverridden(type.GetMethod(nameof(OnPhysicsProcess), flags, [typeof(float)])))
            result |= Callbacks.PhysicsProcess;
        if (IsOverridden(type.GetMethod(nameof(OnInput), flags, [typeof(InputEvent)])))
            result |= Callbacks.Input;
        if (IsOverridden(type.GetMethod(nameof(OnUnhandledInput), flags, [typeof(InputEvent)])))
            result |= Callbacks.UnhandledInput;
        return result;

        static bool IsOverridden(MethodInfo? method) => method is not null && method.DeclaringType != typeof(Node);
    }
}
