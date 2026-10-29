namespace MainframeEngine.Tests.Crash;

/// <summary>
/// The scene tree's exception filters (G15): an exception leaving a node callback is noted with its node and callback
/// before the stack unwinds, and propagates unchanged — same object, type and stack — so <c>Tick</c> still throws.
/// </summary>
public sealed class NodeExceptionFilterTests
{
    private static GameTime Frame => new() { DeltaTime = 1f / 60f, FrameCount = 1 };

    private sealed class Thrower : Node
    {
        public NodeCallback ThrowIn { get; init; }

        public Exception? Thrown { get; private set; }

        protected override void OnReady()
        {
            SetProcess(true);
            SetPhysicsProcess(true);
            SetProcessInput(true);
            SetProcessUnhandledInput(true);
            Throw(NodeCallback.Ready);
        }

        protected override void OnProcess(in GameTime gameTime) => Throw(NodeCallback.Process);

        protected override void OnPhysicsProcess(float delta) => Throw(NodeCallback.PhysicsProcess);

        protected override void OnInput(InputEvent inputEvent) => Throw(NodeCallback.Input);

        protected override void OnUnhandledInput(InputEvent inputEvent) => Throw(NodeCallback.UnhandledInput);

        private void Throw(NodeCallback callback)
        {
            if (callback != ThrowIn)
                return;
            Thrown = new InvalidOperationException($"thrown in {callback}");
            throw Thrown;
        }
    }

    private static (Exception Caught, Thrower Node) Run(NodeCallback callback, Action<SceneTree, Thrower> act)
    {
        var tree = new SceneTree();
        var scene = new Node { Name = "Level" };
        tree.ChangeScene(scene);
        var node = new Thrower { Name = "Player", ThrowIn = callback == NodeCallback.Ready ? NodeCallback.None : callback };
        scene.AddChild(node);
        try
        {
            var caught = Assert.ThrowsAny<Exception>(() => act(tree, node));
            return (caught, node);
        }
        finally
        {
            tree.Shutdown();
        }
    }

    [Theory]
    [InlineData(NodeCallback.Process, "OnProcess")]
    [InlineData(NodeCallback.PhysicsProcess, "OnPhysicsProcess")]
    public void TickThrowsTheSameExceptionAndNotesTheNode(NodeCallback callback, string name)
    {
        var (caught, node) = Run(callback, static (tree, _) => tree.Tick(Frame));

        Assert.Same(node.Thrown, caught);
        Assert.IsType<InvalidOperationException>(caught);
        Assert.Contains(nameof(Thrower), caught.StackTrace, StringComparison.Ordinal); // the throw site's stack is intact
        var note = CrashContext.NodeOf(caught);
        Assert.NotNull(note);
        Assert.Equal("/root/Level/Player", note!.Path);
        Assert.Equal(typeof(Thrower).FullName, note.Type);
        Assert.Equal(callback, note.Callback);
        Assert.Equal(name, CrashContext.CallbackName(note.Callback));
    }

    [Theory]
    [InlineData(NodeCallback.Input)]
    [InlineData(NodeCallback.UnhandledInput)]
    public void InputCallbacksAreNoted(NodeCallback callback)
    {
        var (caught, _) = Run(callback, static (tree, _) => tree.PushInput(new InputEventMouseMotion()));

        Assert.Equal(callback, CrashContext.NodeOf(caught)!.Callback);
    }

    [Fact]
    public void ReadyNamesTheChildNotItsParent()
    {
        var tree = new SceneTree();
        tree.ChangeScene(new Node { Name = "Level" });
        var parent = new Node { Name = "Parent" };
        tree.CurrentScene!.AddChild(parent);

        var caught = Assert.Throws<InvalidOperationException>(() => parent.AddChild(new Thrower { Name = "Child", ThrowIn = NodeCallback.Ready }));

        var note = CrashContext.NodeOf(caught)!;
        Assert.Equal(NodeCallback.Ready, note.Callback);
        Assert.EndsWith("/Parent/Child", note.Path, StringComparison.Ordinal);
        tree.Shutdown();
    }

    [Fact]
    public void ADeferredCallNotesItsNode()
    {
        var tree = new SceneTree();
        var level = new Node { Name = "Level" };
        tree.ChangeScene(level);
        level.CallDeferred(static state => throw new FormatException("deferred"), level);

        var caught = Assert.Throws<FormatException>(() => tree.Tick(Frame));

        var note = CrashContext.NodeOf(caught)!;
        Assert.Equal(NodeCallback.Deferred, note.Callback);
        Assert.Equal("/root/Level", note.Path);
        tree.Shutdown();
    }

    [Fact]
    public void AWrappedExceptionStillFindsItsNode()
    {
        var (caught, _) = Run(NodeCallback.Process, static (tree, _) => tree.Tick(Frame));
        var wrapped = new AggregateException(new InvalidOperationException("outer", caught));

        Assert.Equal(NodeCallback.Process, CrashContext.NodeOf(wrapped)!.Callback);
        Assert.Null(CrashContext.NodeOf(new InvalidOperationException("never thrown in a node")));
    }
}
