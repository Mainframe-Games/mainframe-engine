namespace MainframeEngine.Editor;

/// <summary>
/// One undoable edit. <see cref="Do"/> applies it (also on redo), <see cref="Undo"/> reverts it; both must leave the
/// scene exactly as it was before the other ran. Actions that keep detached nodes alive (a removed subtree) implement
/// <see cref="IDisposable"/>: <see cref="UndoRedo"/> disposes an action when it leaves the history, telling it whether
/// it was applied at that moment (<see cref="IDiscardableAction"/>).
/// </summary>
public interface IEditorAction
{
    /// <summary>Shown in the Edit menu and the undo history ("Set Position", "Delete Box").</summary>
    string Name { get; }

    void Do();

    void Undo();

    /// <summary>
    /// Continuous edits (a slider or gizmo drag) merge into one history entry: called on the last action with the next
    /// one while a merge window is open (<see cref="UndoRedo.Commit"/> with a merge key). Return true after absorbing
    /// <paramref name="next"/> (this action's "do" state becomes the next one's; its "undo" state is kept).
    /// </summary>
    bool TryMerge(IEditorAction next) => false;
}

/// <summary>An action that owns something (detached nodes) to release when it leaves the history.</summary>
public interface IDiscardableAction : IEditorAction
{
    /// <summary>
    /// Called once when the action is dropped from the history (redo branch cut, history limit, clear).
    /// <paramref name="applied"/>: whether the action's effect is in the scene at that moment.
    /// </summary>
    void Discard(bool applied);
}

/// <summary>
/// The undo history of one edited scene (Godot's <c>UndoRedo</c>): a list of <see cref="IEditorAction"/>s and a
/// position. Committing cuts the redo branch; the oldest entries are dropped beyond <see cref="Limit"/>. Continuous
/// edits merge by key until <see cref="EndMerge"/> (mouse released). <see cref="IsDirty"/> compares the current
/// position with the one marked saved.
/// </summary>
public sealed class UndoRedo
{
    private readonly List<IEditorAction> _actions = [];
    private int _position;           // actions [0, _position) are applied
    private string? _mergeKey;       // open merge window: the last action may absorb the next with the same key
    private object? _savedAt;        // the action at the top when saved (SavedAtStart for an empty prefix)
    private bool _savedReachable = true;

    private static readonly object SavedAtStart = new();

    public UndoRedo(int limit = 256)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        Limit = limit;
        _savedAt = SavedAtStart;
    }

    /// <summary>Most entries kept; older ones are discarded.</summary>
    public int Limit { get; }

    /// <summary>The history, oldest first; entries before <see cref="Position"/> are applied.</summary>
    public IReadOnlyList<IEditorAction> Actions => _actions;

    /// <summary>Number of applied actions.</summary>
    public int Position => _position;

    public bool CanUndo => _position > 0;

    public bool CanRedo => _position < _actions.Count;

    /// <summary>The action <see cref="Undo"/> would revert, or null.</summary>
    public IEditorAction? UndoAction => _position > 0 ? _actions[_position - 1] : null;

    /// <summary>The action <see cref="Redo"/> would apply, or null.</summary>
    public IEditorAction? RedoAction => _position < _actions.Count ? _actions[_position] : null;

    /// <summary>True while a merge window is open (between a keyed <see cref="Commit"/> and <see cref="EndMerge"/>).</summary>
    public bool IsMerging => _mergeKey is not null;

    /// <summary>True when the scene differs from the last <see cref="MarkSaved"/> (or the start).</summary>
    public bool IsDirty => !_savedReachable || !ReferenceEquals(_savedAt, CurrentMarker);

    /// <summary>Raised after every change of the history or position (commit, undo, redo, save, clear).</summary>
    public event Action? Changed;

    private object CurrentMarker => _position == 0 ? SavedAtStart : _actions[_position - 1];

    /// <summary>
    /// Records <paramref name="action"/>, applying it first unless <paramref name="alreadyApplied"/> (a gizmo drag that
    /// moved the node live). With <paramref name="mergeKey"/>, consecutive commits with the same key merge into one
    /// entry until <see cref="EndMerge"/>; a different key (or none) ends the window.
    /// </summary>
    public void Commit(IEditorAction action, bool alreadyApplied = false, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!alreadyApplied)
            action.Do();

        if (mergeKey is not null && string.Equals(mergeKey, _mergeKey, StringComparison.Ordinal) && _position > 0 &&
            _position == _actions.Count && _actions[_position - 1].TryMerge(action))
        {
            // The top entry now also covers this edit: if it was the saved state, the save point is gone.
            if (ReferenceEquals(_savedAt, _actions[_position - 1]))
                _savedReachable = false;
            Discard(action, applied: true, absorbed: true);
            Changed?.Invoke();
            return;
        }

        TruncateRedo();
        _actions.Add(action);
        _position = _actions.Count;
        _mergeKey = mergeKey;

        while (_actions.Count > Limit)
        {
            var oldest = _actions[0];
            _actions.RemoveAt(0);
            _position--;
            // Position 0 now means "after the oldest action": the state before it can no longer be reached.
            if (ReferenceEquals(_savedAt, SavedAtStart))
                _savedReachable = false;
            else if (ReferenceEquals(_savedAt, oldest))
                _savedAt = SavedAtStart;
            Discard(oldest, applied: true, absorbed: false);
        }

        Changed?.Invoke();
    }

    /// <summary>Closes the merge window (mouse released, field committed): the next commit starts a new entry.</summary>
    public void EndMerge() => _mergeKey = null;

    /// <summary>Reverts the last applied action. False when there is nothing to undo.</summary>
    public bool Undo()
    {
        _mergeKey = null;
        if (_position == 0)
            return false;
        _actions[_position - 1].Undo();
        _position--;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Re-applies the next action. False when there is nothing to redo.</summary>
    public bool Redo()
    {
        _mergeKey = null;
        if (_position >= _actions.Count)
            return false;
        _actions[_position].Do();
        _position++;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Undoes or redoes until <paramref name="position"/> actions are applied (the undo history menu).</summary>
    public void GoTo(int position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(position, _actions.Count);
        _mergeKey = null;
        var changed = _position != position;
        while (_position > position)
            _actions[--_position].Undo();
        while (_position < position)
            _actions[_position++].Do();
        if (changed)
            Changed?.Invoke();
    }

    /// <summary>The current state is what is on disk.</summary>
    public void MarkSaved()
    {
        _mergeKey = null;
        _savedAt = CurrentMarker;
        _savedReachable = true;
        Changed?.Invoke();
    }

    /// <summary>Drops the whole history (keeping the scene as it is); the save point is lost unless it is the current state.</summary>
    public void Clear()
    {
        var dirty = IsDirty;
        for (var i = _actions.Count - 1; i >= 0; i--)
            Discard(_actions[i], applied: i < _position, absorbed: false);
        _actions.Clear();
        _position = 0;
        _mergeKey = null;
        _savedAt = SavedAtStart;
        _savedReachable = !dirty;
        Changed?.Invoke();
    }

    private void TruncateRedo()
    {
        if (_position == _actions.Count)
            return;
        // The saved state may live in the branch being cut: then it can never be reached again.
        for (var i = _position; i < _actions.Count; i++)
            if (ReferenceEquals(_savedAt, _actions[i]))
                _savedReachable = false;
        for (var i = _actions.Count - 1; i >= _position; i--)
            Discard(_actions[i], applied: false, absorbed: false);
        _actions.RemoveRange(_position, _actions.Count - _position);
    }

    private static void Discard(IEditorAction action, bool applied, bool absorbed)
    {
        // An action absorbed by a merge handed its "do" state over: it owns nothing the merged entry does not.
        if (!absorbed && action is IDiscardableAction discardable)
            discardable.Discard(applied);
    }
}
