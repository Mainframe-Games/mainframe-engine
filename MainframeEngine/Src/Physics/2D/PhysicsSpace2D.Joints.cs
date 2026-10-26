using System.Numerics;
using Box2D.NET;
using static Box2D.NET.B2Bodies;
using static Box2D.NET.B2Joints;
using static Box2D.NET.B2MathFunction;
using static Box2D.NET.B2Types;
using static Box2D.NET.B2Worlds;

namespace MainframeEngine;

/// <summary>The physics state of one <see cref="Joint2D"/> in a <see cref="PhysicsSpace2D"/> (ADR 0201).</summary>
internal sealed class JointRecord2D(Joint2D node, PhysicsSpace2D space, long sequence)
{
    public readonly Joint2D Node = node;
    public readonly PhysicsSpace2D Space = space;
    public readonly long Sequence = sequence;

    /// <summary>The bodies joined (null: the space's static world body).</summary>
    public BodyRecord2D? A, B;

    public B2JointId Id;
    public bool HasJoint;
    public bool Removed;
    public bool Broken;
    public bool Dirty;
    public bool InDirtyList;
    public bool LoggedError;

    /// <summary>
    /// The authored frames: the joint's pose (and a type's second anchor) in each body's local space, metres, captured
    /// on creation and kept while the joint's paths stay the same (a body that leaves and returns re-joins there).
    /// </summary>
    public B2Transform FrameA, FrameB;
    public CollisionObject2D? FramedA, FramedB;
    public bool HasFrames;

    /// <summary>Applied force (kg·px/s²) and torque (kg·px²/s²) when the joint broke.</summary>
    public float BrokenForce, BrokenTorque;
}

public sealed partial class PhysicsSpace2D
{
    private readonly List<JointRecord2D> _joints = [];
    private readonly List<JointRecord2D> _dirtyJoints = [];
    private readonly List<JointRecord2D> _brokenJoints = [];
    private B2BodyId _worldBody;
    private bool _hasWorldBody;
    private bool _retryPendingJoints;
    private long _jointSequence;

    /// <summary>Joints that broke so far (the dev overlay shows the rate).</summary>
    public long BrokenJointTotal { get; private set; }

    /// <summary>Joints in the space (created or waiting for their bodies).</summary>
    public int JointCount => _joints.Count;

    /// <summary>Joints that exist in Box2D now.</summary>
    public int ActiveJointCount
    {
        get
        {
            var n = 0;
            foreach (var joint in _joints)
                if (joint.HasJoint)
                    n++;
            return n;
        }
    }

    internal JointRecord2D AddJoint(Joint2D node)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var record = new JointRecord2D(node, this, ++_jointSequence);
        _joints.Add(record);
        MarkJointDirty(record, recapture: true);
        return record;
    }

    internal void RemoveJoint(JointRecord2D record)
    {
        if (record.Removed)
            return;
        record.Removed = true;
        DestroyJoint(record);
        _joints.Remove(record);
    }

    /// <summary>Re-creates the joint at the next flush; <paramref name="recapture"/> takes new frames from the joint's pose.</summary>
    internal void MarkJointDirty(JointRecord2D record, bool recapture)
    {
        if (record.Removed)
            return;
        if (recapture)
            record.HasFrames = false;
        record.Dirty = true;
        if (record.InDirtyList)
            return;
        record.InDirtyList = true;
        _dirtyJoints.Add(record);
    }

    /// <summary>A property Box2D can change in place (tuning, thresholds, limits, motor, spring).</summary>
    internal void OnJointPropertyChanged(JointRecord2D record)
    {
        if (record.HasJoint)
            record.Node.ApplyLive(record.Id, record, PixelsPerMeter);
    }

    internal Vector2 GetJointForce(JointRecord2D record) =>
        record.HasJoint ? ToNumerics(b2Joint_GetConstraintForce(record.Id)) * PixelsPerMeter : Vector2.Zero;

    internal float GetJointTorque(JointRecord2D record) =>
        record.HasJoint ? b2Joint_GetConstraintTorque(record.Id) * PixelsPerMeter * PixelsPerMeter : 0f;

    /// <summary>Distance between the two anchors in pixels (0 when the joint holds exactly; grows when it is stretched).</summary>
    internal float GetJointSeparation(JointRecord2D record) =>
        record.HasJoint ? b2Joint_GetLinearSeparation(record.Id) * PixelsPerMeter : 0f;

    // A body leaving the space: Box2D's b2DestroyBody destroys its joints, so the records only forget their handles and
    // wait for the body (or another one at the same path) to join again.
    private void DetachJoints(BodyRecord2D body)
    {
        if (body.Joints is not { Count: > 0 } joints)
            return;
        for (var i = joints.Count - 1; i >= 0; i--)
        {
            var joint = joints[i];
            Unlink(joint);
            joint.HasJoint = false;
            MarkJointDirty(joint, recapture: false);
        }
    }

    private void DestroyJoint(JointRecord2D record)
    {
        if (!record.HasJoint)
            return;
        Unlink(record);
        record.HasJoint = false;
        if (!_disposed && b2Joint_IsValid(record.Id))
            b2DestroyJoint(record.Id, true);
    }

    private static void Unlink(JointRecord2D record)
    {
        record.A?.Joints?.Remove(record);
        record.B?.Joints?.Remove(record);
        record.A = record.B = null;
    }

    private void FlushJoints()
    {
        if (_retryPendingJoints)
        {
            _retryPendingJoints = false;
            foreach (var joint in _joints)
                if (!joint.HasJoint && !joint.Broken && !joint.InDirtyList)
                {
                    joint.InDirtyList = true;
                    _dirtyJoints.Add(joint);
                }
        }

        if (_dirtyJoints.Count == 0)
            return;
        // Index loop: creating a joint never adds to the list, but keep it robust to re-entrant marks.
        for (var i = 0; i < _dirtyJoints.Count; i++)
        {
            var joint = _dirtyJoints[i];
            joint.InDirtyList = false;
            joint.Dirty = false;
            if (joint.Removed || joint.Broken)
                continue;
            DestroyJoint(joint);
            TryCreate(joint);
        }

        _dirtyJoints.Clear();
    }

    private void TryCreate(JointRecord2D record)
    {
        var node = record.Node;
        if (!node.IsEffectivelyEnabled)
            return; // Box2D has no joint enable flag: a disabled joint does not exist there (frames are kept)
        if (!TryResolve(record, node.NodeA, out var a) || !TryResolve(record, node.NodeB, out var b))
            return;
        if (a is null && b is null)
            return; // nothing to join (Snake Oil clears both paths to detach a joint while tuning): silent
        if (ReferenceEquals(a, b))
        {
            LogOnce(record, $"[Physics2D] Joint '{node.Name}': NodeA and NodeB are the same body; not created.");
            return;
        }

        var bodyA = a?.Body ?? WorldBody();
        var bodyB = b?.Body ?? WorldBody();
        if (!record.HasFrames || !ReferenceEquals(record.FramedA, a?.Node) || !ReferenceEquals(record.FramedB, b?.Node))
        {
            var pose = node.GlobalTransform;
            var poseB = node.GetAnchorB(pose);
            var angle = pose.Rotation + node.FrameAngle;
            record.FrameA = LocalFrame(bodyA, pose.Origin, angle);
            record.FrameB = LocalFrame(bodyB, poseB, angle);
            record.FramedA = a?.Node;
            record.FramedB = b?.Node;
            record.HasFrames = true;
        }

        var def = b2DefaultJointDef();
        def.bodyIdA = bodyA;
        def.bodyIdB = bodyB;
        def.localFrameA = record.FrameA;
        def.localFrameB = record.FrameB;
        def.collideConnected = !node.DisableCollision;
        def.userData = B2UserData.Ref(record);
        ApplyBase(ref def, node, PixelsPerMeter);
        record.Id = node.CreateJoint(_world, def, PixelsPerMeter);
        record.HasJoint = true;
        record.A = a;
        record.B = b;
        if (a is not null)
            (a.Joints ??= []).Add(record);
        if (b is not null)
            (b.Joints ??= []).Add(record);
        record.LoggedError = false;
    }

    internal static void ApplyBase(ref B2JointDef def, Joint2D node, float ppm)
    {
        def.constraintHertz = node.ConstraintHertz;
        def.constraintDampingRatio = node.ConstraintDampingRatio;
        def.forceThreshold = node.BreakForce > 0f ? node.BreakForce / ppm : float.MaxValue;
        def.torqueThreshold = node.BreakTorque > 0f ? node.BreakTorque / (ppm * ppm) : float.MaxValue;
    }

    // False: the path names something that cannot be joined yet (missing, not in this space, not a body).
    private bool TryResolve(JointRecord2D record, NodePath path, out BodyRecord2D? body)
    {
        body = null;
        if (path.IsEmpty)
            return true;
        switch (record.Node.GetNodeOrNull(path))
        {
            case null:
                return false; // not in the tree (yet): wait
            case PhysicsBody2D { Record: { } found } when ReferenceEquals(found.Space, this):
                body = found;
                return true;
            case PhysicsBody2D { Record: null }:
                return false; // disabled or not yet added: wait for it to join
            case PhysicsBody2D:
                LogOnce(record, $"[Physics2D] Joint '{record.Node.Name}': '{path}' is in another world; not created.");
                return false;
            default:
                LogOnce(record, $"[Physics2D] Joint '{record.Node.Name}': '{path}' is not a PhysicsBody2D; not created.");
                return false;
        }
    }

    private static void LogOnce(JointRecord2D record, string message)
    {
        if (record.LoggedError)
            return;
        record.LoggedError = true;
        Log.Error(message);
    }

    private B2BodyId WorldBody()
    {
        if (_hasWorldBody)
            return _worldBody;
        var def = b2DefaultBodyDef(); // static, at the origin, no shapes: the anchor for joints with one empty path
        _worldBody = b2CreateBody(_world, def);
        _hasWorldBody = true;
        return _worldBody;
    }

    private B2Transform LocalFrame(B2BodyId body, Vector2 point, float rotation)
    {
        var xf = b2Body_GetTransform(body);
        var p = ToB2(point / PixelsPerMeter);
        var q = b2MakeRot(rotation);
        return new B2Transform(b2InvTransformPoint(in xf, in p), b2InvMulRot(in xf.q, in q));
    }

    // After the step: joints over their force or torque threshold break (destroyed now, Broken dispatched after the
    // body signals).
    private void ReadJointEvents()
    {
        if (_joints.Count == 0)
            return;
        var events = b2World_GetJointEvents(_world);
        for (var i = 0; i < events.count; i++)
        {
            if (events.jointEvents[i].userData.oValue is not JointRecord2D record || !record.HasJoint || record.Removed)
                continue;
            record.BrokenForce = GetJointForce(record).Length();
            record.BrokenTorque = MathF.Abs(GetJointTorque(record));
            DestroyJoint(record);
            record.Broken = true;
            BrokenJointTotal++;
            _brokenJoints.Add(record);
        }
    }

    private void DispatchJointBreaks()
    {
        if (_brokenJoints.Count == 0)
            return;
        // Index loop: a handler may break or free other joints (a freed joint is skipped).
        for (var i = 0; i < _brokenJoints.Count; i++)
            if (!_brokenJoints[i].Removed)
                _brokenJoints[i].Node.EmitBroken();
        _brokenJoints.Clear();
    }

    private static readonly Vector4 JointColor = new(1f, 0.75f, 0.2f, 1f);
    private static readonly Vector4 StretchedJointColor = new(1f, 0.25f, 0.2f, 1f);
    private static readonly Vector4 InactiveJointColor = new(0.55f, 0.55f, 0.6f, 1f);

    private void DrawJointsDebug(DebugLines lines)
    {
        foreach (var joint in _joints)
        {
            var node = joint.Node;
            if (!node.IsVisibleInTree())
                continue;
            if (!joint.HasJoint)
            {
                DrawJoint(joint, lines, InactiveJointColor); // pending (edit mode, missing bodies), disabled or broken: greyed
                continue;
            }

            var color = node.IsStretched(AnchorDistance(joint)) ? StretchedJointColor : JointColor;
            DrawJoint(joint, lines, color);
        }
    }

    private float AnchorDistance(JointRecord2D joint)
    {
        JointFrames(joint, out var frame, out var b);
        return Vector2.Distance(frame.Origin, b);
    }

    /// <summary>Draws one joint: at its bodies' poses when it exists in Box2D, else at the node (pending, disabled, broken).</summary>
    internal void DrawJoint(JointRecord2D joint, DebugLines lines, Vector4 color)
    {
        var node = joint.Node;
        if (!joint.HasJoint)
        {
            var pose = node.GlobalTransform;
            var frame = Transform2D.FromTrs(pose.Origin, pose.Rotation + node.FrameAngle, Vector2.One);
            var anchor = node.GetAnchorB(pose);
            DrawAnchors(lines, frame.Origin, anchor, color);
            node.DrawDebugShape(lines, frame, anchor, 0f, color);
            return;
        }

        JointFrames(joint, out var jointFrame, out var b);
        DrawAnchors(lines, jointFrame.Origin, b, color);
        node.DrawDebugShape(lines, jointFrame, b, node.GetDebugValue(joint.Id, PixelsPerMeter), color);
    }

    // The joint's frame on body A (pixels) and its anchor on body B, from the Box2D bodies.
    private void JointFrames(JointRecord2D joint, out Transform2D frame, out Vector2 anchorB)
    {
        var xfA = b2Body_GetTransform(b2Joint_GetBodyA(joint.Id));
        var xfB = b2Body_GetTransform(b2Joint_GetBodyB(joint.Id));
        var a = ToNumerics(b2TransformPoint(in xfA, in joint.FrameA.p)) * PixelsPerMeter;
        anchorB = ToNumerics(b2TransformPoint(in xfB, in joint.FrameB.p)) * PixelsPerMeter;
        var rotation = b2MulRot(in xfA.q, in joint.FrameA.q);
        frame = Transform2D.FromTrs(a, b2Rot_GetAngle(rotation), Vector2.One);
    }

    /// <summary>Draws a joint that is in no space (outside a tree): at the node.</summary>
    internal static void DrawLooseJoint(Joint2D node, DebugLines lines, Vector4 color)
    {
        var pose = node.GlobalTransform;
        var frame = Transform2D.FromTrs(pose.Origin, pose.Rotation + node.FrameAngle, Vector2.One);
        var anchor = node.GetAnchorB(pose);
        DrawAnchors(lines, frame.Origin, anchor, color);
        node.DrawDebugShape(lines, frame, anchor, 0f, color);
    }

    private static void DrawAnchors(DebugLines lines, Vector2 a, Vector2 b, Vector4 color)
    {
        lines.AddLine2D(Transform2D.Identity, a, b, color);
        lines.AddCircle2D(Transform2D.Identity, a, 3f, color);
        lines.AddCircle2D(Transform2D.Identity, b, 3f, color);
    }
}
