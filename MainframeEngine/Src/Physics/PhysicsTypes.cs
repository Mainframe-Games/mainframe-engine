namespace MainframeEngine;

/// <summary>How a <see cref="RigidBody3D"/>/<see cref="RigidBody2D"/> moves.</summary>
public enum RigidBodyMode
{
    /// <summary>Simulated: gravity, forces, contacts.</summary>
    Dynamic,

    /// <summary>
    /// Moved by code (its node transform or <c>LinearVelocity</c>); pushes dynamic bodies but is not pushed back.
    /// Moving the node turns into a velocity for the step, so contacts respond properly.
    /// </summary>
    Kinematic,
}

/// <summary>Axes a rigid body may not move or rotate along (world space). 2D bodies use LinearX/LinearY/AngularZ.</summary>
[Flags]
public enum AxisLock
{
    None = 0,
    LinearX = 1,
    LinearY = 2,
    LinearZ = 4,
    AngularX = 8,
    AngularY = 16,
    AngularZ = 32,
}

/// <summary>Collision layer/mask helpers (Godot semantics, ADR 0021).</summary>
public static class CollisionLayers
{
    /// <summary>Layer 1, the default layer and mask of every collision object.</summary>
    public const uint Default = 1;

    /// <summary>Every layer.</summary>
    public const uint All = uint.MaxValue;

    /// <summary>
    /// Whether two collision objects collide: <c>(A.layer &amp; B.mask) != 0 || (B.layer &amp; A.mask) != 0</c> —
    /// either one scanning the other's layer is enough. Areas are one-way: an area detects a body when
    /// <c>(area.mask &amp; body.layer) != 0</c>.
    /// </summary>
    public static bool ShouldCollide(uint layerA, uint maskA, uint layerB, uint maskB) =>
        (layerA & maskB) != 0 || (layerB & maskA) != 0;

    /// <summary>The bit for layer <paramref name="layerNumber"/> (1..32, as numbered in the editor).</summary>
    public static uint Layer(int layerNumber)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(layerNumber, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(layerNumber, 32);
        return 1u << (layerNumber - 1);
    }
}

/// <summary>Kinds of collision object a physics space tracks.</summary>
internal enum PhysicsBodyKind : byte
{
    Static,
    Kinematic,
    Dynamic,
    Character,
    Area,
}

/// <summary>Physics signals in dispatch order: exits, then enters, then sleep changes (ADR 0022).</summary>
internal enum PhysicsEventKind : byte
{
    ContactExited,
    AreaExited,
    ContactEntered,
    AreaEntered,
    SleepChanged,
}

/// <summary>A queued physics signal: emitted on the main thread after the step that produced it.</summary>
internal readonly record struct PhysicsEvent<TRecord>(PhysicsEventKind Kind, TRecord Receiver, TRecord? Other, long ReceiverSequence, long OtherSequence)
    where TRecord : class
{
    /// <summary>Exits, enters, sleep; then receiver creation order; then other creation order.</summary>
    public sealed class Comparer : IComparer<PhysicsEvent<TRecord>>
    {
        public static readonly Comparer Instance = new();

        public int Compare(PhysicsEvent<TRecord> x, PhysicsEvent<TRecord> y)
        {
            var c = x.Kind.CompareTo(y.Kind);
            if (c != 0)
                return c;
            c = x.ReceiverSequence.CompareTo(y.ReceiverSequence);
            return c != 0 ? c : x.OtherSequence.CompareTo(y.OtherSequence);
        }
    }
}

/// <summary>Per-pair contact bookkeeping for bodies with <c>ContactMonitor</c>: shape contacts and the reported state.</summary>
internal struct ContactPairState
{
    public int ShapeContacts;
    public bool Reported;
}
