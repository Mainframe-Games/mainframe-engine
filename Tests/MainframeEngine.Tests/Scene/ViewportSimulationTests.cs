using System.Numerics;
using MainframeEngine.Networking;
using MainframeEngine.Serialization;
using Silk.NET.Input;

namespace MainframeEngine.Tests.Scene;

/// <summary>
/// G7.5: a <see cref="SubViewport"/> with <see cref="SubViewport.Simulation"/> runs its subtree as a game inside an
/// edit-mode tree (game scripts, physics of its own space only, no input), pauses alone, and catches the exceptions of
/// its game code in <see cref="SceneTree.SimulationFault"/> instead of letting them escape <see cref="SceneTree.Tick"/>.
/// </summary>
public sealed class ViewportSimulationTests : IDisposable
{
    private readonly SceneTree _tree;
    private readonly PhysicsServer3D _physics = new(new PhysicsSettings3D { MultiThreaded = false });

    public ViewportSimulationTests()
    {
        var servers = new ServerRegistry();
        servers.Register(_physics);
        _tree = new SceneTree(servers)
        {
            EditMode = true,
            EditModeScripts = static t => t.Assembly == typeof(ViewportSimulationTests).Assembly, // this assembly is the game
        };
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _tree.Servers.Dispose();
    }

    private void Run(int frames)
    {
        for (var i = 0; i < frames; i++)
            _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
    }

    private static RigidBody3D Crate(string name)
    {
        var body = new RigidBody3D { Name = name, Position = new Vector3(0, 5, 0) };
        body.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = Vector3.One } });
        return body;
    }

    private SubViewport View(ViewportSimulation simulation)
    {
        var view = new SubViewport { Name = "Simulation", Simulation = simulation };
        _tree.Root.AddChild(view);
        return view;
    }

    [Fact]
    public void GameScriptsRunInsideARunningSimulationAndNotOutside()
    {
        var outside = new ScriptCounter { Name = "Outside" };
        _tree.Root.AddChild(outside);
        var view = View(ViewportSimulation.Running);
        var inside = new ScriptCounter { Name = "Inside" };
        view.AddChild(inside);
        Run(3);

        Assert.True(inside.IsSimulated);
        Assert.False(outside.IsSimulated);
        Assert.Equal(ViewportSimulation.Running, inside.ResolvedSimulation);
        Assert.Equal(1, inside.ReadyCount);
        Assert.Equal(3, inside.Processed);
        Assert.Equal(3, inside.PhysicsProcessed);
        Assert.Equal(0, outside.ReadyCount); // edit mode: game scripts' lifecycle falls back to the engine base
        Assert.Equal(0, outside.Processed);
        Assert.Equal(0, outside.PhysicsProcessed);
        Assert.True(_tree.IsSimulating);

        _tree.PushInput(new InputEventKey { Key = Key.Space, Pressed = true });
        Assert.Equal(0, inside.Inputs); // input never reaches a simulation

        view.Free();
        Assert.False(_tree.IsSimulating);
    }

    [Fact]
    public void PhysicsStepsOnlyTheRunningSimulationsSpaceAndPausedStopsIt()
    {
        var edited = new SubViewport { Name = "Edited" };
        _tree.Root.AddChild(edited);
        var authored = Crate("Authored");
        edited.AddChild(authored);
        var view = View(ViewportSimulation.Running);
        var falling = Crate("Falling");
        view.AddChild(falling);
        Run(30);

        Assert.Equal(5f, authored.Position.Y); // the edited world never steps
        Assert.True(falling.Position.Y < 4.5f, $"the simulated crate fell to {falling.Position.Y}");

        view.Simulation = ViewportSimulation.Paused;
        Assert.Equal(ViewportSimulation.Paused, falling.ResolvedSimulation);
        var held = falling.Position.Y;
        Run(20);
        Assert.Equal(held, falling.Position.Y, 4);

        view.Simulation = ViewportSimulation.Running;
        Run(10);
        Assert.True(falling.Position.Y < held);
    }

    [Fact]
    public void ASimulatedExceptionIsCaughtAndRecordedButAnEditorOneStillEscapes()
    {
        var view = View(ViewportSimulation.Running);
        view.AddChild(new Thrower { Name = "Bad" });
        Run(2); // does not throw
        Assert.IsType<InvalidOperationException>(_tree.SimulationFault);
        _tree.ClearSimulationFault();
        Assert.Null(_tree.SimulationFault);
        view.Free();

        _tree.Root.AddChild(new ToolThrower { Name = "Tool" });
        Assert.Throws<InvalidOperationException>(() => Run(1));
    }

    [Fact]
    public void NetworkingIsRefusedWhileSimulating()
    {
        var api = MultiplayerApi.Attach(_tree);
        View(ViewportSimulation.Running);
        api.Host(0, 4);
        Assert.Equal(MultiplayerMode.Offline, api.Mode);
        Assert.False(api.TryConnect("enet:127.0.0.1:1"));
    }

    [Fact]
    public void TheGeneratorRecordsHandleHints()
    {
        var info = TypeRegistry.GetRequired(typeof(HintedNode));
        Assert.Equal(HandleHint.Radius, info.FindProperty("Radius")!.Hints.Handle);
        Assert.Equal(HandleHint.Length, info.FindProperty("Reach")!.Hints.Handle);
        Assert.Equal(HandleHint.None, info.FindProperty("Plain")!.Hints.Handle);
        var node = new HintedNode();
        Assert.True(info.FindProperty("Radius")!.TryGetFloat(node, out var radius));
        Assert.Equal(3f, radius);
        Assert.True(info.FindProperty("Radius")!.TrySetFloat(node, 7f));
        Assert.Equal(7f, node.Radius);
        Assert.False(info.FindProperty("Count")!.TryGetFloat(node, out _));
        node.Free();
    }
}

/// <summary>A game script (this test assembly is the "game") counting its callbacks.</summary>
public class ScriptCounter : Node
{
    public int ReadyCount { get; private set; }
    public int Processed { get; private set; }
    public int PhysicsProcessed { get; private set; }
    public int Inputs { get; private set; }

    protected override void OnReady() => ReadyCount++;

    protected override void OnProcess(in GameTime gameTime) => Processed++;

    protected override void OnPhysicsProcess(float delta) => PhysicsProcessed++;

    protected override void OnInput(InputEvent inputEvent) => Inputs++;
}

/// <summary>Game code that fails every frame.</summary>
public class Thrower : Node
{
    protected override void OnProcess(in GameTime gameTime) => throw new InvalidOperationException("game bug");
}

/// <summary>Editor ([Tool]) code that fails: not simulated, so it escapes as before.</summary>
[Tool]
public class ToolThrower : Node
{
    protected override void OnProcess(in GameTime gameTime) => throw new InvalidOperationException("editor bug");
}

/// <summary>[Export(Handle = …)] floats (G7.3), recorded by the generator.</summary>
public class HintedNode : Node3D
{
    [Export(Handle = HandleHint.Radius)]
    public float Radius { get; set; } = 3f;

    [Export(Range = "0,10", Handle = HandleHint.Length)]
    public float Reach { get; set; } = 1f;

    [Export]
    public float Plain { get; set; }

    [Export]
    public int Count { get; set; }
}
