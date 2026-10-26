namespace MainframeEngine;

/// <summary>The joint a <see cref="PhysicalBone3D"/> has to the physical bone of its nearest ancestor bone.</summary>
public enum PhysicalBoneJointType
{
    /// <summary>Not joined: the body moves on its own when simulated.</summary>
    None,

    /// <summary>A ball joint (<see cref="PinJoint3D"/>).</summary>
    Pin,

    /// <summary>A swing cone and twist range about the joint's X (<see cref="ConeTwistJoint3D"/>): shoulders, hips, spines.</summary>
    ConeTwist,

    /// <summary>A hinge about the joint's Z with angle limits (<see cref="HingeJoint3D"/>): knees, elbows.</summary>
    Hinge,

    /// <summary>A slider along the joint's X with distance limits (<see cref="SliderJoint3D"/>).</summary>
    Slider,
}

/// <summary>
/// A rigid body that drives one bone of a <see cref="Skeleton3D"/> (Godot 4.3's <c>PhysicalBone3D</c>, ADR 0212). It is a
/// child of a <see cref="PhysicalBoneSimulator3D"/> under the skeleton. While its bone is not simulated it is kinematic
/// and follows the animated bone (<see cref="BodyOffset"/> from it), pushing what it touches; simulated, it falls under
/// physics and writes the bone's pose. Its joint to the physical bone of the nearest ancestor bone is set here
/// (<see cref="JointType"/>, <see cref="JointOffset"/> and the "Joint" group); the simulator creates it.
/// </summary>
[EditorIcon("bone", Family = EditorIconFamily.Physics)]
public sealed class PhysicalBone3D : RigidBody3D
{
    /// <summary>The bone this body drives (by name).</summary>
    [Export]
    public string BoneName { get; set; } = "";

    /// <summary>The body's transform relative to its bone (a capsule's centre and axis along the bone).</summary>
    [Export]
    public Transform3D BodyOffset
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Simulator?.OnBodyOffsetChanged(this);
        }
    } = Transform3D.Identity;

    /// <summary>The joint to the nearest ancestor bone's physical bone. Changing it re-creates the joint.</summary>
    [ExportGroup("Joint")]
    [Export]
    public PhysicalBoneJointType JointType
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Simulator?.OnBoneJointChanged(this);
        }
    } = PhysicalBoneJointType.ConeTwist;

    /// <summary>The joint's frame relative to this body (where the bone starts, its X along the bone for a cone-twist).</summary>
    [Export]
    public Transform3D JointOffset
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Simulator?.OnBoneJointChanged(this);
        }
    } = Transform3D.Identity;

    /// <summary>Cone-twist: how far the bone may swing from its parent's axis, in degrees.</summary>
    [Export(Range = "0,180,0.1")]
    public float SwingSpanDegrees
    {
        get;
        set
        {
            field = value;
            Simulator?.OnBoneJointChanged(this, rebuild: false);
        }
    } = 45f;

    /// <summary>Cone-twist: how far the bone may twist about its axis either way, in degrees.</summary>
    [Export(Range = "0,180,0.1")]
    public float TwistSpanDegrees
    {
        get;
        set
        {
            field = value;
            Simulator?.OnBoneJointChanged(this, rebuild: false);
        }
    } = 30f;

    /// <summary>Hinge: the lowest angle in degrees.</summary>
    [Export(Range = "-180,180,0.1")]
    public float LowerAngleDegrees
    {
        get;
        set
        {
            field = value;
            Simulator?.OnBoneJointChanged(this, rebuild: false);
        }
    } = -90f;

    /// <summary>Hinge: the highest angle in degrees.</summary>
    [Export(Range = "-180,180,0.1")]
    public float UpperAngleDegrees
    {
        get;
        set
        {
            field = value;
            Simulator?.OnBoneJointChanged(this, rebuild: false);
        }
    } = 90f;

    /// <summary>Slider: the lowest position along the axis in metres.</summary>
    [Export(Range = "-100,100,0.01")]
    public float LowerDistance
    {
        get;
        set
        {
            field = value;
            Simulator?.OnBoneJointChanged(this, rebuild: false);
        }
    } = -0.1f;

    /// <summary>Slider: the highest position along the axis in metres.</summary>
    [Export(Range = "-100,100,0.01")]
    public float UpperDistance
    {
        get;
        set
        {
            field = value;
            Simulator?.OnBoneJointChanged(this, rebuild: false);
        }
    } = 0.1f;

    /// <summary>The simulator this bone belongs to (its parent), or null.</summary>
    public PhysicalBoneSimulator3D? Simulator => Parent as PhysicalBoneSimulator3D;

    /// <summary>The joint the simulator created for this bone (an internal node, not saved), or null.</summary>
    public Joint3D? Joint { get; internal set; }

    /// <summary>The bone's index in the skeleton (−1 when it has no such bone).</summary>
    public int BoneIndex { get; internal set; } = -1;

    /// <summary>This bone is simulated (it drives its bone); otherwise it follows the animated bone.</summary>
    public bool IsSimulatingPhysics => !HeldKinematic && Simulator is { IsSimulating: true };

    /// <summary>Creates the internal joint node for <see cref="JointType"/> with the "Joint" group's limits.</summary>
    internal Joint3D? CreateJoint() => JointType switch
    {
        PhysicalBoneJointType.Pin => new PinJoint3D(),
        PhysicalBoneJointType.ConeTwist => new ConeTwistJoint3D { SwingSpanDegrees = SwingSpanDegrees, TwistSpanDegrees = TwistSpanDegrees },
        PhysicalBoneJointType.Hinge => new HingeJoint3D { UseLimit = true, LowerAngleDegrees = LowerAngleDegrees, UpperAngleDegrees = UpperAngleDegrees },
        PhysicalBoneJointType.Slider => new SliderJoint3D { UseLimit = true, LowerDistance = LowerDistance, UpperDistance = UpperDistance },
        _ => null,
    };

    /// <summary>Copies the "Joint" group's limits to the live joint (no re-creation).</summary>
    internal void ApplyJointLimits()
    {
        switch (Joint)
        {
            case ConeTwistJoint3D cone:
                cone.SwingSpanDegrees = SwingSpanDegrees;
                cone.TwistSpanDegrees = TwistSpanDegrees;
                break;
            case HingeJoint3D hinge:
                hinge.LowerAngleDegrees = LowerAngleDegrees;
                hinge.UpperAngleDegrees = UpperAngleDegrees;
                break;
            case SliderJoint3D slider:
                slider.LowerDistance = LowerDistance;
                slider.UpperDistance = UpperDistance;
                break;
        }
    }
}
