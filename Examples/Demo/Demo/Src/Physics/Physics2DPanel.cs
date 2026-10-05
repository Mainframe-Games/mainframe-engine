using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>The Physics 2D scene's controls: collision-shape overlay, body count and reset.</summary>
public sealed class Physics2DPanel : UiDocument
{
    private RmlDataModel? _model;
    private Spawner2D? _bodies;
    private PhysicsServer2D? _physics;
    private bool _enterDebugDraw;

    public Physics2DPanel()
    {
        Source = "Content/Physics/panel2d.rml";
        AutoFocus = false;
    }

    protected override void OnReady()
    {
        var bodies = _bodies = Parent!.Parent!.GetNode<Spawner2D>("Bodies");
        // The collision-shape overlay is server-global: remember it so leaving this scene restores the other scenes' look.
        _physics = Tree?.Servers.Get<PhysicsServer2D>();
        _enterDebugDraw = _physics?.DebugDrawEnabled ?? false;
        _model = CreateDataModel("physics2d")
            .Bind("shapes", this, static d => d._physics?.DebugDrawEnabled ?? false,
                static (d, v) => { if (d._physics is { } p) p.DebugDrawEnabled = v; })
            .Bind("count", this, static d => d._bodies?.Count ?? 0)
            .Event("reset", () => bodies.ResetBodies());
    }

    protected override void OnProcess(in GameTime gameTime) => _model?.Dirty("count");

    protected override void OnExitTree()
    {
        if (_physics is { } p)
            p.DebugDrawEnabled = _enterDebugDraw;
        _physics = null;
        _bodies = null;
        base.OnExitTree();
    }
}
