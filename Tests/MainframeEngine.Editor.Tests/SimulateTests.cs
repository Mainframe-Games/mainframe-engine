using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>G7.5: game code that fails while it simulates (in edit mode it never processes).</summary>
public class FaultyNode : Node3D
{
    protected override void OnProcess(in GameTime gameTime) => throw new InvalidOperationException("game bug while simulating");
}

/// <summary>
/// G7.5 Simulate: physics runs on a copy in a twin view, Stop gives the authored scene back byte-identical, edits wait,
/// the inspector shows the copy live and read-only, Keep Transforms copies transforms back as one undo step, and a game
/// exception or a tab switch stops it.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class SimulateTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private (RigidBody3D Crate, StaticBody3D Floor) Crates()
    {
        var scene = _editor.Scene;
        var floor = new StaticBody3D { Name = "Floor", Position = new Vector3(0, -0.5f, 0) };
        floor.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = new Vector3(20, 1, 20) } });
        var crate = new RigidBody3D { Name = "Crate", Position = new Vector3(0, 4, 0) };
        crate.AddChild(new CollisionShape3D { Name = "Shape", Shape = new BoxShape3D { Size = Vector3.One } });
        scene.AddNode(floor, scene.Root);
        scene.AddNode(crate, scene.Root);
        foreach (var node in (Node[])[floor, crate])
            foreach (var child in node.Children)
                child.Owner = scene.Root;
        scene.Selection.Set(crate);
        _editor.Tick();
        return (crate, floor);
    }

    [Fact]
    public void ACopyFallsAndStopGivesTheAuthoredSceneBack()
    {
        var (crate, _) = Crates();
        var scene = _editor.Scene;
        var before = SceneSaver.ToJson(scene.Root);
        var history = scene.History.Actions.Count;

        Assert.True(W.Commands.Execute("simulate.toggle"));
        Assert.True(W.Simulation.IsActive);
        Assert.Same(W.Simulation.Viewport, W.Viewport.ViewOf(scene));
        Assert.Equal(SubViewportUpdateMode.Disabled, scene.Viewport.UpdateMode);
        var copy = Assert.IsType<RigidBody3D>(W.Simulation.CounterpartOf(crate));
        Assert.Same(crate, W.Simulation.EditedFor(copy));
        Assert.True(copy.IsSimulated);
        _editor.Tick(40);

        Assert.True(copy.Position.Y < 3.5f, $"the simulated crate is at {copy.Position.Y}");
        Assert.Equal(4f, crate.Position.Y); // the edited crate never moves

        W.Commands.Execute("simulate.toggle");
        _editor.Tick();
        Assert.False(W.Simulation.IsActive);
        Assert.True(copy.IsFreed);
        Assert.Same(scene.Viewport, W.Viewport.ViewOf(scene));
        Assert.Equal(SubViewportUpdateMode.Always, scene.Viewport.UpdateMode);
        Assert.Equal(before, SceneSaver.ToJson(scene.Root)); // byte-identical
        Assert.Equal(history, scene.History.Actions.Count);
        Assert.False(_editor.Tree.IsSimulating);
    }

    [Fact]
    public void EditsWaitAndTheInspectorShowsTheCopyLiveAndReadOnly()
    {
        var (crate, _) = Crates();
        var scene = _editor.Scene;
        W.Simulation.StartNow(scene);
        _editor.Tick(2);
        var copy = W.Simulation.CounterpartOf(crate)!;
        Assert.Same(copy, W.Inspector.Rows[0].Target);
        Assert.True(W.Inspector.Document.GetElementById("inspector-body").IsClassSet("simulating"));
        Assert.False(W.Inspector.Document.GetElementById("sim-keep").IsNull);

        var history = scene.History.Actions.Count;
        Assert.False(W.Commands.Execute("edit.delete"));
        Assert.False(scene.Duplicate([crate]).Any());
        scene.SetProperty(crate, Serialization.TypeRegistry.GetRequired(typeof(RigidBody3D)).FindProperty("Mass")!, 9f);
        Assert.Equal(history, scene.History.Actions.Count);
        Assert.False(crate.IsFreed);
        Assert.NotEqual(9f, crate.Mass);

        W.Gizmo.Mode = GizmoMode.Translate;
        Assert.Empty(W.Viewport.GizmoTargets(scene)); // no gizmo, handles or box while simulating
        W.Simulation.Stop();
        _editor.Tick();
        Assert.Same(crate, W.Inspector.Rows[0].Target);
        Assert.NotEmpty(W.Viewport.GizmoTargets(scene));
    }

    [Fact]
    public void KeepTransformsCopiesTheSelectedTransformsAsOneUndoStep()
    {
        var (crate, _) = Crates();
        var scene = _editor.Scene;
        W.Simulation.StartNow(scene);
        _editor.Tick(60);
        var landed = ((Node3D)W.Simulation.CounterpartOf(crate)!).Position;
        Assert.True(landed.Y < 3f);

        W.Inspector.RunHeaderAction("sim-keep");
        Assert.False(W.Simulation.IsActive);
        Assert.Equal(landed, crate.Position);
        Assert.Equal("Keep Transform of Crate", scene.History.UndoAction!.Name);
        scene.History.Undo();
        Assert.Equal(new Vector3(0, 4, 0), crate.Position);
    }

    [Fact]
    public void PauseFreezesTheCopy()
    {
        var (crate, _) = Crates();
        W.Simulation.StartNow(_editor.Scene);
        _editor.Tick(10);
        W.Commands.Execute("simulate.pause");
        Assert.True(W.Simulation.IsPaused);
        var copy = (Node3D)W.Simulation.CounterpartOf(crate)!;
        var y = copy.Position.Y;
        _editor.Tick(20);
        Assert.Equal(y, copy.Position.Y, 4);
        W.Commands.Execute("simulate.pause");
        _editor.Tick(10);
        Assert.True(copy.Position.Y < y);
    }

    [Fact]
    public void AGameExceptionStopsTheSimulationWithAnError()
    {
        var scene = _editor.Scene;
        scene.AddNode(new FaultyNode { Name = "Faulty" }, scene.Root);
        _editor.Tick(3); // edit mode: it never processes
        W.Simulation.StartNow(scene);
        _editor.Tick(3);
        Assert.False(W.Simulation.IsActive);
        Assert.Contains(W.Output.Messages, e => e.Level == OutputLevel.Error && e.Text.Contains("game bug while simulating", StringComparison.Ordinal));
        Assert.Contains(W.Output.Messages, e => e.Text.Contains("Simulation stopped by InvalidOperationException", StringComparison.Ordinal));
        Assert.Null(_editor.Tree.SimulationFault);
    }

    [Fact]
    public void SwitchingTabsStopsItAndAltF6Toggles()
    {
        Crates();
        var first = _editor.Scene;
        Assert.Equal("simulate.toggle", EditorWorkspace.ProjectShortcutFor(Key.F6, EditorModifiers.Alt));
        _editor.Key(Key.F6, Key.AltLeft);
        Assert.True(W.Simulation.IsSimulating(first));
        W.Session.NewScene();
        _editor.Tick(2);
        Assert.False(W.Simulation.IsActive);
        Assert.Contains(W.Output.Messages, e => e.Text.Contains("its tab is no longer active", StringComparison.Ordinal));
    }
}
