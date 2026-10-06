namespace MainframeEngine.Tests.Scene;

public sealed class SceneTreeTests : IDisposable
{
    private readonly SceneTree _tree = new();
    private readonly List<string> _log = [];

    public void Dispose() => _tree.Shutdown();

    private static GameTime Frame(float dt = 1f / 60f) => new() { DeltaTime = dt, FrameCount = 1 };

    private LoggingNode Logged(string name) => new() { Name = name, Log = _log };

    [Fact]
    public void EnterIsTopDownReadyIsBottomUpExitIsBottomUp()
    {
        var parent = Logged("P");
        var a = Logged("A");
        var a1 = Logged("A1");
        var b = Logged("B");
        parent.AddChild(a);
        a.AddChild(a1);
        parent.AddChild(b);

        _tree.Root.AddChild(parent);
        Assert.Equal(["P:enter", "A:enter", "A1:enter", "B:enter", "A1:ready", "A:ready", "B:ready", "P:ready"], _log);

        _log.Clear();
        _tree.Root.RemoveChild(parent);
        Assert.Equal(["B:exit", "A1:exit", "A:exit", "P:exit"], _log);
        Assert.False(parent.IsInsideTree);
        Assert.Null(a1.Tree);
        parent.Free();
    }

    [Fact]
    public void ReadyRunsOncePerNodeUnlessRequested()
    {
        var node = Logged("N");
        _tree.Root.AddChild(node);
        _tree.Root.RemoveChild(node);
        _tree.Root.AddChild(node);
        Assert.Equal(1, _log.Count(e => e == "N:ready"));

        _tree.Root.RemoveChild(node);
        node.RequestReady();
        _tree.Root.AddChild(node);
        Assert.Equal(2, _log.Count(e => e == "N:ready"));
    }

    [Fact]
    public void BuiltInSignalsFireAroundTheCallbacks()
    {
        var parent = new PlainNode { Name = "P" };
        var child = Logged("C");
        parent.AddChild(child);
        child.TreeEntered += () => _log.Add("C:tree_entered");
        child.Ready += () => _log.Add("C:ready_signal");
        child.TreeExiting += () => _log.Add("C:tree_exiting");
        child.TreeExited += () => _log.Add("C:tree_exited");
        parent.ChildEnteredTree += n => _log.Add($"P:child_entered {n.Name}");
        parent.ChildExitingTree += n => _log.Add($"P:child_exiting {n.Name}");
        child.Renamed += () => _log.Add("C:renamed");

        _tree.Root.AddChild(parent);
        child.Name = "C2";
        _tree.Root.RemoveChild(parent);

        Assert.Equal(
        [
            "C:enter", "C:tree_entered", "P:child_entered C", "C:ready", "C:ready_signal", "C:renamed",
            "C2:exit", "C:tree_exiting", "P:child_exiting C2", "C:tree_exited",
        ], _log);
        parent.Free();
    }

    [Fact]
    public void ChildrenAddedDuringReadyEnterImmediately()
    {
        var parent = Logged("P");
        parent.OnReadyAction = p => p.AddChild(Logged("Late"));
        _tree.Root.AddChild(parent);

        Assert.Equal(["P:enter", "P:ready", "Late:enter", "Late:ready"], _log);
        Assert.True(parent.GetNode("Late").IsInsideTree);
    }

    [Fact]
    public void ChildrenAddedDuringEnterTreeEnterOnce()
    {
        var parent = Logged("P");
        parent.OnEnterAction = p => p.AddChild(Logged("Early"));
        _tree.Root.AddChild(parent);

        Assert.Equal(1, _log.Count(e => e == "Early:enter"));
        Assert.Equal(1, _log.Count(e => e == "Early:ready"));
        Assert.True(_log.IndexOf("Early:ready") < _log.IndexOf("P:ready"));
    }

    [Fact]
    public void ProcessRunsByPriorityThenTreeOrder()
    {
        var a = Logged("A");
        var b = Logged("B");
        var c = Logged("C");
        var plain = new PlainNode();
        _tree.Root.AddChild(a);
        _tree.Root.AddChild(b);
        b.AddChild(c);
        _tree.Root.AddChild(plain);
        _log.Clear();

        _tree.Tick(Frame());
        Assert.Equal(["A:physics", "B:physics", "C:physics", "A:process", "B:process", "C:process"], _log);

        _log.Clear();
        c.ProcessPriority = -1;
        a.ProcessPriority = 5;
        _tree.Tick(Frame());
        Assert.Equal(["C:physics", "B:physics", "A:physics", "C:process", "B:process", "A:process"], _log);

        // Tree order follows MoveChild.
        _log.Clear();
        a.ProcessPriority = 0;
        c.ProcessPriority = 0;
        _tree.Root.MoveChild(b, 0);
        _tree.Tick(Frame(0));
        Assert.Equal(["B:process", "C:process", "A:process"], _log);
    }

    [Fact]
    public void SetProcessTogglesCallbacks()
    {
        var node = Logged("N");
        _tree.Root.AddChild(node);
        Assert.True(node.IsProcessing);
        Assert.True(node.IsPhysicsProcessing);

        node.SetProcess(false);
        node.SetPhysicsProcess(false);
        _log.Clear();
        _tree.Tick(Frame());
        Assert.Empty(_log);

        node.SetProcess(true);
        _tree.Tick(Frame());
        Assert.Equal(["N:process"], _log);

        Assert.False(new PlainNode().IsProcessing);
    }

    [Fact]
    public void PhysicsUsesAFixedStepAccumulator()
    {
        var node = Logged("N");
        _tree.Root.AddChild(node);

        _tree.Tick(Frame(1f / 60f));
        Assert.Single(node.PhysicsDeltas);

        _tree.Tick(Frame(1f / 120f)); // half a step: no physics this frame
        Assert.Single(node.PhysicsDeltas);
        Assert.Equal(1f / 120f, _tree.ProcessDeltaTime, 1e-6f); // nothing dropped: process gets the frame's delta
        Assert.InRange(_tree.PhysicsInterpolationFraction, 0.49f, 0.51f);

        _tree.Tick(Frame(1f / 120f)); // completes the step
        Assert.Equal(2, node.PhysicsDeltas.Count);
        Assert.All(node.PhysicsDeltas, d => Assert.Equal(1f / 60f, d, 1e-6f));

        _tree.Tick(Frame(3f / 60f)); // three steps
        Assert.Equal(5, node.PhysicsDeltas.Count);
        Assert.Equal(4, node.ProcessCount);
        Assert.Equal(5ul, _tree.PhysicsFrames);
    }

    [Fact]
    public void PhysicsClampsLongFramesAndCapsSubsteps()
    {
        var node = Logged("N");
        _tree.Root.AddChild(node);

        _tree.Tick(Frame(10f)); // clamped to 0.25 s = 15 steps, capped at 5
        Assert.Equal(5, node.PhysicsDeltas.Count);
        // Process sees only the time physics ran (Godot drops the skipped steps from process_step too).
        Assert.Equal(5f / 60f, _tree.ProcessDeltaTime, 1e-5f);

        // The backlog is dropped, not carried into the next frames.
        _tree.Tick(Frame(0));
        Assert.Equal(5, node.PhysicsDeltas.Count);

        _tree.PhysicsTicksPerSecond = 30;
        _tree.Tick(Frame(1f / 30f));
        Assert.Equal(1f / 30f, node.PhysicsDeltas[^1], 1e-6f);
    }

    [Fact]
    public void FixedDeltaEqualToTheStepAlwaysStepsOnce()
    {
        var node = Logged("N");
        _tree.Root.AddChild(node);
        for (var i = 0; i < 1000; i++)
            _tree.Tick(Frame(1f / 60f));
        Assert.Equal(1000, node.PhysicsDeltas.Count);
    }

    [Fact]
    public void PauseStopsPausableNodesAndRunsWhenPausedOnes()
    {
        var pausable = Logged("Pausable");
        var always = Logged("Always");
        var whenPaused = Logged("WhenPaused");
        var disabled = Logged("Disabled");
        var inheritsAlways = Logged("Inherit");
        always.ProcessMode = ProcessMode.Always;
        whenPaused.ProcessMode = ProcessMode.WhenPaused;
        disabled.ProcessMode = ProcessMode.Disabled;
        _tree.Root.AddChild(pausable);
        _tree.Root.AddChild(always);
        always.AddChild(inheritsAlways);
        _tree.Root.AddChild(whenPaused);
        _tree.Root.AddChild(disabled);

        _log.Clear();
        _tree.Tick(Frame(0));
        Assert.Equal(["Pausable:process", "Always:process", "Inherit:process"], _log);

        _tree.Paused = true;
        _log.Clear();
        _tree.Tick(Frame(0));
        Assert.Equal(["Always:process", "Inherit:process", "WhenPaused:process"], _log);
        Assert.False(pausable.CanProcess());
        Assert.True(inheritsAlways.CanProcess());
        Assert.Equal(ProcessMode.Always, inheritsAlways.ResolvedProcessMode);

        // Changing a parent's mode re-resolves inheriting children.
        always.ProcessMode = ProcessMode.Pausable;
        Assert.False(inheritsAlways.CanProcess());
    }

    [Fact]
    public void QueueFreeDuringProcessIsSafeAndHappensAtTheEndOfTheFrame()
    {
        var a = Logged("A");
        var b = Logged("B");
        var c = Logged("C");
        _tree.Root.AddChild(a);
        _tree.Root.AddChild(b);
        _tree.Root.AddChild(c);
        a.OnProcessAction = _ =>
        {
            b.QueueFree();
            c.Free(); // immediate free mid-iteration is skipped, not crashed on
        };

        _log.Clear();
        _tree.Tick(Frame(0));

        Assert.Equal(["A:process", "C:exit", "B:exit"], _log);
        Assert.True(b.IsFreed);
        Assert.True(c.IsFreed);
        Assert.Single(_tree.Root.Children);
    }

    [Fact]
    public void DeferredCallsRunAfterProcessInOrderBeforeFrees()
    {
        var node = Logged("N");
        var victim = Logged("Victim");
        _tree.Root.AddChild(node);
        _tree.Root.AddChild(victim);
        node.OnProcessAction = n =>
        {
            victim.QueueFree();
            n.CallDeferred(() => _log.Add("deferred 1"));
            n.CallDeferred(static s => ((List<string>)s!).Add("deferred 2"), _log);
            n.CallDeferred(() =>
            {
                _log.Add("deferred 3");
                _tree.CallDeferred(() => _log.Add("queued during flush"));
            });
        };

        _log.Clear();
        _tree.Tick(Frame(0));

        // Victim was queued before its turn, so it does not process this frame.
        Assert.Equal(
            ["N:process", "deferred 1", "deferred 2", "deferred 3", "queued during flush", "Victim:exit"],
            _log);
    }

    [Fact]
    public void CallsDeferredByReadyOrPhysicsRunBeforeProcessLikeGodotsMessageQueue()
    {
        var node = Logged("N");
        var victim = Logged("Victim");
        node.OnReadyAction = n =>
        {
            n.CallDeferred(() => _log.Add("deferred by ready"));
            victim.QueueFree(); // frees still wait for the end of the frame
        };
        _tree.Root.AddChild(victim);
        _tree.Root.AddChild(node);
        node.OnProcessAction = _ => _log.Add($"process (victim freed={victim.IsFreed})");

        _log.Clear();
        _tree.Tick(Frame(0));

        Assert.Equal(["deferred by ready", "N:process", "process (victim freed=False)", "Victim:exit"], _log);
    }

    [Fact]
    public void CallDeferredNeedsATree()
    {
        using var loose = new PlainNode();
        Assert.Throws<InvalidOperationException>(() => loose.CallDeferred(() => { }));
    }

    [Fact]
    public void GroupsTrackMembersInTreeOrder()
    {
        var a = new PlainNode { Name = "A" };
        var b = new PlainNode { Name = "B" };
        var c = new PlainNode { Name = "C" };
        c.AddToGroup("enemies");
        a.AddToGroup("enemies");
        _tree.Root.AddChild(a);
        _tree.Root.AddChild(b);
        _tree.Root.AddChild(c);
        b.AddToGroup("enemies", persistent: true);

        Assert.Equal([a, b, c], _tree.GetNodesInGroup("enemies"));
        Assert.Same(a, _tree.GetFirstNodeInGroup("enemies"));
        Assert.True(b.IsInGroup("enemies"));
        Assert.Contains(b.Groups, g => g is { Name: "enemies", Persistent: true });

        var visited = new List<string>();
        _tree.CallGroup("enemies", n =>
        {
            visited.Add(n.Name);
            if (n == a)
                c.RemoveFromGroup("enemies"); // removal during the call is honoured
        });
        Assert.Equal(["A", "B"], visited);

        _tree.Root.RemoveChild(b);
        Assert.Equal([a], _tree.GetNodesInGroup("enemies"));
        Assert.True(b.IsInGroup("enemies")); // membership travels with the node
        _tree.Root.AddChild(b);
        Assert.Equal([a, b], _tree.GetNodesInGroup("enemies"));
        Assert.Empty(_tree.GetNodesInGroup("nobody"));
        Assert.False(_tree.HasGroup("nobody"));
    }

    [Fact]
    public void FindByNodeIdTracksEnterAndExit()
    {
        var node = new PlainNode();
        Assert.Null(_tree.Find(node.Id));
        _tree.Root.AddChild(node);
        Assert.Same(node, _tree.Find(node.Id));
        var count = _tree.NodeCount;

        node.QueueFree();
        _tree.FlushDeferred();
        Assert.Null(_tree.Find(node.Id));
        Assert.Equal(count - 1, _tree.NodeCount);
    }

    [Fact]
    public void ChangeSceneReplacesAndFreesTheOldScene()
    {
        var first = Logged("First");
        var second = Logged("Second");
        _tree.ChangeScene(first);
        Assert.Same(first, _tree.CurrentScene);

        _tree.ChangeScene(second);
        Assert.True(first.IsFreed);
        Assert.Same(second, _tree.CurrentScene);

        // During a tick the change is deferred to the end of the frame.
        var third = Logged("Third");
        second.OnProcessAction = _ =>
        {
            _tree.ChangeScene(third);
            Assert.Same(second, _tree.CurrentScene);
        };
        _tree.Tick(Frame(0));
        Assert.Same(third, _tree.CurrentScene);
        Assert.True(second.IsFreed);
    }

    [Fact]
    public void TimersFireOnceAndRespectPause()
    {
        var fired = 0;
        var timer = _tree.CreateTimer(0.05f, processAlways: false);
        timer.Timeout += () => fired++;

        _tree.Tick(Frame(0.03f));
        Assert.Equal(0, fired);
        _tree.Paused = true;
        _tree.Tick(Frame(0.03f));
        Assert.Equal(0, fired);
        _tree.Paused = false;
        _tree.Tick(Frame(0.03f));
        Assert.Equal(1, fired);
        _tree.Tick(Frame(1f));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void TimerNodeRepeatsUnlessOneShot()
    {
        var repeating = new Timer { WaitTime = 0.1f, Autostart = true };
        var oneShot = new Timer { WaitTime = 0.1f, OneShot = true, Autostart = true, ProcessCallback = TimerProcessCallback.Physics };
        int repeats = 0, shots = 0;
        repeating.Timeout += () => repeats++;
        oneShot.Timeout += () => shots++;
        _tree.Root.AddChild(repeating);
        _tree.Root.AddChild(oneShot);

        for (var i = 0; i < 60; i++)
            _tree.Tick(Frame(1f / 60f));

        Assert.InRange(repeats, 9, 10);
        Assert.Equal(1, shots);
        Assert.True(oneShot.IsStopped);
        Assert.False(repeating.IsStopped);

        repeating.Stop();
        Assert.True(repeating.IsStopped);
    }

    [Fact]
    public void ATimerFiresOnTheFrameGodotsDoes()
    {
        // Godot: time_left is a double and times out below zero. A 1 s timer at a fixed 60 FPS fires on frame 60 and
        // then every 60 frames (a float countdown fired on frame 61; ADR 0135).
        var timer = new Timer { WaitTime = 1f, Autostart = true };
        var frames = new List<int>();
        var frame = 0;
        timer.Timeout += () => frames.Add(frame);
        _tree.Root.AddChild(timer);

        for (frame = 1; frame <= 180; frame++)
            _tree.Tick(Frame(1f / 60f));

        Assert.Equal([60, 120, 180], frames);
        timer.Stop();
        Assert.True(timer.IsStopped);
        Assert.Equal(0f, timer.TimeLeft);
    }

    [Fact]
    public void ProcessFrameEventsFire()
    {
        int process = 0, physics = 0;
        _tree.ProcessFrame += () => process++;
        _tree.PhysicsFrame += () => physics++;
        _tree.Tick(Frame(2f / 60f));
        Assert.Equal(1, process);
        Assert.Equal(2, physics);
        Assert.Equal(1ul, _tree.ProcessFrames);
    }

    [Fact]
    public void InputRoutesInReverseTreeOrderUntilHandled()
    {
        var log = new List<string>();
        var a = new InputNode { Name = "A", Log = log };
        var b = new InputNode { Name = "B", Log = log };
        var b1 = new InputNode { Name = "B1", Log = log };
        _tree.Root.AddChild(a);
        _tree.Root.AddChild(b);
        b.AddChild(b1);

        var handled = _tree.PushInput(new InputEventKey { Key = Silk.NET.Input.Key.A, Pressed = true });
        Assert.False(handled);
        Assert.Equal(["B1:input", "B:input", "A:input", "B1:unhandled", "B:unhandled", "A:unhandled"], log);

        log.Clear();
        b.HandleInput = true;
        Assert.True(_tree.PushInput(new InputEventMouseWheel()));
        Assert.Equal(["B1:input", "B:input"], log);

        log.Clear();
        b.HandleInput = false;
        b1.HandleUnhandled = true;
        _tree.Paused = true; // pausable nodes get no input
        _tree.PushInput(new InputEventText { Character = 'x' });
        Assert.Empty(log);
    }

    [Fact]
    public void ShutdownFreesEverythingAndRejectsFurtherTicks()
    {
        var tree = new SceneTree();
        var node = Logged("N");
        tree.Root.AddChild(node);
        tree.Shutdown();

        Assert.True(node.IsFreed);
        Assert.True(tree.Root.IsFreed);
        Assert.Contains("N:exit", _log);
        Assert.Throws<ObjectDisposedException>(() => tree.Tick(Frame()));
        tree.Shutdown(); // idempotent
    }

    [Fact]
    public void TreeRootCannotBeReparented()
    {
        using var other = new PlainNode();
        Assert.Throws<InvalidOperationException>(() => other.AddChild(_tree.Root));
        Assert.True(_tree.Root.IsTreeRoot);
        Assert.Equal("root", _tree.Root.Name);
        Assert.Same(_tree.Root, _tree.Root.GetViewport());
    }
}
