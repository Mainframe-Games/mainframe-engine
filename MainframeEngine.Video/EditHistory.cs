namespace MainframeEngine.Video;

/// <summary>
/// Snapshot-based undo/redo. Callers <see cref="Push"/> the state as it was <em>before</em> an
/// edit; <see cref="Undo"/> and <see cref="Redo"/> take the current state and hand back the one to
/// restore. Snapshots are expected to be cheap: for segments that is an array of references to
/// immutable <see cref="Models.Segment"/> records.
/// </summary>
/// <typeparam name="T">The snapshot type.</typeparam>
public sealed class EditHistory<T>
    where T : class
{
    private readonly List<T> _undo = [];
    private readonly Stack<T> _redo = new();
    private int _limit;

    public EditHistory(int limit)
    {
        _limit = Math.Max(1, limit);
    }

    /// <summary>
    /// Maximum number of undo steps kept. Never below 1. Lowering it drops the oldest steps at once.
    /// </summary>
    public int Limit
    {
        get => _limit;
        set
        {
            _limit = Math.Max(1, value);
            if (Trim())
            {
                OnChanged();
            }
        }
    }

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised whenever <see cref="CanUndo"/> or <see cref="CanRedo"/> may have changed.</summary>
    public event EventHandler? Changed;

    /// <summary>Records the state before an edit. Anything that could have been redone is discarded.</summary>
    public void Push(T stateBeforeEdit)
    {
        ArgumentNullException.ThrowIfNull(stateBeforeEdit);
        _undo.Add(stateBeforeEdit);
        _redo.Clear();
        Trim();
        OnChanged();
    }

    /// <summary>Steps back once. <paramref name="current"/> becomes redoable.</summary>
    /// <returns>The state to restore.</returns>
    /// <exception cref="InvalidOperationException">Nothing to undo.</exception>
    public T Undo(T current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_undo.Count == 0)
        {
            throw new InvalidOperationException("Nothing to undo.");
        }

        var restored = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Push(current);
        OnChanged();
        return restored;
    }

    /// <summary>Steps forward once. <paramref name="current"/> becomes undoable again.</summary>
    /// <returns>The state to restore.</returns>
    /// <exception cref="InvalidOperationException">Nothing to redo.</exception>
    public T Redo(T current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (_redo.Count == 0)
        {
            throw new InvalidOperationException("Nothing to redo.");
        }

        var restored = _redo.Pop();
        _undo.Add(current);
        OnChanged();
        return restored;
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        OnChanged();
    }

    /// <returns>True if any entries were dropped.</returns>
    private bool Trim()
    {
        var excess = _undo.Count - _limit;
        if (excess <= 0)
        {
            return false;
        }

        _undo.RemoveRange(0, excess);
        return true;
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
