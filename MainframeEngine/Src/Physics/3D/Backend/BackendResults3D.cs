using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A body in a <see cref="PhysicsBackend3D"/>: an index into the backend's table (indices start at 1 and are reused after
/// <see cref="PhysicsBackend3D.DestroyBody"/>). <c>default</c> is <see cref="None"/>. Handles are per backend: never use one
/// with another space's backend.
/// </summary>
internal readonly record struct BackendBody(int Index)
{
    public static BackendBody None => default;

    public bool IsNone => Index == 0;
}

/// <summary>What a backend needs to create a body (shapes come separately, through <see cref="PhysicsBackend3D.SetShapes"/>).</summary>
internal readonly record struct BodyDescription3D(
    PhysicsBodyKind Kind,
    Vector3 Position,
    Quaternion Rotation,
    uint Layer,
    uint Mask,
    BodyProperties3D Properties,
    float Friction,
    float Bounce);

/// <summary>
/// Simulation properties of a body. <see cref="Simulated"/> is true only for a dynamic <see cref="RigidBody3D"/>; every
/// other body gets <c>default</c> (no gravity, no axis locks: kinematic moves must reach their target).
/// </summary>
/// <param name="LinearDampingPerStep">Velocity removed per step, 0..1 (the engine converts the node's per-second damping).</param>
/// <param name="AngularDampingPerStep">Angular velocity removed per step, 0..1.</param>
/// <param name="GravityScale">Multiplier on the space's gravity (Godot semantics; 0 floats).</param>
internal readonly record struct BodyProperties3D(
    bool Simulated,
    float LinearDampingPerStep,
    float AngularDampingPerStep,
    float GravityScale,
    AxisLock AxisLocks,
    bool Ccd,
    bool CanSleep);

/// <summary>
/// A query or sweep hit. <see cref="ShapeIndex"/> is the index of the hit shape's description in the last
/// <see cref="PhysicsBackend3D.SetShapes"/> of <see cref="Body"/> (−1 when unknown). <see cref="Normal"/> is the hit
/// surface's normal facing the query (zero when the query starts inside); <see cref="Position"/> is on the hit body.
/// </summary>
internal struct BackendHit3D
{
    public BackendBody Body;
    public int ShapeIndex;
    public Vector3 Position;
    public Vector3 Normal;
    public float Fraction;
}

/// <summary>
/// The signed distance between one shape of a body and one nearby shape (<see cref="PhysicsBackend3D.Penetration"/>):
/// negative when they overlap. <see cref="Normal"/> points from the body's shape towards the other shape (any length; zero
/// when unknown); <see cref="Point"/> is the closest point on the other shape.
/// </summary>
internal readonly record struct BackendContact3D(float Distance, Vector3 Normal, Vector3 Point);

/// <summary>One shape of another body found near a body (<see cref="PhysicsBackend3D.CollectNearbyShapes"/>); <see cref="Shape"/> is backend-defined.</summary>
internal readonly record struct BackendShapeRef(BackendBody Body, int Shape);

/// <summary>A shape the backend could not build (<see cref="ShapeIndex"/> into the descriptions), or −1: the mass properties.</summary>
internal readonly record struct ShapeError3D(int ShapeIndex, string Message);
