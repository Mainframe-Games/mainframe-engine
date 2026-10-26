using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Drives a <see cref="Skeleton3D"/>'s bones with physics (Godot 4.3's <c>PhysicalBoneSimulator3D</c>, ADR 0212): a child
/// of the skeleton whose <see cref="PhysicalBone3D"/> children are the bodies. On ready it puts each body at its bone's
/// pose, joins it to the physical bone of its nearest ancestor bone (an internal joint node, not saved) and holds every
/// body kinematic, following the animated bones (they push what they touch and are hit by rays).
/// <see cref="PhysicalBonesStartSimulation"/> lets bodies fall; each frame after the animation, the simulated bones'
/// poses are written from their bodies, blended with the animated pose by <see cref="Influence"/>.
/// </summary>
/// <remarks>
/// Bodies of one simulator never collide with each other. The pose write runs at a high
/// <see cref="Node.ProcessPriority"/> (after <see cref="AnimationPlayer"/>, before skinning). Joint limits are measured
/// from the pose the skeleton has when the simulator becomes ready. Nothing happens in the editor's edit mode.
/// </remarks>
[EditorIcon("sitemap", Family = EditorIconFamily.Physics)]
public sealed class PhysicalBoneSimulator3D : Node3D
{
    private static int s_groups;

    private PhysicalBone3D[] _bones = [];
    private PhysicalBone3D?[] _byBone = [];
    private Transform3D[] _inverseOffsets = [];
    private Transform3D[] _readyPoses = [];
    private Transform3D[] _globals = [];

    // Per skeleton bone: the animated pose the blend mixes with, and what the blend last wrote. A bone whose pose still
    // equals what was written was not re-posed by the animation (no track, paused, physics-rate animation at a higher
    // frame rate), so the stored animated pose is used again instead of feeding the blend back into itself.
    private Vector3[] _animatedPositions = [];
    private Quaternion[] _animatedRotations = [];
    private Vector3[] _writtenPositions = [];
    private Quaternion[] _writtenRotations = [];
    private bool[] _written = [];
    private Skeleton3D? _skeleton;
    private int _group;
    private bool _ready;

    public PhysicalBoneSimulator3D()
    {
        ProcessPriority = 1000; // after the AnimationPlayer has posed the skeleton
    }

    /// <summary>How much of the simulated bones' pose comes from physics: 0 = the animation, 1 = the bodies.</summary>
    [Export(Range = "0,1,0.01")]
    public float Influence
    {
        get;
        set => field = Math.Clamp(value, 0f, 1f);
    } = 1f;

    /// <summary>Some bones are simulated (<see cref="PhysicalBonesStartSimulation"/>).</summary>
    public bool IsSimulating { get; private set; }

    /// <summary>The skeleton the simulator drives (its parent), or null.</summary>
    public Skeleton3D? Skeleton => Parent as Skeleton3D;

    /// <summary>The physical bones with a bone in the skeleton, parents first (valid once ready).</summary>
    public ReadOnlySpan<PhysicalBone3D> PhysicalBones => _bones;

    /// <summary>
    /// Lets the bodies of <paramref name="bones"/> (bone names; empty: every bone) fall under physics, each keeping the
    /// velocity of its last kinematic move; their bones then follow them.
    /// </summary>
    public void PhysicalBonesStartSimulation(ReadOnlySpan<string> bones = default)
    {
        foreach (var bone in _bones)
        {
            if (!bones.IsEmpty && !Contains(bones, bone.BoneName))
                continue;
            var linear = Vector3.Zero;
            var angular = Vector3.Zero;
            bone.Record?.GetBodyVelocity(out linear, out angular);
            bone.HeldKinematic = false;
            bone.LinearVelocity = linear;
            bone.AngularVelocity = angular;
            if (bone.BoneIndex >= 0 && bone.BoneIndex < _written.Length)
                _written[bone.BoneIndex] = false; // the blend starts from the pose the animation has now
            IsSimulating = true;
        }
    }

    /// <summary>Holds every body kinematic again: the bones follow the animation and the bodies follow the bones.</summary>
    public void PhysicalBonesStopSimulation()
    {
        foreach (var bone in _bones)
            Hold(bone);
        IsSimulating = false;
    }

    protected override void OnReady()
    {
        base.OnReady();
        if ((Tree is { EditMode: true } && !IsSimulated) || Skeleton is not { } skeleton) // the editor: only in a running Simulate
            return;
        _skeleton = skeleton;
        var count = skeleton.GetBoneCount();
        _byBone = new PhysicalBone3D?[count];
        _globals = new Transform3D[count];
        _animatedPositions = new Vector3[count];
        _animatedRotations = new Quaternion[count];
        _writtenPositions = new Vector3[count];
        _writtenRotations = new Quaternion[count];
        _written = new bool[count];
        var bones = new List<PhysicalBone3D>();
        foreach (var child in Children)
        {
            if (child is not PhysicalBone3D bone)
                continue;
            bone.BoneIndex = skeleton.FindBone(bone.BoneName);
            if (bone.BoneIndex < 0 || _byBone[bone.BoneIndex] is not null)
            {
                Log.Warning($"[Physics] PhysicalBone3D '{bone.Name}': the skeleton has no bone '{bone.BoneName}' (or another body drives it).");
                continue;
            }

            _byBone[bone.BoneIndex] = bone;
            bones.Add(bone);
        }

        bones.Sort(static (a, b) => a.BoneIndex.CompareTo(b.BoneIndex)); // parents before children
        _bones = [.. bones];
        _inverseOffsets = new Transform3D[_bones.Length];
        _readyPoses = new Transform3D[_bones.Length];
        if (_group == 0)
            _group = Interlocked.Increment(ref s_groups);
        for (var i = 0; i < _bones.Length; i++)
        {
            var bone = _bones[i];
            _inverseOffsets[i] = bone.BodyOffset.AffineInverse();
            bone.ExclusionGroup = _group;
            Hold(bone);
            bone.Teleport(Target(skeleton, bone));
            bone.GlobalTransform.Decompose(out var position, out var rotation, out _);
            _readyPoses[i] = Transform3D.FromTrs(position, Quaternion.Normalize(rotation), Vector3.One);
        }

        foreach (var bone in _bones)
            CreateJoint(bone);
        _ready = true;
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        ChildEnteredTree += OnChildEnteredTree;
        ChildExitingTree += OnChildExitingTree;
    }

    protected override void OnExitTree()
    {
        ChildEnteredTree -= OnChildEnteredTree;
        ChildExitingTree -= OnChildExitingTree;
        base.OnExitTree();
    }

    // A physical bone that leaves (removed, reparented, or the simulator leaving the tree) is an ordinary body again and
    // stops driving its bone; one of the ready bones that comes back is held and drives it again.
    private void OnChildExitingTree(Node child)
    {
        if (child is not PhysicalBone3D bone)
            return;
        bone.ExclusionGroup = 0;
        bone.HeldKinematic = false;
        if (bone.BoneIndex >= 0 && bone.BoneIndex < _byBone.Length && ReferenceEquals(_byBone[bone.BoneIndex], bone))
            _byBone[bone.BoneIndex] = null;
    }

    private void OnChildEnteredTree(Node child)
    {
        if (!_ready || child is not PhysicalBone3D bone || Array.IndexOf(_bones, bone) < 0)
            return;
        _byBone[bone.BoneIndex] = bone;
        bone.ExclusionGroup = _group;
        Hold(bone);
    }

    protected override void OnPhysicsProcess(float delta)
    {
        if (!_ready || _skeleton is not { } skeleton)
            return;
        // The bones that are not simulated: their bodies follow the animated pose (a kinematic move, so they push).
        foreach (var bone in _bones)
            if (bone.HeldKinematic)
                bone.GlobalTransform = Target(skeleton, bone);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (!_ready || !IsSimulating || _skeleton is not { } skeleton)
            return;
        // Parents first: each bone's skeleton-space pose is its parent's (as written this frame) times its local pose.
        var toSkeleton = skeleton.GlobalTransform.AffineInverse();
        var influence = Influence;
        var physical = 0;
        for (var i = 0; i < _globals.Length; i++)
        {
            var parent = skeleton.GetBoneParent(i);
            var parentGlobal = parent >= 0 ? _globals[parent] : Transform3D.Identity;
            if (_byBone[i] is not { HeldKinematic: false } body)
            {
                _globals[i] = parentGlobal * skeleton.GetBonePose(i);
                continue;
            }

            while (!ReferenceEquals(_bones[physical], body))
                physical++;
            var boneGlobal = toSkeleton * body.GlobalTransform * _inverseOffsets[physical];
            var local = parentGlobal.AffineInverse() * boneGlobal;
            local.Decompose(out var position, out var rotation, out _);
            var posedPosition = skeleton.GetBonePosePosition(i);
            var posedRotation = skeleton.GetBonePoseRotation(i);
            if (!_written[i] || posedPosition != _writtenPositions[i] || posedRotation != _writtenRotations[i])
            {
                _animatedPositions[i] = posedPosition; // the animation posed the bone since our last write
                _animatedRotations[i] = posedRotation;
            }

            position = Vector3.Lerp(_animatedPositions[i], position, influence);
            rotation = Quaternion.Normalize(Quaternion.Slerp(_animatedRotations[i], Quaternion.Normalize(rotation), influence));
            skeleton.SetBonePosePosition(i, position);
            skeleton.SetBonePoseRotation(i, rotation);
            _writtenPositions[i] = skeleton.GetBonePosePosition(i);
            _writtenRotations[i] = skeleton.GetBonePoseRotation(i);
            _written[i] = true;
            _globals[i] = parentGlobal * Transform3D.FromTrs(position, rotation, skeleton.GetBonePoseScale(i));
        }
    }

    /// <summary>A bone's <see cref="PhysicalBone3D.BodyOffset"/> changed: the pose write uses the new one at once.</summary>
    internal void OnBodyOffsetChanged(PhysicalBone3D bone)
    {
        var index = Array.IndexOf(_bones, bone);
        if (index >= 0)
            _inverseOffsets[index] = bone.BodyOffset.AffineInverse();
    }

    /// <summary>A bone's joint type or offset changed (re-create its joint), or only its limits (copy them).</summary>
    internal void OnBoneJointChanged(PhysicalBone3D bone, bool rebuild = true)
    {
        if (!_ready)
            return;
        if (!rebuild)
        {
            bone.ApplyJointLimits();
            return;
        }

        if (bone.Joint is { } old)
        {
            bone.Joint = null;
            old.Free();
        }

        CreateJoint(bone);
    }

    // The joint to the physical bone of the nearest ancestor bone that has one (none for a root): an internal child of
    // the simulator (no owner: not saved, not in the editor's tree). Its frames come from the ready pose (the body's
    // ready pose times its JointOffset, in both bodies' ready frames), so one re-created at run time (a new JointType or
    // JointOffset) is authored like the first, wherever the crumpled bodies are now.
    private void CreateJoint(PhysicalBone3D bone)
    {
        if (_skeleton is not { } skeleton || bone.BoneIndex < 0)
            return;
        PhysicalBone3D? parent = null;
        for (var b = skeleton.GetBoneParent(bone.BoneIndex); b >= 0 && parent is null; b = skeleton.GetBoneParent(b))
            parent = _byBone[b];
        if (parent is null || bone.CreateJoint() is not { } joint)
            return;
        joint.Name = bone.Name + "Joint";
        AddChild(joint);
        joint.GlobalTransform = bone.GlobalTransform * bone.JointOffset;
        joint.NodeA = "../" + parent.Name;
        joint.NodeB = "../" + bone.Name;
        var readyBone = _readyPoses[Array.IndexOf(_bones, bone)];
        joint.UseAuthoredFrames(parent, _readyPoses[Array.IndexOf(_bones, parent)], bone, readyBone, readyBone * bone.JointOffset);
        bone.Joint = joint;
    }

    /// <summary>
    /// Builds a physical skeleton for <paramref name="skeleton"/> (not added to it; the editor's "Create physical
    /// skeleton" adds it as one undo step): a simulator with one <see cref="PhysicalBone3D"/> per bone at least
    /// <paramref name="minBoneLength"/> long, a capsule from the bone to its first child (radius 20 % of the length; the
    /// default 1 kg, as Godot's: equal masses keep a chain of joints stiff, where a heavy thigh on a light pelvis stretches)
    /// and a 45°/30° cone-twist joint at the bone. Bones without children have no length and get no body. Limits are
    /// generic: a humanoid map would make knees and elbows hinges (G1c, not yet).
    /// </summary>
    public static PhysicalBoneSimulator3D Build(Skeleton3D skeleton, float minBoneLength = 0.05f)
    {
        ArgumentNullException.ThrowIfNull(skeleton);
        var simulator = new PhysicalBoneSimulator3D { Name = "PhysicalBoneSimulator3D" };
        var count = skeleton.GetBoneCount();
        for (var bone = 0; bone < count; bone++)
        {
            var child = -1;
            for (var c = bone + 1; c < count && child < 0; c++)
                if (skeleton.GetBoneParent(c) == bone)
                    child = c;
            if (child < 0)
                continue;
            var toChild = skeleton.GetBoneRest(child).Origin; // in the bone's space
            var length = toChild.Length();
            if (length < minBoneLength)
                continue;

            // The body sits halfway along the bone, its Y (the capsule's axis) along the bone; the joint is at the bone's
            // start with its X (the cone-twist axis) along the bone.
            var offset = Transform3D.FromTrs(toChild / 2, FromTo(Vector3.UnitY, toChild / length), Vector3.One);
            var radius = length * 0.2f;
            var body = new PhysicalBone3D
            {
                Name = "Bone_" + SafeName(skeleton.GetBoneName(bone)),
                BoneName = skeleton.GetBoneName(bone),
                BodyOffset = offset,
                JointOffset = Transform3D.FromTrs(new Vector3(0, -length / 2, 0), Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2), Vector3.One),
                Transform = skeleton.GetBoneGlobalRest(bone) * offset,
            };
            body.AddChild(new CollisionShape3D { Name = "Shape", Shape = new CapsuleShape3D { Radius = radius, Height = length } });
            simulator.AddChild(body);
        }

        return simulator;
    }

    // Node names may not hold path characters: "Hand.R" → "Hand_R".
    private static string SafeName(string bone)
    {
        var name = bone.ToCharArray();
        for (var i = 0; i < name.Length; i++)
            if (name[i] is '.' or ':' or '@' or '/' or '"' or '%')
                name[i] = '_';
        return new string(name);
    }

    // The shortest rotation taking unit vector a to unit vector b.
    private static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var dot = Vector3.Dot(a, b);
        if (dot > 0.99999f)
            return Quaternion.Identity;
        if (dot < -0.99999f)
            return Quaternion.CreateFromAxisAngle(MathF.Abs(a.X) < 0.9f ? Vector3.UnitX : Vector3.UnitZ, MathF.PI);
        return Quaternion.Normalize(Quaternion.CreateFromAxisAngle(Vector3.Normalize(Vector3.Cross(a, b)), MathF.Acos(dot)));
    }

    private static Transform3D Target(Skeleton3D skeleton, PhysicalBone3D bone) =>
        skeleton.GlobalTransform * skeleton.GetBoneGlobalPose(bone.BoneIndex) * bone.BodyOffset;

    private static void Hold(PhysicalBone3D bone)
    {
        bone.HeldKinematic = true;
        bone.LinearVelocity = Vector3.Zero;
        bone.AngularVelocity = Vector3.Zero;
    }

    private static bool Contains(ReadOnlySpan<string> names, string name)
    {
        foreach (var candidate in names)
            if (string.Equals(candidate, name, StringComparison.Ordinal))
                return true;
        return false;
    }
}
