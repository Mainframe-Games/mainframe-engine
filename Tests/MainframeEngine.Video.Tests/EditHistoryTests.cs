namespace MainframeEngine.Video.Tests;

public sealed class EditHistoryTests
{
    // ---- construction -------------------------------------------------------------------------

    [Fact]
    public void NewHistoryHasNothingToUndoOrRedo()
    {
        var history = new EditHistory<string>(10);

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
        Assert.Equal(10, history.Limit);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void LimitBelowOneIsClampedToOne(int limit)
    {
        var history = new EditHistory<string>(limit);

        Assert.Equal(1, history.Limit);
    }

    // ---- undo / redo --------------------------------------------------------------------------

    [Fact]
    public void UndoReturnsTheStatePushedBeforeTheEdit()
    {
        var history = new EditHistory<string>(10);

        history.Push("before");
        var restored = history.Undo("after");

        Assert.Equal("before", restored);
        Assert.False(history.CanUndo);
        Assert.True(history.CanRedo);
    }

    [Fact]
    public void RedoReturnsTheStateThatUndoReplaced()
    {
        var history = new EditHistory<string>(10);
        history.Push("before");
        history.Undo("after");

        var redone = history.Redo("before");

        Assert.Equal("after", redone);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    [Fact]
    public void UndoWalksBackThroughEditsInReverseOrder()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");
        history.Push("v1");
        history.Push("v2");

        Assert.Equal("v2", history.Undo("v3"));
        Assert.Equal("v1", history.Undo("v2"));
        Assert.Equal("v0", history.Undo("v1"));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void PushAfterUndoDiscardsTheRedoStack()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");
        history.Undo("v1");

        history.Push("v0");

        Assert.False(history.CanRedo);
    }

    [Fact]
    public void UndoWithNothingToUndoThrows()
    {
        var history = new EditHistory<string>(10);

        var act = () => history.Undo("current");

        Assert.Throws<InvalidOperationException>(act);
    }

    [Fact]
    public void RedoWithNothingToRedoThrows()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");

        var act = () => history.Redo("current");

        Assert.Throws<InvalidOperationException>(act);
    }

    // ---- limit --------------------------------------------------------------------------------

    [Fact]
    public void PushingBeyondTheLimitDropsTheOldestEntry()
    {
        var history = new EditHistory<string>(2);
        history.Push("v0");
        history.Push("v1");
        history.Push("v2");

        Assert.Equal("v2", history.Undo("v3"));
        Assert.Equal("v1", history.Undo("v2"));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void LoweringTheLimitTrimsTheOldestEntriesImmediately()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");
        history.Push("v1");
        history.Push("v2");

        history.Limit = 1;

        Assert.Equal("v2", history.Undo("v3"));
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void RaisingTheLimitKeepsExistingEntries()
    {
        var history = new EditHistory<string>(2);
        history.Push("v0");
        history.Push("v1");

        history.Limit = 5;

        Assert.Equal("v1", history.Undo("v2"));
        Assert.Equal("v0", history.Undo("v1"));
    }

    // ---- clear --------------------------------------------------------------------------------

    [Fact]
    public void ClearEmptiesBothStacks()
    {
        var history = new EditHistory<string>(10);
        history.Push("v0");
        history.Push("v1");
        history.Undo("v2");

        history.Clear();

        Assert.False(history.CanUndo);
        Assert.False(history.CanRedo);
    }

    // ---- change notification ------------------------------------------------------------------

    [Fact]
    public void ChangedIsRaisedByPushUndoRedoAndClear()
    {
        var history = new EditHistory<string>(10);
        var count = 0;
        history.Changed += (_, _) => count++;

        history.Push("v0");
        history.Undo("v1");
        history.Redo("v0");
        history.Clear();

        Assert.Equal(4, count);
    }
}
