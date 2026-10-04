namespace MainframeEngine.Tests.Physics;

/// <summary>The scene tree's interpolation hooks for fixed-step servers (<see cref="IFixedStepServer"/>).</summary>
public sealed class FixedStepHookTests
{
    private sealed class RecordingServer : IFixedStepServer
    {
        public List<string> Calls { get; } = [];

        public void FixedStep(float delta) => Calls.Add("step");

        public void BeforeFixedSteps() => Calls.Add("before");

        public void AfterFixedSteps(float interpolationFraction) => Calls.Add($"after {interpolationFraction:0.00}");

        public void Dispose()
        {
        }
    }

    private sealed class PhysicsProcessNode(List<string> calls) : Node
    {
        protected override void OnPhysicsProcess(float delta) => calls.Add("physics");

        protected override void OnProcess(in GameTime gameTime) => calls.Add("process");
    }

    [Fact]
    public void HooksBracketTheFixedStepsAndRunBeforeProcess()
    {
        var server = new RecordingServer();
        var servers = new ServerRegistry();
        servers.Register(server);
        var tree = new SceneTree(servers);
        tree.Root.AddChild(new PhysicsProcessNode(server.Calls));

        tree.Tick(new GameTime { DeltaTime = 1f / 60 * 2.5f }); // two steps, half a step left
        Assert.Equal(["before", "physics", "step", "physics", "step", "after 0.50", "process"], server.Calls);

        server.Calls.Clear();
        tree.Tick(new GameTime { DeltaTime = 1f / 60 * 0.25f }); // no step: no "before", still "after"
        Assert.Equal(["after 0.75", "process"], server.Calls);

        server.Calls.Clear();
        tree.Paused = true; // frozen: no hooks, no steps (and the pausable node doesn't process either)
        tree.Tick(new GameTime { DeltaTime = 1f / 60 });
        Assert.Empty(server.Calls);
        tree.Shutdown();
    }
}
