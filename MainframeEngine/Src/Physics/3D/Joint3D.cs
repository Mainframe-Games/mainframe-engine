using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// Base of the 3D joints (Godot's <c>Joint3D</c>, ADR 0211): connects the <see cref="PhysicsBody3D"/>s at
/// <see cref="NodeA"/> and <see cref="NodeB"/> (an empty path is the static world) at the joint's pose. The joint is
/// created at the next physics flush after its bodies are in the space, so tree order and the order paths are set in
/// don't matter. Its pose is captured in each body's frame on creation (moving the joint node afterwards does nothing;
/// <see cref="Rebuild"/> re-captures it). A body that leaves the space, is disabled or switches Dynamic ↔ Kinematic
/// keeps its joints: they are re-created at the authored frames, with limits measured from the authored pose.
/// </summary>
/// <remarks>
/// Jitter2 keeps no constraint between two bodies that are not dynamic: such a joint waits (<see cref="IsActive"/>
/// false) until one of them is dynamic again. Every Jitter2 call is in <see cref="PhysicsSpace3D"/> and the joint types.
/// </remarks>
[EditorIcon("link", Family = EditorIconFamily.Physics)]
public abstract class Joint3D : Node3D
{
    internal JointRecord3D? Record { get; set; }

    /// <summary>The first body (empty: the static world). Changing it re-creates the joint.</summary>
    [Export(NodeType = typeof(PhysicsBody3D))]
    public NodePath NodeA
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Rebuild();
        }
    }

    /// <summary>The second body (empty: the static world). Changing it re-creates the joint.</summary>
    [Export(NodeType = typeof(PhysicsBody3D))]
    public NodePath NodeB
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            Rebuild();
        }
    }

    /// <summary>The two bodies don't collide with each other while the joint holds them (Godot's default).</summary>
    [Export]
    public bool ExcludeNodesFromCollision
    {
        get;
        set => Live(ref field, value);
    } = true;

    /// <summary>Off: the joint's constraints are disabled (kept, so turning it on again is instant).</summary>
    [Export]
    public bool Enabled
    {
        get;
        set => Live(ref field, value);
    } = true;

    /// <summary>The joint breaks when the force it applies exceeds this (N); 0 = never.</summary>
    [ExportGroup("Breaking")]
    [Export(Range = "0,1000000,1")]
    public float BreakForce
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    }

    /// <summary>The joint breaks when the torque it applies exceeds this (N·m); 0 = never.</summary>
    [Export(Range = "0,1000000,1")]
    public float BreakTorque
    {
        get;
        set => Live(ref field, MathF.Max(0f, value));
    }

    /// <summary>The joint broke (<see cref="BreakForce"/>/<see cref="BreakTorque"/>), after the step, on the main thread.</summary>
    [Signal]
    public event Action? Broken;

    /// <summary>The joined first body (null: the world, or not created).</summary>
    public PhysicsBody3D? BodyA => Record?.A?.Node as PhysicsBody3D;

    /// <summary>The joined second body (null: the world, or not created).</summary>
    public PhysicsBody3D? BodyB => Record?.B?.Node as PhysicsBody3D;

    /// <summary>The joint exists in its space (its bodies are there, one is dynamic, and it is not broken).</summary>
    public bool IsActive => Record is { HasJoint: true };

    /// <summary>The joint broke; it stays broken until <see cref="Rebuild"/>.</summary>
    public bool IsBroken => Record is { Broken: true };

    /// <summary>The force the joint applied in the last step (N); when broken, the force that broke it.</summary>
    public float GetAppliedForce() => Record is { } r ? r.Broken ? r.BrokenForce : r.Space.GetJointForce(r) : 0f;

    /// <summary>The torque the joint applied in the last step (N·m); when broken, the torque that broke it.</summary>
    public float GetAppliedTorque() => Record is { } r ? r.Broken ? r.BrokenTorque : r.Space.GetJointTorque(r) : 0f;

    /// <summary>How far apart the joint's anchors on its two bodies are in metres (0 when it holds exactly, or inactive).</summary>
    public float GetSeparation()
    {
        if (Record is not { HasJoint: true } record)
            return 0f;
        record.GetFrameA(out var a, out _);
        record.GetFrameB(out var b, out _);
        return Vector3.Distance(a, b);
    }

    /// <summary>
    /// Re-creates the joint at the next physics flush: resolves the paths again, captures its frames from the joint's
    /// current pose and clears <see cref="IsBroken"/>.
    /// </summary>
    public void Rebuild()
    {
        if (Record is not { } record)
            return;
        record.Broken = false;
        record.Space.MarkJointDirty(record, recapture: true);
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        Record = PhysicsServer3D.For(Tree!).AddJoint(this);
    }

    protected override void OnExitTree()
    {
        if (Record is { } record)
            record.Space.RemoveJoint(record);
        Record = null;
        base.OnExitTree();
    }

    // A disabled joint node (ProcessMode.Disabled) disables its constraints, like Enabled = false.
    private protected override void OnDisabledChanged(bool disabled)
    {
        if (Record is { } record)
            record.Space.OnJointPropertyChanged(record);
    }

    internal void EmitBroken() => Broken?.Invoke();

    /// <summary>
    /// Draws the joint into <paramref name="lines"/> in <paramref name="color"/>: its anchors, axis, limits and current
    /// angle or position, as the physics debug draw shows it. An active joint is drawn at its bodies' rendered poses, an
    /// inactive one (edit mode, waiting for its bodies, broken) at the node. The editor draws the selected joint with it.
    /// </summary>
    public void DrawDebug(DebugLines lines, Vector4 color)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (Record is { HasJoint: true } record)
        {
            PhysicsSpace3D.JointFrames(record, out var frame, out var anchorB);
            DrawDebugShape(lines, frame, anchorB, GetDebugValue(record), color);
            return;
        }

        var pose = GlobalTransform;
        pose.Decompose(out var position, out var rotation, out _);
        DrawDebugShape(lines, Transform3D.FromTrs(position, Quaternion.Normalize(rotation), Vector3.One), GetAnchorB(pose), 0f, color);
    }

    /// <summary>Creates the type's Jitter2 constraints into <paramref name="record"/> (bodies and frames are set).</summary>
    internal abstract void CreateConstraints(JointRecord3D record);

    /// <summary>Applies the properties Jitter2 can change in place (limits, motors).</summary>
    internal virtual void ApplyLive(JointRecord3D record)
    {
    }

    /// <summary>The joint has a spring the space applies before each step (<see cref="ApplySpring"/>).</summary>
    internal virtual bool HasSpring => false;

    /// <summary>Applies the joint's spring for one step of <paramref name="delta"/> seconds.</summary>
    internal virtual void ApplySpring(JointRecord3D record, float delta)
    {
    }

    /// <summary>Where body B is anchored, given the joint's global pose (the joint's origin unless a type says otherwise).</summary>
    internal virtual Vector3 GetAnchorB(in Transform3D pose) => pose.Origin;

    /// <summary>Whether the debug draw shows the joint as overstretched (its anchors more than 5 cm apart).</summary>
    internal virtual bool IsStretched(JointRecord3D record, Vector3 anchorA, Vector3 anchorB) => Vector3.DistanceSquared(anchorA, anchorB) > 0.05f * 0.05f;

    /// <summary>The value the debug shape marks (a hinge's angle, a slider's position); 0 when there is none.</summary>
    internal virtual float GetDebugValue(JointRecord3D record) => 0f;

    /// <summary>
    /// Draws the joint: <paramref name="frame"/> is its frame on body A (world, unscaled), <paramref name="anchorB"/> the
    /// anchor on body B, <paramref name="value"/> what <see cref="GetDebugValue"/> returned.
    /// </summary>
    internal virtual void DrawDebugShape(DebugLines lines, in Transform3D frame, Vector3 anchorB, float value, Vector4 color)
    {
        DrawAnchors(lines, frame, anchorB, color);
    }

    /// <summary>Small crosses at both anchors and the segment between them when they separate.</summary>
    private protected static void DrawAnchors(DebugLines lines, in Transform3D frame, Vector3 anchorB, Vector4 color, float size = 0.06f)
    {
        var a = frame.Origin;
        lines.AddLine(a - Vector3.UnitX * size, a + Vector3.UnitX * size, color);
        lines.AddLine(a - Vector3.UnitY * size, a + Vector3.UnitY * size, color);
        lines.AddLine(a - Vector3.UnitZ * size, a + Vector3.UnitZ * size, color);
        if (Vector3.DistanceSquared(a, anchorB) > 1e-6f)
        {
            lines.AddLine(a, anchorB, color);
            lines.AddSphere(Transform3D.FromTrs(anchorB, Quaternion.Identity, Vector3.One), size * 0.5f, color);
        }
    }

    /// <summary>An arc of <paramref name="radius"/> from angle <paramref name="from"/> to <paramref name="to"/> (radians, from +u toward +v).</summary>
    private protected static void DrawArc(DebugLines lines, in Transform3D frame, Vector3 u, Vector3 v, float radius, float from, float to,
        Vector4 color)
    {
        var segments = Math.Clamp((int)MathF.Ceiling(MathF.Abs(to - from) / (MathF.Tau / 32)), 1, 32);
        var previous = frame.TransformPoint((u * MathF.Cos(from) + v * MathF.Sin(from)) * radius);
        for (var i = 1; i <= segments; i++)
        {
            var angle = from + (to - from) * i / segments;
            var next = frame.TransformPoint((u * MathF.Cos(angle) + v * MathF.Sin(angle)) * radius);
            lines.AddLine(previous, next, color);
            previous = next;
        }
    }

    private protected void Live<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        if (Record is { } record)
            record.Space.OnJointPropertyChanged(record);
    }
}
