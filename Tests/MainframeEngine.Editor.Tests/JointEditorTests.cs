using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Editor.Tests;

/// <summary>ADR 0211 in the editor: joint warnings, body-only path pickers, the Rebuild and Swap A/B buttons.</summary>
[Collection(nameof(SerialEditor))]
public sealed class JointEditorTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    [Fact]
    public void JointsWarnWhenTheirPathsNameNoBodies()
    {
        var root = new Node3D { Name = "Root" };
        var body = new RigidBody3D { Name = "Body" };
        root.AddChild(body);
        root.AddChild(new Node3D { Name = "Plain" });

        var empty = new HingeJoint3D { Name = "Empty" };
        root.AddChild(empty);
        Assert.Contains("NodeA and NodeB are empty", NodeWarnings.For(empty));

        var missing = new PinJoint3D { Name = "Missing", NodeA = "../Nowhere", NodeB = "../Body" };
        root.AddChild(missing);
        Assert.Contains("NodeA '../Nowhere' does not point at a node", NodeWarnings.For(missing));

        var plain = new PinJoint3D { Name = "Plain3D", NodeA = "../Plain" };
        root.AddChild(plain);
        Assert.Contains("is a Node3D, not a PhysicsBody3D", NodeWarnings.For(plain));

        var same = new WeldJoint3D { Name = "Same", NodeA = "../Body", NodeB = "../Body" };
        root.AddChild(same);
        Assert.Contains("the same body", NodeWarnings.For(same));

        var world = new PinJoint3D { Name = "World", NodeB = "../Body" }; // an empty path is the world: fine
        root.AddChild(world);
        Assert.Null(NodeWarnings.For(world));

        var root2D = new Node2D { Name = "Root2D" };
        root2D.AddChild(new RigidBody2D { Name = "Body" });
        var groove = new GrooveJoint2D { Name = "Groove", NodeA = "../Body", NodeB = "../Wheel" };
        root2D.AddChild(groove);
        Assert.Contains("NodeB '../Wheel' does not point at a node", NodeWarnings.For(groove));
        root.Dispose();
        root2D.Dispose();
    }

    [Fact]
    public void JointPathPickersListOnlyPhysicsBodies()
    {
        Assert.Equal(typeof(PhysicsBody3D), TypeRegistry.GetRequired(typeof(HingeJoint3D)).FindProperty("NodeA")!.Hints.NodeType);
        Assert.Equal(typeof(PhysicsBody3D), TypeRegistry.GetRequired(typeof(ConeTwistJoint3D)).FindProperty("NodeB")!.Hints.NodeType);
        Assert.Equal(typeof(PhysicsBody2D), TypeRegistry.GetRequired(typeof(WheelJoint2D)).FindProperty("NodeA")!.Hints.NodeType);
        Assert.IsType<Joint3DInspector>(CustomInspectors.Find(typeof(SliderJoint3D)));
        Assert.IsType<Joint2DInspector>(CustomInspectors.Find(typeof(PinJoint2D)));
    }

    [Fact]
    public void SwapAAndBIsOneUndoAndRebuildRecapturesTheJoint()
    {
        var frame = new StaticBody3D { Name = "Frame" };
        frame.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
        var door = new RigidBody3D { Name = "Door", Position = new Vector3(1, 0, 0) };
        door.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
        var hinge = new HingeJoint3D { Name = "Hinge", NodeA = "../Frame", NodeB = "../Door" };
        _editor.Scene.AddNode(frame, _editor.Scene.Root);
        _editor.Scene.AddNode(door, _editor.Scene.Root);
        _editor.Scene.AddNode(hinge, _editor.Scene.Root);
        _editor.Tick();

        var header = CustomInspectors.Find(typeof(HingeJoint3D))!.GetHeaderRml(hinge);
        Assert.Contains("joint-rebuild", header);
        Assert.Contains("joint-swap", header);
        _editor.Scene.Selection.Set(hinge);
        _editor.Tick();
        _editor.Workspace.Inspector.RunHeaderAction("joint-swap");
        Assert.Equal(("../Door", "../Frame"), (hinge.NodeA.ToString(), hinge.NodeB.ToString()));
        _editor.Scene.History.Undo();
        Assert.Equal(("../Frame", "../Door"), (hinge.NodeA.ToString(), hinge.NodeB.ToString()));
        _editor.Scene.History.Redo();
        Assert.Equal(("../Door", "../Frame"), (hinge.NodeA.ToString(), hinge.NodeB.ToString()));

        _editor.Workspace.Inspector.RunHeaderAction("joint-rebuild");
        _editor.Tick();
        Assert.False(hinge.IsBroken);

        // The selected joint draws its gizmo into the viewport's overlay (at the node in edit mode).
        var lines = new DebugLines();
        hinge.DrawDebug(lines, Vector4.One);
        Assert.True(lines.LineCount > 10);
    }

    [Fact]
    public void CreatePhysicalSkeletonAddsTheBodiesAsOneUndo()
    {
        var skeleton = new Skeleton3D { Name = "Skeleton" };
        var hips = skeleton.AddBone("Hips", -1, Transform3D.Identity);
        var spine = skeleton.AddBone("Spine", hips, Transform3D.FromTrs(new Vector3(0, 0.3f, 0), Quaternion.Identity, Vector3.One));
        var head = skeleton.AddBone("Head", spine, Transform3D.FromTrs(new Vector3(0, 0.4f, 0), Quaternion.Identity, Vector3.One));
        skeleton.AddBone("HeadTop.End", head, Transform3D.FromTrs(new Vector3(0, 0.02f, 0), Quaternion.Identity, Vector3.One)); // 2 cm: no body
        _editor.Scene.AddNode(skeleton, _editor.Scene.Root);
        _editor.Tick();

        var inspector = CustomInspectors.Find(typeof(Skeleton3D));
        Assert.IsType<Skeleton3DInspector>(inspector);
        Assert.Contains(Skeleton3DInspector.CreateAction, inspector!.GetHeaderRml(skeleton));
        _editor.Scene.Selection.Set(skeleton);
        _editor.Tick();
        _editor.Workspace.Inspector.RunHeaderAction(Skeleton3DInspector.CreateAction);
        var simulator = Assert.Single(skeleton.Children.OfType<PhysicalBoneSimulator3D>());
        Assert.Equal(["Bone_Hips", "Bone_Spine"], simulator.Children.OfType<PhysicalBone3D>().Select(b => b.Name));
        Assert.Same(_editor.Scene.Root, simulator.Owner);
        Assert.All(simulator.Children, b => Assert.Same(_editor.Scene.Root, b.Owner)); // saved with the scene
        Assert.Null(NodeWarnings.For(simulator.GetChild(0)));
        Assert.Null(inspector.GetHeaderRml(skeleton)); // one simulator per skeleton

        _editor.Scene.History.Undo(); // one step removes it all
        Assert.Empty(skeleton.Children.OfType<PhysicalBoneSimulator3D>());
        _editor.Scene.History.Redo();
        Assert.Single(skeleton.Children.OfType<PhysicalBoneSimulator3D>());
    }
}
