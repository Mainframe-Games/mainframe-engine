using System.Diagnostics;
using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>2D joints on Box2D (ADR 0201): creation order, lifecycle, breaking, live properties and a soft-body lattice.</summary>
[Collection(nameof(SerialBox2D))]
public sealed class Physics2DJointTests : IDisposable
{
    private readonly PhysicsHarness2D _h = new();

    public void Dispose() => _h.Dispose();

    private T AddJoint<T>(T joint, Vector2 position, string? nodeA, string? nodeB) where T : Joint2D
    {
        joint.Position = position;
        _h.Add(joint);
        joint.NodeA = nodeA is null ? default(NodePath) : "../" + nodeA;
        joint.NodeB = nodeB is null ? default(NodePath) : "../" + nodeB;
        return joint;
    }

    [Fact]
    public void PinnedToTheWorldTheBoxSwingsAtTheSameDistance()
    {
        var box = _h.AddBox(new Vector2(100, 0), size: 20);
        var joint = AddJoint(new PinJoint2D { Name = "Pin" }, Vector2.Zero, nodeA: null, nodeB: "Box");
        var farthest = 0f;
        for (var i = 0; i < 90; i++)
        {
            _h.Run(1);
            farthest = MathF.Max(farthest, box.Position.Length());
            Assert.InRange(box.Position.Length(), 97f, 103f);
        }

        Assert.True(joint.IsActive);
        Assert.Null(joint.BodyA); // the world
        Assert.Same(box, joint.BodyB);
        Assert.True(box.Position.Y > 50, $"the box swung down: {box.Position}"); // Y-down gravity
        Assert.True(farthest < 103f);
    }

    [Fact]
    public void PathsSetAfterTheBodiesAreInTheTreeJoinThemAtTheNextStep()
    {
        _h.AddStaticBox(Vector2.Zero, new Vector2(20, 20), name: "Anchor");
        var hanging = _h.AddBox(new Vector2(0, 60), size: 20, name: "Hanging");
        var joint = new PinJoint2D { Name = "Pin" };
        _h.Add(joint);
        _h.Run(1);
        Assert.False(joint.IsActive); // no paths yet: nothing joined, nothing logged

        // Snake Oil's order: the joint and both bodies are in the tree, then the paths are set.
        joint.NodeA = "../Anchor";
        joint.NodeB = "../Hanging";
        _h.RunSeconds(1f);
        Assert.True(joint.IsActive);
        Assert.InRange(hanging.Position.Y, 58f, 62f); // held where the joint was (at the anchor), not fallen
        Assert.InRange(joint.GetSeparation(), 0f, 1f);
    }

    [Fact]
    public void FreeingEitherBodyRemovesItsJointsWithoutDanglingHandles()
    {
        _h.AddStaticBox(Vector2.Zero, new Vector2(20, 20), name: "Anchor");
        var b = _h.AddBox(new Vector2(0, 40), size: 20, name: "B");
        var c = _h.AddBox(new Vector2(0, 80), size: 20, name: "C");
        var top = AddJoint(new PinJoint2D { Name = "Top" }, new Vector2(0, 20), "Anchor", "B");
        var middle = AddJoint(new PinJoint2D { Name = "Middle" }, new Vector2(0, 60), "B", "C");
        _h.Run(2);
        Assert.Equal(2, _h.Space.ActiveJointCount);

        b.Free(); // both joints used it
        Assert.False(top.IsActive);
        Assert.False(middle.IsActive);
        Assert.Equal(0, _h.Space.ActiveJointCount);
        _h.RunSeconds(0.5f);
        Assert.True(c.Position.Y > 150, "C falls once nothing holds it");

        middle.Free();
        top.Free();
        Assert.Equal(0, _h.Space.JointCount);
        _h.Run(2);
    }

    [Fact]
    public void ABodyThatLeavesAndReturnsIsJoinedAgainAtTheAuthoredFrame()
    {
        _h.AddStaticBox(Vector2.Zero, new Vector2(20, 20), name: "Anchor");
        var b = _h.AddBox(new Vector2(0, 50), size: 20, name: "B");
        var joint = AddJoint(new PinJoint2D { Name = "Pin" }, new Vector2(0, 25), "Anchor", "B");
        _h.Run(2);
        Assert.True(joint.IsActive);

        _h.Scene.RemoveChild(b);
        Assert.False(joint.IsActive);
        _h.Run(2);
        _h.Scene.AddChild(b);
        _h.RunSeconds(0.5f);
        Assert.True(joint.IsActive);
        Assert.InRange(b.Position.Y, 47f, 53f);
    }

    [Fact]
    public void AJointOverItsBreakForceBreaksOnceAndRebuildRestoresIt()
    {
        var box = _h.AddBox(new Vector2(0, 50), size: 20);
        box.Mass = 10;
        var joint = AddJoint(new PinJoint2D { Name = "Pin" }, Vector2.Zero, nodeA: null, nodeB: "Box");
        joint.BreakForce = 10 * 980 * 0.5f; // half the box's weight (kg·px/s²)
        var broken = 0;
        joint.Broken += () => broken++;
        _h.RunSeconds(1f);

        Assert.Equal(1, broken);
        Assert.True(joint.IsBroken);
        Assert.False(joint.IsActive);
        Assert.True(joint.GetAppliedForce() > joint.BreakForce);
        Assert.True(box.Position.Y > 300, "the box fell");

        box.Teleport(Transform2D.FromTrs(new Vector2(0, 50), 0, Vector2.One));
        box.LinearVelocity = Vector2.Zero;
        joint.BreakForce = 0; // never
        joint.Rebuild();
        _h.RunSeconds(1f);
        Assert.False(joint.IsBroken);
        Assert.True(joint.IsActive);
        Assert.Equal(1, broken);
        Assert.InRange(box.Position.Length(), 47f, 53f);
    }

    [Fact]
    public void LimitsMotorAndSoftnessChangeLive()
    {
        var wheel = _h.AddBox(new Vector2(0, 0), size: 40, name: "Wheel");
        wheel.GravityScale = 0;
        var joint = AddJoint(new PinJoint2D { Name = "Axle", MotorEnabled = true, MotorTargetVelocity = 4f }, Vector2.Zero, null, "Wheel");
        joint.AngularLimitEnabled = true;
        joint.AngularLimitLower = -0.2f;
        joint.AngularLimitUpper = 0.2f;
        _h.RunSeconds(1f);
        Assert.InRange(wheel.Rotation, 0.15f, 0.3f); // driven against the upper limit (Box2D limits are soft: a little past it)

        joint.AngularLimitEnabled = false; // live: no re-creation
        _h.RunSeconds(1f);
        Assert.True(MathF.Abs(wheel.Rotation) > 1f || wheel.AngularVelocity > 3f, $"turning freely: {wheel.Rotation} rad, {wheel.AngularVelocity} rad/s");

        // Softness: a heavy box hangs further below a soft pin than a stiff one.
        var heavy = _h.AddBox(new Vector2(300, 0), size: 20, name: "Heavy");
        heavy.Mass = 50;
        var hook = AddJoint(new PinJoint2D { Name = "Hook" }, new Vector2(300, 0), null, "Heavy");
        _h.RunSeconds(1f);
        var stiff = hook.GetSeparation();
        hook.ConstraintHertz = 1f;
        hook.ConstraintDampingRatio = 1f;
        _h.RunSeconds(2f);
        var soft = hook.GetSeparation();
        Assert.True(soft > stiff * 4 && soft > 1f, $"soft {soft} px vs stiff {stiff} px");
    }

    [Fact]
    public void ADampedSpringPullsTowardItsRestLengthAndNeverPastLength()
    {
        var weight = _h.AddBox(new Vector2(0, 100), size: 20, name: "Weight");
        var spring = AddJoint(new DampedSpringJoint2D { Name = "Spring", Length = 100, RestLength = 40, SpringHertz = 2f, SpringDampingRatio = 0.7f },
            Vector2.Zero, null, "Weight");
        var longest = 0f;
        for (var i = 0; i < 180; i++)
        {
            _h.Run(1);
            longest = MathF.Max(longest, weight.Position.Length());
        }

        Assert.True(spring.IsActive);
        Assert.True(longest <= 102f, $"stretched to {longest} px past its 100 px length");
        // At rest: pulled up from 100 px, hanging below its 40 px rest length under gravity (k = m (2π f)²).
        Assert.InRange(weight.Position.Y, 40f + 980f / MathF.Pow(2 * MathF.PI * 2f, 2) - 4f, 40f + 980f / MathF.Pow(2 * MathF.PI * 2f, 2) + 4f);
    }

    [Fact]
    public void BodiesKeepTheirJointsAcrossModeChanges()
    {
        var a = _h.AddBox(new Vector2(0, 0), size: 20, name: "A");
        var b = _h.AddBox(new Vector2(0, 40), size: 20, name: "B");
        a.GravityScale = b.GravityScale = 0;
        var joint = AddJoint(new PinJoint2D { Name = "Pin" }, new Vector2(0, 20), "A", "B");
        _h.Run(2);
        a.Mode = RigidBodyMode.Kinematic;
        _h.Run(2);
        a.Mode = RigidBodyMode.Dynamic;
        _h.Run(2);
        Assert.True(joint.IsActive);
        Assert.Equal(1, _h.Space.ActiveJointCount);
    }

    [Fact]
    public void ASoftBodyLatticeOfAbout450PinsStaysStableAt120Hz()
    {
        // Buy My Snake Oil's prep bench: ~100 small bodies pinned to their neighbours, physics at 120 ticks.
        _h.Tree.PhysicsTicksPerSecond = 120;
        const int columns = 11, rows = 11;
        const float spacing = 20;
        var bodies = new RigidBody2D[columns, rows];
        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                var body = new RigidBody2D { Name = $"B{x}_{y}", Position = new Vector2(x * spacing, y * spacing), Mass = 0.2f };
                body.AddChild(new CollisionShape2D { Shape = new CircleShape2D { Radius = 6 } });
                bodies[x, y] = _h.Add(body);
            }

        var joints = new List<PinJoint2D>();
        void Pin(string? a, string b, Vector2 at)
        {
            var joint = new PinJoint2D { Name = $"J{joints.Count}", Position = at };
            _h.Add(joint);
            joint.NodeA = a is null ? default(NodePath) : "../" + a;
            joint.NodeB = "../" + b;
            joints.Add(joint);
        }

        for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                var here = bodies[x, y];
                var at = here.Position;
                if (y == 0)
                    Pin(null, here.Name, at); // the top row hangs from the world
                if (x + 1 < columns)
                    Pin(here.Name, bodies[x + 1, y].Name, at + new Vector2(spacing / 2, 0));
                if (y + 1 < rows)
                    Pin(here.Name, bodies[x, y + 1].Name, at + new Vector2(0, spacing / 2));
                if (x + 1 < columns && y + 1 < rows)
                    Pin(here.Name, bodies[x + 1, y + 1].Name, at + new Vector2(spacing / 2, spacing / 2));
                if (x > 0 && y + 1 < rows)
                    Pin(here.Name, bodies[x - 1, y + 1].Name, at + new Vector2(-spacing / 2, spacing / 2));
            }

        Assert.True(joints.Count >= 430, $"{joints.Count} joints");
        var watch = Stopwatch.StartNew();
        _h.Run(480, 1f / 120); // 4 s: settles, hanging from the top row
        Assert.Equal(joints.Count, _h.Space.ActiveJointCount);
        Assert.True(bodies[0, rows - 1].Position.Y > (rows - 1) * spacing, "gravity stretched it a little");

        // A hard yank on a corner (the bench's grab), then let it settle again.
        var corner = bodies[columns - 1, rows - 1];
        for (var i = 0; i < 12; i++)
        {
            corner.LinearVelocity = new Vector2(1500, -1500);
            _h.Run(1, 1f / 120);
        }

        var peak = 0f;
        foreach (var joint in joints)
            peak = MathF.Max(peak, joint.GetSeparation());
        _h.Run(480, 1f / 120);
        watch.Stop();

        var worst = 0f;
        foreach (var joint in joints)
            worst = MathF.Max(worst, joint.GetSeparation());
        var fastest = 0f;
        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);
        foreach (var body in bodies)
        {
            Assert.True(float.IsFinite(body.Position.X) && float.IsFinite(body.Position.Y));
            fastest = MathF.Max(fastest, body.LinearVelocity.Length());
            min = Vector2.Min(min, body.Position);
            max = Vector2.Max(max, body.Position);
        }

        TestContext.Current.SendDiagnosticMessage(
            $"Lattice: {joints.Count} pins, {bodies.Length} bodies, {watch.Elapsed.TotalMilliseconds / 972:F3} ms per 120 Hz step (debug build, " +
            $"{_h.Server.Settings.SubstepCount} substeps); separation during the yank {peak:F2} px, after {worst:F2} px, fastest body {fastest:F2} px/s, size {max - min}");
        Assert.True(peak < 20f, $"the yank tore the lattice: {peak} px");
        Assert.True(worst < 3f, $"a joint stretched {worst} px");
        Assert.True(fastest < 30f, $"still moving at {fastest} px/s 4 s after the yank: not at rest");
        Assert.InRange(max.X - min.X, 200f * 0.9f, 200f * 1.1f); // neither exploded nor collapsed
        Assert.InRange(max.Y - min.Y, 200f * 0.9f, 200f * 1.25f); // hangs, stretching a little under its own weight
    }

    [Fact]
    public void ADisabledJointLeavesBox2DAndComesBackAtItsFrames()
    {
        var box = _h.AddBox(new Vector2(0, 50), size: 20);
        var joint = AddJoint(new PinJoint2D { Name = "Pin" }, Vector2.Zero, nodeA: null, nodeB: "Box");
        _h.Run(2);
        Assert.True(joint.IsActive);

        joint.Enabled = false;
        _h.RunSeconds(0.5f);
        Assert.False(joint.IsActive);
        Assert.True(box.Position.Y > 100, "falls while the pin is off");

        box.Teleport(Transform2D.FromTrs(new Vector2(0, 50), 0, Vector2.One));
        box.LinearVelocity = Vector2.Zero;
        joint.Enabled = true;
        joint.ProcessMode = ProcessMode.Disabled; // a disabled node too
        _h.Run(2);
        Assert.False(joint.IsActive);
        joint.ProcessMode = ProcessMode.Inherit;
        _h.RunSeconds(0.5f);
        Assert.True(joint.IsActive);
        Assert.InRange(box.Position.Length(), 47f, 53f);
    }

    [Fact]
    public void AJointAuthoredDisabledStillJoinsAtItsAuthoredFrames()
    {
        // Review fix 5: the frames of a joint that starts disabled are captured at the authored pose too.
        var box = _h.AddBox(new Vector2(0, 50), size: 20);
        var joint = AddJoint(new PinJoint2D { Name = "Pin", Enabled = false }, Vector2.Zero, nodeA: null, nodeB: "Box");
        _h.RunSeconds(1f);
        Assert.False(joint.IsActive);
        Assert.True(box.Position.Y > 200, "falls while the pin is off");
        joint.Enabled = true;
        _h.RunSeconds(3f);
        Assert.True(joint.IsActive);
        Assert.InRange(box.Position.Length(), 45f, 55f); // hanging 50 px from the pin, as authored
    }

    [Fact]
    public void ASliderMovesAlongItsAxisWithinItsLimits()
    {
        var carriage = _h.AddBox(Vector2.Zero, size: 20, name: "Carriage");
        carriage.GravityScale = 0;
        var slider = AddJoint(new SliderJoint2D { Name = "Slider", UseLimit = true, LowerTranslation = -20, UpperTranslation = 40, MotorEnabled = true, MotorTargetVelocity = 100 },
            Vector2.Zero, null, "Carriage");
        carriage.ApplyImpulse(new Vector2(0, 50));
        carriage.AngularVelocity = 3f;
        _h.RunSeconds(1f);
        Assert.InRange(slider.GetTranslation(), 38f, 42f);
        Assert.InRange(carriage.Position.X, 38f, 42f);
        Assert.InRange(carriage.Position.Y, -1f, 1f);
        Assert.InRange(carriage.Rotation, -0.01f, 0.01f);

        slider.SpringEnabled = true; // live: the spring wins over a motor with no force
        slider.MotorEnabled = false;
        slider.SpringTargetTranslation = -10;
        slider.SpringDampingRatio = 1;
        _h.RunSeconds(2f);
        Assert.InRange(slider.GetTranslation(), -12f, -8f);
    }

    [Fact]
    public void AGrooveHoldsItsBodyOnTheGrooveBetweenItsEnds()
    {
        var bead = _h.AddBox(new Vector2(0, 50), size: 10, name: "Bead");
        var groove = AddJoint(new GrooveJoint2D { Name = "Groove", Length = 100, InitialOffset = 50 }, Vector2.Zero, null, "Bead");
        bead.ApplyImpulse(new Vector2(30, 0)); // sideways: held in the groove
        var lowest = 0f;
        for (var i = 0; i < 90; i++)
        {
            _h.Run(1);
            lowest = MathF.Max(lowest, bead.Position.Y);
            Assert.InRange(bead.Position.X, -1.5f, 1.5f);
        }

        Assert.True(groove.IsActive);
        Assert.InRange(lowest, 95f, 103f); // slid down (gravity is +Y) to the groove's end
        Assert.InRange(bead.Position.Y, 97f, 103f);
    }

    [Fact]
    public void AWeldHoldsAndItsSpringsBend()
    {
        _h.AddStaticBox(Vector2.Zero, new Vector2(20, 20), name: "Post");
        var sign = _h.AddBox(new Vector2(60, 0), size: 40, name: "Sign");
        var weld = AddJoint(new WeldJoint2D { Name = "Weld" }, new Vector2(10, 0), "Post", "Sign");
        _h.RunSeconds(1f);
        Assert.InRange(sign.Position.Y, -1f, 1.5f);
        Assert.InRange(sign.Rotation, -0.02f, 0.02f);

        weld.AngularHertz = 1f; // live: a bendy sign sags
        weld.AngularDampingRatio = 1f;
        _h.RunSeconds(2f);
        Assert.True(sign.Position.Y > 5f, $"sagged: {sign.Position}");
        Assert.True(weld.IsActive);
    }

    [Fact]
    public void AWheelDrivesAndRidesItsSuspension()
    {
        _h.AddStaticBox(Vector2.Zero, new Vector2(40, 10), name: "Chassis");
        var wheel = new RigidBody2D { Name = "Wheel", Position = new Vector2(0, 40) };
        wheel.AddChild(new CollisionShape2D { Shape = new CircleShape2D { Radius = 15 } });
        _h.Add(wheel);
        // X of the joint points up the suspension (−90°): the wheel hangs on its spring below the chassis.
        var axle = AddJoint(new WheelJoint2D { Name = "Axle", Rotation = -MathF.PI / 2, MotorEnabled = true, MotorTargetVelocity = 5, SpringHertz = 3 },
            new Vector2(0, 40), "Chassis", "Wheel");
        _h.RunSeconds(2f);
        Assert.True(axle.IsActive);
        Assert.InRange(wheel.AngularVelocity, 4.9f, 5.1f);
        Assert.InRange(wheel.Position.X, -1f, 1f);
        Assert.True(wheel.Position.Y > 40.5f, $"sags on its spring: {wheel.Position}");

        axle.UseLimit = true; // live: the travel stops 5 px below
        axle.LowerTranslation = -5;
        axle.UpperTranslation = 5;
        axle.SpringHertz = 0.5f;
        _h.RunSeconds(2f);
        Assert.InRange(wheel.Position.Y, 40f, 46f);
    }

    [Fact]
    public void APinSpringSettlesAtItsTargetAngle()
    {
        var flap = _h.AddBox(new Vector2(40, 0), size: 20, name: "Flap");
        flap.GravityScale = 0;
        var pin = AddJoint(new PinJoint2D { Name = "Pin", SpringEnabled = true, SpringHertz = 4, SpringDampingRatio = 1, SpringTargetAngle = 0.5f },
            Vector2.Zero, null, "Flap");
        // Box2D's revolute spring uses the bodies' inertia about their centres, so an off-centre flap rings longer than
        // the nominal 4 Hz, ratio 1.
        _h.RunSeconds(5f);
        Assert.True(MathF.Abs(pin.GetAngle() - 0.5f) < 0.02f && MathF.Abs(flap.Rotation - 0.5f) < 0.02f, $"angle {pin.GetAngle()}, rotation {flap.Rotation}");
    }

    [Fact]
    public void NewJointTypesRoundTripThroughScenes()
    {
        var root = new Node2D { Name = "Root" };
        root.AddChild(new SliderJoint2D { Name = "Slider", Enabled = false, UseLimit = true, LowerTranslation = -5, UpperTranslation = 7, MotorMaxForce = 3, SpringTargetTranslation = 2 });
        root.AddChild(new GrooveJoint2D { Name = "Groove", Length = 80, InitialOffset = 10 });
        root.AddChild(new WeldJoint2D { Name = "Weld", LinearHertz = 2, AngularHertz = 3, LinearDampingRatio = 0.2f, AngularDampingRatio = 0.3f });
        root.AddChild(new WheelJoint2D { Name = "Wheel", SpringEnabled = false, SpringHertz = 4, UseLimit = true, MotorEnabled = true, MotorTargetVelocity = 6 });
        root.AddChild(new PinJoint2D { Name = "Pin", SpringEnabled = true, SpringHertz = 5, SpringTargetAngle = 0.25f });
        foreach (var child in root.Children)
            child.Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();
        var slider = copy.GetNode<SliderJoint2D>("Slider");
        Assert.Equal((false, true, -5f, 7f, 3f, 2f), (slider.Enabled, slider.UseLimit, slider.LowerTranslation, slider.UpperTranslation, slider.MotorMaxForce,
            slider.SpringTargetTranslation));
        var groove = copy.GetNode<GrooveJoint2D>("Groove");
        Assert.Equal((80f, 10f), (groove.Length, groove.InitialOffset));
        var weld = copy.GetNode<WeldJoint2D>("Weld");
        Assert.Equal((2f, 3f, 0.2f, 0.3f), (weld.LinearHertz, weld.AngularHertz, weld.LinearDampingRatio, weld.AngularDampingRatio));
        var wheel = copy.GetNode<WheelJoint2D>("Wheel");
        Assert.Equal((false, 4f, true, true, 6f), (wheel.SpringEnabled, wheel.SpringHertz, wheel.UseLimit, wheel.MotorEnabled, wheel.MotorTargetVelocity));
        var pin = copy.GetNode<PinJoint2D>("Pin");
        Assert.Equal((true, 5f, 0.25f), (pin.SpringEnabled, pin.SpringHertz, pin.SpringTargetAngle));
        root.Free();
        copy.Free();
    }

    [Fact]
    public void JointPropertiesRoundTripThroughScenes()
    {
        var root = new Node2D { Name = "Root" };
        root.AddChild(new PinJoint2D
        {
            Name = "Pin",
            NodeA = "../A",
            NodeB = "../B",
            DisableCollision = false,
            ConstraintHertz = 30,
            ConstraintDampingRatio = 5,
            BreakForce = 1000,
            AngularLimitEnabled = true,
            AngularLimitLower = -0.5f,
            AngularLimitUpper = 0.25f,
            MotorEnabled = true,
            MotorTargetVelocity = 2,
        });
        root.AddChild(new DampedSpringJoint2D { Name = "Spring", Length = 80, RestLength = 30, SpringHertz = 3, SpringDampingRatio = 0.5f });
        foreach (var child in root.Children)
            child.Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();
        var pin = copy.GetNode<PinJoint2D>("Pin");
        Assert.Equal("../A", pin.NodeA.ToString());
        Assert.Equal("../B", pin.NodeB.ToString());
        Assert.False(pin.DisableCollision);
        Assert.Equal((30f, 5f, 1000f), (pin.ConstraintHertz, pin.ConstraintDampingRatio, pin.BreakForce));
        Assert.True(pin.AngularLimitEnabled);
        Assert.Equal((-0.5f, 0.25f), (pin.AngularLimitLower, pin.AngularLimitUpper));
        Assert.Equal((true, 2f), (pin.MotorEnabled, pin.MotorTargetVelocity));
        var spring = copy.GetNode<DampedSpringJoint2D>("Spring");
        Assert.Equal((80f, 30f, 3f, 0.5f), (spring.Length, spring.RestLength, spring.SpringHertz, spring.SpringDampingRatio));
        root.Free();
        copy.Free();
    }
}
