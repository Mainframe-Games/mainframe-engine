using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A rigid-part ragdoll (ADR 0212): its <b>direct</b> <see cref="RigidBody3D"/> children are the parts, joined by
/// ordinary joint nodes (<see cref="ConeTwistJoint3D"/> shoulders, <see cref="HingeJoint3D"/> knees, …) under it. While
/// not <see cref="Simulating"/> the parts are kinematic and follow their nodes (an <see cref="AnimationPlayer"/> or code
/// moves them; they push what they touch); <see cref="StartSimulation"/> makes them dynamic, carrying the velocity of
/// their last kinematic move into the fall, and <see cref="StopSimulation"/> holds them kinematic where they are.
/// </summary>
/// <remarks>
/// Parts are flat siblings: a parent's physics pose write would teleport nested dynamic bodies. Put visuals under each
/// part. The parts' <see cref="RigidBody3D.Mode"/> is not changed (the scene saves what was authored); the ragdoll holds
/// them kinematic at run time. Joints between two kinematic parts wait in Jitter2 and come back, at their authored
/// frames and limits, when the simulation starts. Moving the ragdoll node moves the parts with it (kinematic: smoothly;
/// simulating: a teleport).
/// </remarks>
[EditorIcon("bone", Family = EditorIconFamily.Physics)]
public class Ragdoll3D : Node3D
{
    private static int s_groups;
    private int _group;
    private bool _ready;

    /// <summary>The parts are dynamic (simulated); off, they are kinematic and follow their nodes.</summary>
    [Export]
    public bool Simulating
    {
        get;
        set
        {
            if (field == value)
                return;
            if (!_ready)
            {
                field = value; // applied in OnReady
                return;
            }

            if (value)
                StartSimulation();
            else
                StopSimulation();
        }
    }

    /// <summary>The parts never collide with each other (joined or not), whatever their layers.</summary>
    [Export]
    public bool ExcludePartCollisions
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            if (_ready)
                ApplyGroup();
        }
    } = true;

    /// <summary>The simulation started (<see cref="StartSimulation"/>).</summary>
    [Signal]
    public event Action? SimulationStarted;

    /// <summary>The simulation stopped (<see cref="StopSimulation"/>).</summary>
    [Signal]
    public event Action? SimulationStopped;

    /// <summary>Adds the parts (the direct <see cref="RigidBody3D"/> children) to <paramref name="results"/>; returns how many.</summary>
    public int GetParts(List<RigidBody3D> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        var count = 0;
        for (var i = 0; i < ChildCount; i++)
        {
            if (GetChild(i) is RigidBody3D part)
            {
                results.Add(part);
                count++;
            }
        }

        return count;
    }

    /// <summary>Makes the parts dynamic, each keeping the velocity of its last kinematic move. Raises <see cref="SimulationStarted"/>.</summary>
    public void StartSimulation()
    {
        if (_ready && Simulating)
            return;
        SetSimulating(true);
        for (var i = 0; i < ChildCount; i++)
        {
            if (GetChild(i) is not RigidBody3D part)
                continue;
            var linear = Vector3.Zero;
            var angular = Vector3.Zero;
            part.Record?.GetBodyVelocity(out linear, out angular);
            part.HeldKinematic = false;
            part.LinearVelocity = linear;
            part.AngularVelocity = angular;
        }

        if (_ready)
            SimulationStarted?.Invoke();
    }

    /// <summary>Holds the parts kinematic where they are (they follow their nodes again). Raises <see cref="SimulationStopped"/>.</summary>
    public void StopSimulation()
    {
        if (_ready && !Simulating)
            return;
        SetSimulating(false);
        for (var i = 0; i < ChildCount; i++)
        {
            if (GetChild(i) is not RigidBody3D part)
                continue;
            part.HeldKinematic = true;
            part.LinearVelocity = Vector3.Zero; // a kinematic velocity would keep moving it
            part.AngularVelocity = Vector3.Zero;
        }

        if (_ready)
            SimulationStopped?.Invoke();
    }

    /// <summary>Applies an impulse (N·s, world space) to one of the parts, at <paramref name="position"/> or its centre of mass.</summary>
    public void ApplyImpulse(RigidBody3D part, Vector3 impulse, Vector3? position = null)
    {
        ArgumentNullException.ThrowIfNull(part);
        if (!ReferenceEquals(part.Parent, this))
            throw new ArgumentException($"'{part.Name}' is not a part of the ragdoll '{Name}'.", nameof(part));
        part.ApplyImpulse(impulse, position);
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        ChildEnteredTree += OnChildEnteredTree;
    }

    protected override void OnExitTree()
    {
        ChildEnteredTree -= OnChildEnteredTree;
        base.OnExitTree();
    }

    // A part added after the ragdoll is ready joins in the ragdoll's current state.
    private void OnChildEnteredTree(Node child)
    {
        if (!_ready || child is not RigidBody3D part)
            return;
        part.ExclusionGroup = ExcludePartCollisions ? _group : 0;
        part.HeldKinematic = !Simulating;
    }

    protected override void OnReady()
    {
        base.OnReady();
        if (_group == 0)
            _group = Interlocked.Increment(ref s_groups);
        ApplyGroup();
        // The initial state, without signals: the parts are held kinematic unless the ragdoll starts simulating.
        if (Simulating)
            StartSimulation();
        else
            StopSimulation();
        _ready = true;
    }

    private void SetSimulating(bool value)
    {
        var ready = _ready;
        _ready = false; // the property setter only stores while not ready
        Simulating = value;
        _ready = ready;
    }

    private void ApplyGroup()
    {
        for (var i = 0; i < ChildCount; i++)
            if (GetChild(i) is RigidBody3D part)
                part.ExclusionGroup = ExcludePartCollisions ? _group : 0;
    }
}
