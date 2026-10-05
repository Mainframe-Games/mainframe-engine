using System.Numerics;
using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>The Physics 3D scene's controls: collision-shape overlay, crate count, rain and reset.</summary>
public sealed class Physics3DPanel : UiDocument
{
    private RmlDataModel? _model;
    private Dropper3D? _crates;
    private PhysicsServer3D? _physics;
    private bool _enterDebugDraw;

    public Physics3DPanel()
    {
        Source = "Content/Physics/panel3d.rml";
        AutoFocus = false;
    }

    protected override void OnReady()
    {
        var crates = _crates = Parent!.Parent!.GetNode<Dropper3D>("Crates");
        // The collision-shape overlay is server-global: remember it so leaving this scene restores the other scenes' look.
        _physics = Tree?.Servers.Get<PhysicsServer3D>();
        _enterDebugDraw = _physics?.DebugDrawEnabled ?? false;
        _model = CreateDataModel("physics3d")
            .Bind("shapes", this, static d => d._physics?.DebugDrawEnabled ?? false,
                static (d, v) => { if (d._physics is { } p) p.DebugDrawEnabled = v; })
            .Bind("count", this, static d => d._crates?.Count ?? 0)
            .Event("reset", () => crates.ResetBodies())
            .Event("rain", () =>
            {
                for (var i = 0; i < 10; i++)
                    crates.Drop(new Vector3(Random.Shared.NextSingle() * 6 - 3, 8 + i, Random.Shared.NextSingle() * 4 - 2));
            });
    }

    protected override void OnProcess(in GameTime gameTime) => _model?.Dirty("count");

    protected override void OnExitTree()
    {
        if (_physics is { } p)
            p.DebugDrawEnabled = _enterDebugDraw;
        _physics = null;
        _crates = null;
        base.OnExitTree();
    }
}
