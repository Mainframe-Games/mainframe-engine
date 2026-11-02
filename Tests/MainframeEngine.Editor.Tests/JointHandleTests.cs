using System.Numerics;
using MainframeEngine.Tests;
using Silk.NET.Input;

namespace MainframeEngine.Editor.Tests;

/// <summary>
/// G9.7: joint limit handles — where each joint type's handles sit, angles from the mouse ray, snapping, clamping, lower
/// ≤ upper, hollow handles that turn a limit on, and a viewport drag that is one undo entry.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class JointHandleTests : IDisposable
{
    private static readonly Vector2 View = new(800, 600);
    private readonly HeadlessEditor _editor = new();
    private readonly List<EditorHandle> _handles = [];

    public JointHandleTests()
    {
        // Looking along -Z from +Z: the camera's right is +X, its up +Y.
        var camera = _editor.Scene.Camera;
        camera.Pivot = Vector3.Zero;
        camera.Distance = 12f;
        camera.Yaw = 0f;
        camera.Pitch = 0f;
    }

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    private EditorCamera Camera => _editor.Scene.Camera;

    private List<EditorHandle> Handles(Node node, EditorCamera? camera = null)
    {
        _handles.Clear();
        HandleProviders.Find(node.GetType())!.GetHandles(node, camera ?? Camera, View, _handles);
        return _handles;
    }

    // A drag whose mouse ray comes straight down the normal onto the plane point it names.
    private HandleDrag At(Vector3 point, Vector3 normal, bool snap = false, EditorCamera? camera = null) => new()
    {
        RayOrigin = point + normal * 5f,
        RayDirection = -normal,
        Snap = snap,
        SnapStep = 0.5f,
        AngleStep = 15f,
        Camera = camera ?? Camera,
    };

    private static HandleDrag Along(float along, Vector3 axis, Vector3 origin = default, bool snap = false) => new()
    {
        NodeOrigin = origin,
        Axis = axis,
        Along = along,
        Snap = snap,
        SnapStep = 0.5f,
        AngleStep = 15f,
        Camera = new EditorCamera(),
    };

    private static Vector3 Polar(float degrees, float radius = 1f) =>
        new Vector3(MathF.Cos(float.DegreesToRadians(degrees)), MathF.Sin(float.DegreesToRadians(degrees)), 0f) * radius;

    private static float AngleOf(Vector3 p) => float.RadiansToDegrees(MathF.Atan2(p.Y, p.X));

    private Vector2 WindowPoint(Vector3 world)
    {
        var view = W.Viewport.ViewPixels;
        Assert.True(Camera.Project(world, view.X, view.Y, out var pixel));
        var rect = W.Viewport.ViewRect;
        return new Vector2(rect.X, rect.Y) + pixel / W.Viewport.ViewScale;
    }

    [Theory]
    [InlineData(typeof(HingeJoint3D), typeof(HingeJoint3DHandles))]
    [InlineData(typeof(SliderJoint3D), typeof(SliderJoint3DHandles))]
    [InlineData(typeof(ConeTwistJoint3D), typeof(ConeTwistJoint3DHandles))]
    [InlineData(typeof(DistanceJoint3D), typeof(DistanceJoint3DHandles))]
    [InlineData(typeof(PhysicalBone3D), typeof(PhysicalBone3DHandles))]
    [InlineData(typeof(PinJoint2D), typeof(PinJoint2DHandles))]
    [InlineData(typeof(SliderJoint2D), typeof(SliderJoint2DHandles))]
    [InlineData(typeof(WheelJoint2D), typeof(WheelJoint2DHandles))]
    [InlineData(typeof(GrooveJoint2D), typeof(GrooveJoint2DHandles))]
    [InlineData(typeof(DampedSpringJoint2D), typeof(DampedSpringJoint2DHandles))]
    public void EveryLimitedJointTypeHasAProvider(Type node, Type provider) => Assert.IsType(provider, HandleProviders.Find(node));

    [Fact]
    public void JointsWithoutLimitsHaveNoHandles()
    {
        Assert.Null(HandleProviders.Find(typeof(PinJoint3D)));
        Assert.Null(HandleProviders.Find(typeof(WeldJoint3D)));
        Assert.Null(HandleProviders.Find(typeof(WeldJoint2D)));
        Assert.Empty(Handles(new PhysicalBone3D { JointType = PhysicalBoneJointType.Pin }));
    }

    [Fact]
    public void HingeHandlesSitOnTheLimitArcAndAreHollowWhileTheLimitIsOff()
    {
        var hinge = new HingeJoint3D { LowerAngleDegrees = -30f, UpperAngleDegrees = 60f };
        var handles = Handles(hinge);
        Assert.Equal(2, handles.Count);
        Assert.All(handles, h => Assert.Equal(HandleStyle.Hollow, h.Style));
        Assert.Equal(-30f, AngleOf(handles[JointHandles.Lower].Position), 2);
        Assert.Equal(60f, AngleOf(handles[JointHandles.Upper].Position), 2);
        var radius = handles[0].Position.Length();
        Assert.True(radius >= 0.2f);
        Assert.Equal(radius, handles[1].Position.Length(), 3);
        Assert.Equal(0f, Vector3.Dot(handles[1].DragAxis, handles[1].Position), 3); // tangent to the arc
        Assert.Equal("Upper Angle: no limit — drag to set", HandleProviders.Find(typeof(HingeJoint3D))!.Describe(hinge, JointHandles.Upper));

        hinge.UseLimit = true;
        Assert.All(Handles(hinge), h => Assert.Equal(HandleStyle.Square, h.Style));
        Assert.Equal("Upper Angle 60.0°", HandleProviders.Find(typeof(HingeJoint3D))!.Describe(hinge, JointHandles.Upper));
    }

    [Fact]
    public void HingeAnglesComeFromTheMouseRayInTheJointsPlane()
    {
        // Turned a quarter turn about Y: the hinge's plane is world YZ, its X is world -Z and its axis world +X.
        var hinge = new HingeJoint3D { Position = new Vector3(1, 2, 3), RotationDegrees = new Vector3(0, 90, 0) };
        var provider = HandleProviders.Find(typeof(HingeJoint3D))!;
        var frame = JointFrame.Of(hinge.GlobalTransform);
        var at = frame.OnCircle(frame.X, frame.Y, float.DegreesToRadians(40f), 3f);

        provider.Drag(hinge, JointHandles.Upper, At(at, frame.Z));
        Assert.Equal(40f, hinge.UpperAngleDegrees, 1);
        Assert.True(hinge.UseLimit); // a hollow handle's drag turns the limit on

        provider.Drag(hinge, JointHandles.Upper, At(frame.OnCircle(frame.X, frame.Y, float.DegreesToRadians(52f), 1f), frame.Z, snap: true));
        Assert.Equal(45f, hinge.UpperAngleDegrees, 1); // the 15° rotate step

        provider.Drag(hinge, JointHandles.Lower, At(frame.OnCircle(frame.X, frame.Y, float.DegreesToRadians(70f), 1f), frame.Z));
        Assert.Equal(45f, hinge.LowerAngleDegrees, 1); // stops at the upper angle
        Assert.Equal(["LowerAngleDegrees", "UseLimit"], provider.Properties(hinge, JointHandles.Lower).ToArray());
    }

    [Fact]
    public void AnAngleDraggedPastAHalfTurnStopsInsteadOfWrapping()
    {
        var hinge = new HingeJoint3D { UseLimit = true, LowerAngleDegrees = -90f, UpperAngleDegrees = 170f };
        var provider = HandleProviders.Find(typeof(HingeJoint3D))!;
        provider.Drag(hinge, JointHandles.Upper, At(Polar(-170f), Vector3.UnitZ)); // 190°, not −170°
        Assert.Equal(180f, hinge.UpperAngleDegrees, 1);
        provider.Drag(hinge, JointHandles.Upper, At(Polar(150f), Vector3.UnitZ));
        Assert.Equal(150f, hinge.UpperAngleDegrees, 1);
        Assert.Equal(200f, float.RadiansToDegrees(JointHandles.Unwrap(float.DegreesToRadians(-160f), float.DegreesToRadians(170f))), 2);
    }

    [Fact]
    public void ARayAlongTheArcsPlaneFallsBackToTheDraggedPoint()
    {
        var hinge = new HingeJoint3D { UseLimit = true };
        var drag = new HandleDrag { Point = Polar(30f, 2f), RayOrigin = new Vector3(-5, 0, 0), RayDirection = Vector3.UnitX, Camera = Camera };
        HandleProviders.Find(typeof(HingeJoint3D))!.Drag(hinge, JointHandles.Upper, drag);
        Assert.Equal(30f, hinge.UpperAngleDegrees, 1);
    }

    [Fact]
    public void SliderHandlesDragTheDistancesAlongItsX()
    {
        var slider = new SliderJoint3D { Position = new Vector3(0, 1, 0), LowerDistance = -0.5f, UpperDistance = 2f };
        var handles = Handles(slider);
        Assert.Equal(new Vector3(-0.5f, 1, 0), handles[JointHandles.Lower].Position);
        Assert.Equal(new Vector3(2f, 1, 0), handles[JointHandles.Upper].Position);
        Assert.Equal(HandleStyle.Hollow, handles[0].Style);
        var provider = HandleProviders.Find(typeof(SliderJoint3D))!;

        provider.Drag(slider, JointHandles.Upper, Along(1.3f, Vector3.UnitX, slider.Position, snap: true));
        Assert.Equal(1.5f, slider.UpperDistance); // the 0.5 move step
        Assert.True(slider.UseLimit);
        provider.Drag(slider, JointHandles.Lower, Along(3f, Vector3.UnitX, slider.Position));
        Assert.Equal(1.5f, slider.LowerDistance); // stops at the upper distance
        provider.Drag(slider, JointHandles.Upper, Along(5000f, Vector3.UnitX, slider.Position));
        Assert.Equal(1000f, slider.UpperDistance); // the [Export(Range)] clamp
        Assert.Equal("Upper Distance 1000.00 m", provider.Describe(slider, JointHandles.Upper));
    }

    [Fact]
    public void ConeTwistHandlesSitOnTheSwingRimAndTheTwistArc()
    {
        var cone = new ConeTwistJoint3D { SwingSpanDegrees = 30f, TwistSpanDegrees = 180f };
        var camera = new EditorCamera { Yaw = 0f, Pitch = 0f };
        var handles = Handles(cone, camera);
        var frame = JointFrame.Of(cone.GlobalTransform);
        var rim = JointHandles.SwingRim(frame, camera);
        var swing = handles.Single(h => h.Id == JointHandles.Swing);
        var twist = handles.Single(h => h.Id == JointHandles.Twist);
        Assert.Equal(30f, float.RadiansToDegrees(MathF.Atan2(Vector3.Dot(swing.Position, rim), swing.Position.X)), 1);
        Assert.Equal(HandleStyle.Square, swing.Style);
        Assert.Equal(HandleStyle.Hollow, twist.Style); // 180° = free
        var provider = HandleProviders.Find(typeof(ConeTwistJoint3D))!;
        Assert.Equal("Twist Span: free — drag to set", provider.Describe(cone, JointHandles.Twist));

        var normal = Vector3.Normalize(Vector3.Cross(frame.X, rim));
        provider.Drag(cone, JointHandles.Swing, At(frame.X * MathF.Cos(1f) + rim * MathF.Sin(1f), normal, camera: camera));
        Assert.Equal(float.RadiansToDegrees(1f), cone.SwingSpanDegrees, 1);
        provider.Drag(cone, JointHandles.Twist, At(-frame.Y * 0.5f + frame.Z * 0.5f, frame.X, camera: camera)); // 135° from Y
        Assert.Equal(135f, cone.TwistSpanDegrees, 1);
        provider.Drag(cone, JointHandles.Twist, At(frame.Y * 0.5f - frame.Z * 0.5f, frame.X, snap: true, camera: camera)); // −45°: a span of 45°
        Assert.Equal(45f, cone.TwistSpanDegrees, 1);
    }

    [Fact]
    public void DistanceHandlesAreHollowUntilALengthIsSetAndMinStaysUnderMax()
    {
        var joint = new DistanceJoint3D { AnchorB = new Vector3(0, -2, 0) };
        var handles = Handles(joint);
        Assert.Equal(new Vector3(0, -2, 0), handles[JointHandles.Upper].Position); // the authored length
        Assert.Equal(new Vector3(0, -0.5f, 0), handles[JointHandles.Lower].Position);
        Assert.All(handles, h => Assert.Equal(HandleStyle.Hollow, h.Style));
        var provider = HandleProviders.Find(typeof(DistanceJoint3D))!;
        Assert.Equal("Max Length: the authored 2.00 m — drag to set", provider.Describe(joint, JointHandles.Upper));
        Assert.Equal("Min Length: slack — drag to set", provider.Describe(joint, JointHandles.Lower));

        provider.Drag(joint, JointHandles.Upper, Along(3f, -Vector3.UnitY));
        Assert.Equal(3f, joint.MaxLength);
        provider.Drag(joint, JointHandles.Lower, Along(4f, -Vector3.UnitY));
        Assert.Equal(3f, joint.MinLength); // ≤ the max
        provider.Drag(joint, JointHandles.Upper, Along(1f, -Vector3.UnitY));
        Assert.Equal(3f, joint.MaxLength); // ≥ the min
        Assert.All(Handles(joint), h => Assert.Equal(HandleStyle.Square, h.Style));
    }

    [Fact]
    public void PhysicalBoneHandlesFollowItsJointTypeAtItsJointOffset()
    {
        var offset = Transform3D.FromTrs(new Vector3(0, -0.5f, 0), Quaternion.Identity, Vector3.One);
        var bone = new PhysicalBone3D
        {
            Position = new Vector3(0, 2, 0),
            JointOffset = offset,
            JointType = PhysicalBoneJointType.Hinge,
            LowerAngleDegrees = -10f,
            UpperAngleDegrees = 80f
        };
        var provider = HandleProviders.Find(typeof(PhysicalBone3D))!;
        var handles = Handles(bone);
        Assert.Equal(2, handles.Count);
        Assert.Equal(80f, AngleOf(handles[JointHandles.Upper].Position - new Vector3(0, 1.5f, 0)), 2); // at the joint's origin
        Assert.Equal(HandleStyle.Square, handles[0].Style); // a bone's hinge is always limited
        provider.Drag(bone, JointHandles.Upper, At(new Vector3(0, 1.5f, 0) + Polar(30f), Vector3.UnitZ));
        Assert.Equal(30f, bone.UpperAngleDegrees, 1);
        Assert.Equal(["UpperAngleDegrees"], provider.Properties(bone, JointHandles.Upper).ToArray());

        bone.JointType = PhysicalBoneJointType.Slider;
        Assert.Equal(["LowerDistance"], provider.Properties(bone, JointHandles.Lower).ToArray());
        provider.Drag(bone, JointHandles.Lower, Along(-0.25f, Vector3.UnitX, bone.Position));
        Assert.Equal(-0.25f, bone.LowerDistance, 3); // measured from the joint's origin, not the body's

        bone.JointType = PhysicalBoneJointType.ConeTwist;
        Assert.Equal([JointHandles.Swing, JointHandles.Twist], Handles(bone).Select(h => h.Id));
        Assert.Equal("Swing Span 45.0°", provider.Describe(bone, JointHandles.Swing));
    }

    [Fact]
    public void TwoDimensionalJointsDragInPixelsInTheirOwnFrame()
    {
        var camera = new EditorCamera { Is2D = true };
        var pin = new PinJoint2D { Position = new Vector2(100, 50), AngularLimitLower = -0.5f, AngularLimitUpper = 0.5f };
        var handles = Handles(pin, camera);
        Assert.Equal(HandleStyle.Hollow, handles[0].Style);
        Assert.Equal(0f, handles[0].Position.Z);
        var provider = HandleProviders.Find(typeof(PinJoint2D))!;
        var ray = new HandleDrag { RayOrigin = new Vector3(100, 50, -10f) + Polar(60f, 30f), RayDirection = Vector3.UnitZ, Camera = camera };
        provider.Drag(pin, JointHandles.Upper, ray);
        Assert.Equal(float.DegreesToRadians(60f), pin.AngularLimitUpper, 3); // +Y is down: angles turn clockwise on screen
        Assert.True(pin.AngularLimitEnabled);
        Assert.Equal("Upper Angle 60.0°", provider.Describe(pin, JointHandles.Upper));

        var slider = new SliderJoint2D { Position = new Vector2(10, 0), Rotation = MathF.PI / 2, LowerTranslation = -20f, UpperTranslation = 40f };
        handles = Handles(slider, camera);
        Assert.True(Vector3.Distance(new Vector3(10, 40, 0), handles[JointHandles.Upper].Position) < 1e-3f); // its X is world +Y
        HandleProviders.Find(typeof(SliderJoint2D))!.Drag(slider, JointHandles.Upper, Along(64f, Vector3.UnitY, new Vector3(10, 0, 0)));
        Assert.Equal(64f, slider.UpperTranslation, 3);
        Assert.True(slider.UseLimit);

        var wheel = new WheelJoint2D { UseLimit = true, LowerTranslation = -5f, UpperTranslation = 5f };
        HandleProviders.Find(typeof(WheelJoint2D))!.Drag(wheel, JointHandles.Lower, Along(10f, Vector3.UnitX));
        Assert.Equal(5f, wheel.LowerTranslation); // stops at the upper translation

        var groove = new GrooveJoint2D { Length = 50f };
        handles = Handles(groove, camera);
        Assert.Equal(new Vector3(0, 50, 0), Assert.Single(handles).Position); // along +Y (down)
        HandleProviders.Find(typeof(GrooveJoint2D))!.Drag(groove, JointHandles.Length, Along(-30f, Vector3.UnitY));
        Assert.Equal(1f, groove.Length); // the [Export(Range = "1,…")] clamp

        var spring = new DampedSpringJoint2D { Length = 50f };
        HandleProviders.Find(typeof(DampedSpringJoint2D))!.Drag(spring, JointHandles.Length, Along(80f, Vector3.UnitY));
        Assert.Equal(80f, spring.Length);
        Assert.Equal("Length 80.00 px", HandleProviders.Find(typeof(DampedSpringJoint2D))!.Describe(spring, JointHandles.Length));
    }

    [Fact]
    public void DraggingAHingeLimitInTheViewportIsOneUndoEntryAndEscapeRestoresIt()
    {
        var hinge = new HingeJoint3D { Name = "Hinge" };
        _editor.Scene.AddNode(hinge, _editor.Scene.Root);
        _editor.Scene.Selection.Set(hinge);
        _editor.Tick();
        W.Viewport.CollectHandles(_editor.Scene);
        Assert.Equal(2, W.Viewport.Handles.Count);
        var upper = W.Viewport.Handles[JointHandles.Upper];
        Assert.Same(hinge, W.Viewport.HandleNode(JointHandles.Upper));
        var radius = upper.Position.Length();
        var history = _editor.Scene.History.Actions.Count;

        var from = WindowPoint(upper.Position);
        var to = WindowPoint(Polar(45f, radius));
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = from });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = from });
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = to });
        _editor.Tick();
        Assert.True(W.Viewport.IsDraggingHandle);
        Assert.Equal("Upper Angle 45.0°", W.ViewportPanel.HandleLabel);
        _editor.Tree.PushInput(new InputEventKey { Key = Key.Escape, Pressed = true });
        _editor.Tick();
        Assert.Equal(90f, hinge.UpperAngleDegrees, 2);
        Assert.False(hinge.UseLimit);
        Assert.Equal(history, _editor.Scene.History.Actions.Count);

        _editor.Tree.PushInput(new InputEventMouseMotion { Position = from });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = true, Position = from });
        _editor.Tree.PushInput(new InputEventMouseMotion { Position = to });
        _editor.Tree.PushInput(new InputEventMouseButton { Button = MouseButton.Left, Pressed = false, Position = to });
        _editor.Tick();
        Assert.Equal(45f, hinge.UpperAngleDegrees, 0);
        Assert.True(hinge.UseLimit);
        Assert.Equal(history + 1, _editor.Scene.History.Actions.Count);
        Assert.Equal("Set Upper Angle Degrees on Hinge", _editor.Scene.History.UndoAction!.Name);
        _editor.Scene.History.Undo();
        Assert.Equal(90f, hinge.UpperAngleDegrees, 2);
        Assert.False(hinge.UseLimit);
    }

    [Fact]
    public void JointHandlesAllocateNothingPerFrame()
    {
        var lines = new DebugLines();
        Node[] nodes =
        [
            new HingeJoint3D(), new SliderJoint3D(), new ConeTwistJoint3D(), new DistanceJoint3D(),
            new PhysicalBone3D { JointType = PhysicalBoneJointType.ConeTwist }, new PinJoint2D(), new SliderJoint2D(), new GrooveJoint2D(),
        ];
        var providers = nodes.Select(n => HandleProviders.Find(n.GetType())!).ToArray();
        void Frame()
        {
            for (var i = 0; i < nodes.Length; i++)
            {
                _handles.Clear();
                providers[i].GetHandles(nodes[i], Camera, View, _handles);
                providers[i].Draw(nodes[i], Camera, View, lines);
            }

            lines.Clear();
        }

        Frame();
        Assert.Equal(0, AllocationGate.SmallestWindow(Frame));
        foreach (var node in nodes)
            node.Dispose();
    }
}
