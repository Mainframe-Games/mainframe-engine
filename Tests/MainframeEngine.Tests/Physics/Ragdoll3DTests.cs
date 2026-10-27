using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>Ragdoll3D (ADR 0212): kinematic until simulated, velocity carried into the fall, parts that never collide.</summary>
public sealed class Ragdoll3DTests : IDisposable
{
    private readonly PhysicsHarness3D _h = new();

    public void Dispose() => _h.Dispose();

    /// <summary>A box mannequin standing on the floor: torso, head, arms and legs joined under a Ragdoll3D.</summary>
    private Ragdoll3D AddMannequin(Vector3 at, List<Joint3D>? joints = null)
    {
        var ragdoll = _h.Add(new Ragdoll3D { Name = "Ragdoll", Position = at });
        RigidBody3D Part(string name, Vector3 position, Vector3 size)
        {
            var part = new RigidBody3D { Name = name, Position = position };
            part.AddChild(new CollisionShape3D { Shape = new BoxShape3D { Size = size } });
            ragdoll.AddChild(part);
            return part;
        }

        void Join(Joint3D joint, Vector3 position, string a, string b)
        {
            joint.Position = position;
            joint.NodeA = "../" + a;
            joint.NodeB = "../" + b;
            ragdoll.AddChild(joint);
            joints?.Add(joint);
        }

        var down = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2);
        Part("Torso", new Vector3(0, 1.3f, 0), new Vector3(0.5f, 0.7f, 0.25f));
        Part("Head", new Vector3(0, 1.85f, 0), new Vector3(0.3f));
        Join(new ConeTwistJoint3D { Name = "Neck", SwingSpanDegrees = 30, TwistSpanDegrees = 30, Rotation = down }, new Vector3(0, 1.67f, 0), "Torso", "Head");
        for (var side = -1; side <= 1; side += 2)
        {
            var s = side < 0 ? "L" : "R";
            Part($"Arm{s}", new Vector3(side * 0.36f, 1.35f, 0), new Vector3(0.14f, 0.6f, 0.14f));
            Join(new ConeTwistJoint3D { Name = $"Shoulder{s}", SwingSpanDegrees = 70, TwistSpanDegrees = 30, Rotation = down },
                new Vector3(side * 0.36f, 1.62f, 0), "Torso", $"Arm{s}");
            Part($"Leg{s}", new Vector3(side * 0.14f, 0.5f, 0), new Vector3(0.16f, 0.8f, 0.16f));
            Join(new HingeJoint3D { Name = $"Hip{s}", UseLimit = true, LowerAngleDegrees = -30, UpperAngleDegrees = 80, Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2) },
                new Vector3(side * 0.14f, 0.92f, 0), "Torso", $"Leg{s}");
        }

        return ragdoll;
    }

    [Fact]
    public void PartsHoldStillUntilTheSimulationStartsThenTheMannequinCollapses()
    {
        _h.AddFloor();
        var joints = new List<Joint3D>();
        var ragdoll = AddMannequin(Vector3.Zero, joints);
        var torso = ragdoll.GetNode<RigidBody3D>("Torso");
        var started = 0;
        ragdoll.SimulationStarted += () => started++;
        _h.RunSeconds(1f);
        Assert.InRange(torso.Position.Y, 1.29f, 1.31f); // kinematic: standing
        Assert.All(joints, j => Assert.False(j.IsActive)); // between two kinematic parts: waiting
        Assert.Equal(RigidBodyMode.Dynamic, torso.Mode); // the authored mode is untouched

        ragdoll.ApplyImpulse(torso, new Vector3(0, 0, 3));
        ragdoll.StartSimulation();
        ragdoll.ApplyImpulse(torso, new Vector3(0, 0, 3)); // a shove as it goes
        Assert.Equal(1, started);
        Assert.True(ragdoll.Simulating);
        _h.RunSeconds(3f);
        Assert.All(joints, j => Assert.True(j.IsActive));
        Assert.True(torso.Position.Y < 0.6f, $"collapsed: torso at {torso.Position}");
        foreach (var joint in joints)
            Assert.True(joint.GetSeparation() < 0.03f, $"{joint.Name} separated {joint.GetSeparation()} m");
    }

    [Fact]
    public void KinematicMotionCarriesIntoTheFall()
    {
        var ragdoll = AddMannequin(new Vector3(0, 3, 0));
        var torso = ragdoll.GetNode<RigidBody3D>("Torso");
        _h.Run(2);
        // Carried along by its node at 3 m/s (an animation would do the same), then let go.
        for (var i = 0; i < 20; i++)
        {
            ragdoll.Position += new Vector3(3f * PhysicsHarness3D.Step, 0, 0);
            _h.Run(1);
        }

        ragdoll.StartSimulation();
        _h.Run(1);
        Assert.InRange(torso.LinearVelocity.X, 2.7f, 3.3f);
        var x = torso.Position.X;
        _h.RunSeconds(0.25f);
        Assert.True(torso.Position.X > x + 0.5f, "kept moving along X");
        Assert.True(torso.LinearVelocity.Y < -1f, "and falls");
    }

    [Fact]
    public void StoppingHoldsThePartsAndRestartingKeepsTheirJoints()
    {
        _h.AddFloor();
        var joints = new List<Joint3D>();
        var ragdoll = AddMannequin(Vector3.Zero, joints);
        var stopped = 0;
        ragdoll.SimulationStopped += () => stopped++;
        ragdoll.Simulating = true; // the property starts it too
        _h.RunSeconds(1f);
        ragdoll.StopSimulation();
        Assert.Equal(1, stopped);
        var torso = ragdoll.GetNode<RigidBody3D>("Torso");
        _h.Run(1); // the physics pose replaces the last interpolated one
        var held = torso.Position;
        _h.RunSeconds(1f);
        Assert.InRange(Vector3.Distance(torso.Position, held), 0f, 0.001f);

        ragdoll.Position += new Vector3(0, 2, 0); // lifted while held
        _h.Run(2);
        ragdoll.StartSimulation();
        _h.RunSeconds(2f);
        foreach (var joint in joints)
        {
            Assert.True(joint.IsActive, joint.Name);
            Assert.True(joint.GetSeparation() < 0.03f, $"{joint.Name} separated {joint.GetSeparation()} m");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PartsNeverCollideWithEachOtherWhenExcluded(bool exclude)
    {
        var ragdoll = _h.Add(new Ragdoll3D { Name = "Ragdoll", ExcludePartCollisions = exclude, Simulating = true });
        RigidBody3D Part(string name, Vector3 at)
        {
            var part = new RigidBody3D { Name = name, Position = at, GravityScale = 0, ContactMonitor = true };
            part.AddChild(new CollisionShape3D { Shape = new BoxShape3D() });
            ragdoll.AddChild(part);
            return part;
        }

        var a = Part("A", Vector3.Zero);
        Part("B", new Vector3(0.6f, 0, 0)); // overlapping, not joined
        var touches = 0;
        a.BodyEntered += _ => touches++;
        _h.Run(10);
        if (exclude)
            Assert.Equal(0, touches);
        else
            Assert.True(touches > 0);
    }

    [Fact]
    public void RagdollPropertiesRoundTripThroughScenes()
    {
        var root = new Node3D { Name = "Root" };
        root.AddChild(new Ragdoll3D { Name = "Ragdoll", Simulating = true, ExcludePartCollisions = false });
        root.GetChild(0).Owner = root;
        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();
        var ragdoll = copy.GetNode<Ragdoll3D>("Ragdoll");
        Assert.True(ragdoll.Simulating);
        Assert.False(ragdoll.ExcludePartCollisions);
        root.Free();
        copy.Free();
    }

    [Fact]
    public void ARagdollMovedRightAfterItIsAddedKeepsItsJointsTogether()
    {
        // Review fix 1: the parts are already held kinematic when the game moves the ragdoll to its spawn point, so the
        // move is only a target the next step applies; the joints' frames must come from that pose, not the old one.
        // A whole-body move shifts every part alike, so it shows on a joint to the world (a hook) and on one part moved
        // on its own (as an animation posing the limbs at spawn would).
        _h.AddFloor();
        var joints = new List<Joint3D>();
        var ragdoll = AddMannequin(Vector3.Zero, joints);
        var hook = new PinJoint3D { Name = "Hook", Position = new Vector3(0, 1.67f, 0), NodeB = "../Torso" };
        ragdoll.AddChild(hook);
        joints.Add(hook);
        ragdoll.GlobalPosition = new Vector3(5, 0, 3);
        ragdoll.GetNode<RigidBody3D>("ArmL").RotationDegrees = new Vector3(0, 0, -40);
        _h.Run(2);
        ragdoll.StartSimulation();
        _h.Run(1);
        foreach (var joint in joints)
            Assert.True(joint.GetSeparation() < 0.03f, $"{joint.Name} separated {joint.GetSeparation()} m");
    }

    [Fact]
    public void APartTakenOutOfTheRagdollIsAnOrdinaryBodyAgain()
    {
        // Review fix 3: held kinematic and the exclusion group are the ragdoll's state; a part that leaves it drops both.
        _h.AddFloor();
        var ragdoll = AddMannequin(Vector3.Zero);
        _h.Run(2);
        var arm = ragdoll.GetNode<RigidBody3D>("ArmL");
        Assert.True(arm.HeldKinematic);
        Assert.NotEqual(0, arm.ExclusionGroup);
        var y = arm.GlobalPosition.Y;
        ragdoll.RemoveChild(arm);
        _h.Scene.AddChild(arm);
        Assert.False(arm.HeldKinematic);
        Assert.Equal(0, arm.ExclusionGroup);
        _h.RunSeconds(0.5f);
        Assert.True(arm.GlobalPosition.Y < y - 0.3f, $"the arm falls: {arm.GlobalPosition}");

        _h.Scene.RemoveChild(arm);
        ragdoll.AddChild(arm); // and joins again in the ragdoll's state
        Assert.True(arm.HeldKinematic);
        Assert.NotEqual(0, arm.ExclusionGroup);
    }
}
