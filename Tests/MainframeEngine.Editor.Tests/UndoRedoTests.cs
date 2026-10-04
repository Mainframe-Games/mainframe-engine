namespace MainframeEngine.Editor.Tests;

public sealed class UndoRedoTests
{
    /// <summary>Adds to a shared list; merges by absorbing the next value; records discards.</summary>
    private sealed class Step(List<int> state, int value, List<string>? discards = null) : IDiscardableAction
    {
        public int Value { get; private set; } = value;
        public string Name => $"Step {Value}";
        public void Do() => state.Add(Value);
        public void Undo() => state.RemoveAt(state.Count - 1);

        public bool TryMerge(IEditorAction next)
        {
            if (next is not Step other)
                return false;
            state.RemoveAt(state.Count - 1); // the next step was applied before merging; it now lives in this one
            state[^1] = other.Value;
            Value = other.Value;
            return true;
        }

        public void Discard(bool applied) => discards?.Add($"{Value}:{(applied ? "applied" : "undone")}");
    }

    private readonly List<int> _state = [];
    private readonly List<string> _discards = [];

    private Step S(int value) => new(_state, value, _discards);

    [Fact]
    public void CommitAppliesAndUndoRedoWalkTheHistory()
    {
        var history = new UndoRedo();
        var changes = 0;
        history.Changed += () => changes++;

        history.Commit(S(1));
        history.Commit(S(2));
        Assert.Equal([1, 2], _state);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal("Step 2", history.UndoAction!.Name);

        Assert.True(history.Undo());
        Assert.Equal([1], _state);
        Assert.Equal("Step 2", history.RedoAction!.Name);
        Assert.True(history.Undo());
        Assert.False(history.Undo());
        Assert.Empty(_state);

        Assert.True(history.Redo());
        Assert.True(history.Redo());
        Assert.False(history.Redo());
        Assert.Equal([1, 2], _state);
        Assert.Equal(6, changes);
    }

    [Fact]
    public void AlreadyAppliedActionsAreRecordedWithoutRunningAgain()
    {
        var history = new UndoRedo();
        _state.Add(7); // e.g. a gizmo drag moved the node live
        history.Commit(S(7), alreadyApplied: true);
        Assert.Equal([7], _state);
        history.Undo();
        Assert.Empty(_state);
    }

    [Fact]
    public void CommittingAfterUndoCutsAndDiscardsTheRedoBranch()
    {
        var history = new UndoRedo();
        history.Commit(S(1));
        history.Commit(S(2));
        history.Commit(S(3));
        history.Undo();
        history.Undo();

        history.Commit(S(9));

        Assert.Equal([1, 9], _state);
        Assert.Equal(2, history.Actions.Count);
        Assert.False(history.CanRedo);
        Assert.Equal(["3:undone", "2:undone"], _discards);
    }

    [Fact]
    public void ContinuousEditsMergeByKeyUntilTheWindowCloses()
    {
        var history = new UndoRedo();
        history.Commit(S(1), mergeKey: "drag");
        history.Commit(S(2), mergeKey: "drag");
        history.Commit(S(3), mergeKey: "drag");
        Assert.Single(history.Actions);
        Assert.Equal([3], _state);
        Assert.True(history.IsMerging);

        history.EndMerge(); // mouse released
        history.Commit(S(4), mergeKey: "drag");
        Assert.Equal(2, history.Actions.Count);

        history.Commit(S(5), mergeKey: "other"); // another key never merges
        Assert.Equal(3, history.Actions.Count);
        Assert.Empty(_discards); // absorbed actions own nothing

        history.Undo();
        history.Undo();
        history.Undo();
        Assert.Empty(_state); // the merged drag undoes in one step
    }

    [Fact]
    public void UndoClosesTheMergeWindow()
    {
        var history = new UndoRedo();
        history.Commit(S(1), mergeKey: "k");
        history.Undo();
        history.Redo();
        history.Commit(S(2), mergeKey: "k");
        Assert.Equal(2, history.Actions.Count);
    }

    [Fact]
    public void TheOldestEntriesAreDroppedBeyondTheLimit()
    {
        var history = new UndoRedo(limit: 3);
        for (var i = 1; i <= 5; i++)
            history.Commit(S(i));

        Assert.Equal(3, history.Actions.Count);
        Assert.Equal(["1:applied", "2:applied"], _discards);
        Assert.Equal("Step 3", history.Actions[0].Name);
        history.Undo();
        history.Undo();
        history.Undo();
        Assert.False(history.CanUndo);
        Assert.Equal([1, 2], _state); // what the dropped entries did stays
    }

    [Fact]
    public void DirtyTracksThePositionRelativeToTheSave()
    {
        var history = new UndoRedo();
        Assert.False(history.IsDirty);
        history.Commit(S(1));
        Assert.True(history.IsDirty);
        history.MarkSaved();
        Assert.False(history.IsDirty);

        history.Commit(S(2));
        Assert.True(history.IsDirty);
        history.Undo();
        Assert.False(history.IsDirty); // back at the saved state
        history.Undo();
        Assert.True(history.IsDirty);
        history.Redo();
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void SavingClosesTheMergeWindowSoTheSavedEntryNeverChanges()
    {
        var history = new UndoRedo();
        history.Commit(S(1), mergeKey: "k");
        history.MarkSaved();
        history.Commit(S(2), mergeKey: "k"); // a new entry, not merged into the saved one

        Assert.Equal(2, history.Actions.Count);
        Assert.True(history.IsDirty);
        history.Undo();
        Assert.False(history.IsDirty);
        Assert.Equal([1], _state);
    }

    [Fact]
    public void CuttingTheBranchThatHeldTheSavePointLeavesTheSceneDirty()
    {
        var history = new UndoRedo();
        history.Commit(S(1));
        history.Commit(S(2));
        history.MarkSaved();
        history.Undo();
        history.Commit(S(3)); // the saved state (1, 2) is gone

        Assert.True(history.IsDirty);
        history.Undo();
        Assert.True(history.IsDirty);
    }

    [Fact]
    public void PruningKeepsTheSavePointWhenItIsStillReachable()
    {
        var history = new UndoRedo(limit: 2);
        history.Commit(S(1));
        history.MarkSaved();
        history.Commit(S(2));
        history.Commit(S(3)); // drops Step 1: position 0 now means "after Step 1" = saved

        history.Undo();
        history.Undo();
        Assert.False(history.IsDirty);

        var fromStart = new UndoRedo(limit: 1);
        fromStart.Commit(S(1));
        fromStart.Commit(S(2)); // the saved start can never come back
        fromStart.Undo();
        Assert.True(fromStart.IsDirty);
    }

    [Fact]
    public void GoToUndoesOrRedoesToAPosition()
    {
        var history = new UndoRedo();
        for (var i = 1; i <= 4; i++)
            history.Commit(S(i));

        history.GoTo(1);
        Assert.Equal([1], _state);
        history.GoTo(3);
        Assert.Equal([1, 2, 3], _state);
        Assert.Throws<ArgumentOutOfRangeException>(() => history.GoTo(5));
    }

    [Fact]
    public void ClearDiscardsEverythingAndKeepsTheScene()
    {
        var history = new UndoRedo();
        history.Commit(S(1));
        history.Commit(S(2));
        history.Undo();

        history.Clear();

        Assert.Empty(history.Actions);
        Assert.Equal([1], _state);
        Assert.Equal(["2:undone", "1:applied"], _discards);
        Assert.True(history.IsDirty); // it was dirty before clearing
    }

    [Fact]
    public void CompositeActionsDoInOrderUndoInReverseAndMergeElementWise()
    {
        var log = new List<string>();
        var a = new LoggedAction("a", log);
        var b = new LoggedAction("b", log);
        var composite = new CompositeAction("Both", a, b);

        composite.Do();
        composite.Undo();
        Assert.Equal(["do a", "do b", "undo b", "undo a"], log);

        var target = new AllHintsNode();
        var info = Serialization.TypeRegistry.GetRequired(typeof(AllHintsNode));
        var count = info.FindProperty("Count")!;
        var speed = info.FindProperty("Speed")!;
        var first = new CompositeAction("Drag", new SetPropertyAction(target, count, 3, 4), new SetPropertyAction(target, speed, 1.5f, 2f));
        var second = new CompositeAction("Drag", new SetPropertyAction(target, count, 4, 5), new SetPropertyAction(target, speed, 2f, 3f));
        Assert.True(first.TryMerge(second));
        first.Do();
        Assert.Equal((5, 3f), (target.Count, target.Speed));
        first.Undo();
        Assert.Equal((3, 1.5f), (target.Count, target.Speed));
        Assert.False(first.TryMerge(new CompositeAction("Other", new SetPropertyAction(target, speed, 1f, 2f))));
    }

    private sealed class LoggedAction(string name, List<string> log) : IEditorAction
    {
        public string Name => name;
        public void Do() => log.Add($"do {name}");
        public void Undo() => log.Add($"undo {name}");
    }
}
