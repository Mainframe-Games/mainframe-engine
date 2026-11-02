using System.Numerics;
using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>The Physics 2D scene's controls: collision-shape and joint overlays, body count and reset (bodies and the bridge).</summary>
public sealed class Physics2DPanel : UiDocument
{
    private RmlDataModel? _model;
    private Spawner2D? _bodies;
    private PhysicsServer2D? _physics;
    private bool _enterDebugDraw;
    private bool _enterDebugJoints;

    public Physics2DPanel()
    {
        Source = "Assets/Physics/panel2d.rml";
        AutoFocus = false;
    }

    protected override void OnReady()
    {
        var bodies = _bodies = Parent!.Parent!.GetNode<Spawner2D>("Bodies");
        // The collision-shape overlay is server-global: remember it so leaving this scene restores the other scenes' look.
        _physics = Tree?.Servers.Get<PhysicsServer2D>();
        _enterDebugDraw = _physics?.DebugDrawEnabled ?? false;
        _enterDebugJoints = _physics?.DebugDrawJoints ?? false;
        _model = CreateDataModel("physics2d")
            .Bind("shapes", this, static d => d._physics?.DebugDrawEnabled ?? false,
                static (d, v) => { if (d._physics is { } p) p.DebugDrawEnabled = v; })
            .Bind("joints", this, static d => d._physics?.DebugDrawJoints ?? false,
                static (d, v) => { if (d._physics is { } p) p.DebugDrawJoints = v; })
            .Bind("count", this, static d => d._bodies?.Count ?? 0)
            .Event("reset", () =>
            {
                bodies.ResetBodies();
                ResetBridge(Parent!.Parent!);
                ResetLift(Parent!.Parent!);
            });
    }

    protected override void OnProcess(in GameTime gameTime) => _model?.Dirty("count");

    /// <summary>Puts the bridge's planks back where they were authored and re-creates its pins (clearing broken ones).</summary>
    public static void ResetBridge(Node scene)
    {
        var bridge = scene.GetNode("Bridge");
        for (var i = 0; i < Joints2DBuild.Planks; i++)
        {
            var plank = bridge.GetNode<RigidBody2D>($"Plank{i}");
            plank.Teleport(Joints2DBuild.PlankPose(i));
            plank.LinearVelocity = default;
            plank.AngularVelocity = 0;
        }

        foreach (var child in bridge.Children)
            if (child is Joint2D joint)
                joint.Rebuild();
    }

    /// <summary>Puts the lift's platform and crates back at the bottom of its travel and starts it again.</summary>
    public static void ResetLift(Node scene)
    {
        var lift = scene.GetNode<Lift2D>("Lift");
        lift.Restart();
        Place(lift.GetNode<RigidBody2D>("Platform"), Joints2DBuild.PlatformAt);
        for (var i = 0; i < Joints2DBuild.CratesAt.Length; i++)
            Place(lift.GetNode<RigidBody2D>($"Crate{i}"), Joints2DBuild.CratesAt[i]);

        static void Place(RigidBody2D body, Vector2 at)
        {
            body.Teleport(Transform2D.FromTrs(at, 0, Vector2.One));
            body.LinearVelocity = default;
            body.AngularVelocity = 0;
        }
    }

    protected override void OnExitTree()
    {
        if (_physics is { } p)
        {
            p.DebugDrawEnabled = _enterDebugDraw;
            p.DebugDrawJoints = _enterDebugJoints;
        }
        _physics = null;
        _bodies = null;
        base.OnExitTree();
    }
}
