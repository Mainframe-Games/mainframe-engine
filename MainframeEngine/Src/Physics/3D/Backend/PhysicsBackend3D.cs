using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// The 3D physics library behind one <see cref="PhysicsSpace3D"/> (ADR 0192, B2). The space owns the engine's behaviour
/// (records, dirty tracking, interpolation, user moves, the layer rule, signals, debug draw, MoveAndSlide); a backend
/// owns the library's world, bodies, shapes and queries. Abstract class, not an interface: sealed implementations, no
/// boxed handles. Every member is main-thread only, allocation-free once warm and called only between steps; the
/// lists passed in are owned by the space (members append to them; the space clears them).
/// </summary>
internal abstract class PhysicsBackend3D : IDisposable
{
    /// <summary>The backend for a new space; <paramref name="kind"/> is what the server resolved (B2: Jitter2 only).</summary>
    public static PhysicsBackend3D Create(PhysicsBackendKind kind, PhysicsSettings3D settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return kind switch
        {
            PhysicsBackendKind.Jitter2 => throw new NotSupportedException("The Jitter2 backend is added in the next task."),
            _ => throw new NotSupportedException($"The {kind} 3D physics backend is not part of this engine build yet."),
        };
    }

    // ---- world ---------------------------------------------------------------------------------------------------

    public abstract PhysicsBackendKind Kind { get; }

    /// <summary>Gravity in m/s².</summary>
    public abstract Vector3 Gravity { get; set; }

    /// <summary>Bodies currently awake.</summary>
    public abstract int ActiveBodyCount { get; }

    /// <summary>Bytes the library allocated on this thread inside its steps while <see cref="PhysicsSpace3D.MeasureLibraryAllocations"/> was set.</summary>
    public long LibraryAllocatedBytes { get; protected set; }

    /// <summary>One fixed step of <paramref name="dt"/> seconds (sub-steps are the backend's).</summary>
    public abstract void Step(float dt);

    public abstract void Dispose();

    // ---- bodies --------------------------------------------------------------------------------------------------

    /// <summary>A body without shapes (the space adds them with <see cref="SetShapes"/> at its next flush). Areas detect, never collide.</summary>
    public abstract BackendBody CreateBody(in BodyDescription3D desc);

    /// <summary>Removes the body now; its handle may be reused by the next <see cref="CreateBody"/>.</summary>
    public abstract void DestroyBody(BackendBody body);

    /// <summary>Static, kinematic, dynamic or character (never to or from <see cref="PhysicsBodyKind.Area"/>).</summary>
    public abstract void SetKind(BackendBody body, PhysicsBodyKind kind);

    /// <summary>
    /// Replaces the body's shapes. <paramref name="dynamicMass"/> is the mass (kg) of a dynamic body, inertia from the shapes;
    /// 0 for every other body. Shapes that cannot be built are skipped and reported in <paramref name="errors"/> (by
    /// description index; −1 when the mass properties failed and a unit inertia was used). Wakes a non-static body.
    /// </summary>
    public abstract void SetShapes(BackendBody body, ReadOnlySpan<ShapeDescription3D> shapes, float dynamicMass, List<ShapeError3D> errors);

    /// <summary>Layer and mask (what <see cref="PhysicsSpace3D.Collides"/> reads; area-ness comes from the kind).</summary>
    public abstract void SetFilter(BackendBody body, uint layer, uint mask);

    public abstract void SetProperties(BackendBody body, in BodyProperties3D props);

    public abstract void SetMaterial(BackendBody body, float friction, float bounce);

    // ---- state ---------------------------------------------------------------------------------------------------

    public abstract void GetPose(BackendBody body, out Vector3 position, out Quaternion rotation);

    /// <summary>Moves the body there (static and area moves, dynamic user moves); does not wake it.</summary>
    public abstract void SetPose(BackendBody body, Vector3 position, Quaternion rotation);

    /// <summary>Moves the body there, keeping its velocity, and wakes it unless it is static.</summary>
    public abstract void Teleport(BackendBody body, Vector3 position, Quaternion rotation);

    /// <summary>Gives a kinematic or character body the velocity that reaches the pose in one step of <paramref name="dt"/>.</summary>
    public abstract void MoveKinematic(BackendBody body, Vector3 position, Quaternion rotation, float dt);

    public abstract Vector3 GetLinearVelocity(BackendBody body);

    public abstract void SetLinearVelocity(BackendBody body, Vector3 velocity);

    public abstract Vector3 GetAngularVelocity(BackendBody body);

    public abstract void SetAngularVelocity(BackendBody body, Vector3 velocity);

    public abstract void ApplyForce(BackendBody body, Vector3 force, Vector3? position);

    public abstract void ApplyImpulse(BackendBody body, Vector3 impulse, Vector3? position);

    /// <summary>Adds the torque for the next step and wakes the body.</summary>
    public abstract void ApplyTorque(BackendBody body, Vector3 torque);

    public abstract bool IsSleeping(BackendBody body);

    public abstract void Wake(BackendBody body);

    /// <summary>Wakes every non-static body touching this one (before it leaves or changes its filter).</summary>
    public abstract void WakeTouching(BackendBody body);

    /// <summary>Drops the body's resting contacts so a new filter applies now.</summary>
    public abstract void ClearContacts(BackendBody body);

    // ---- scans (after Step) --------------------------------------------------------------------------------------

    /// <summary>Appends the bodies touching <paramref name="body"/> with intact contact points (may repeat a body).</summary>
    public abstract void CollectTouching(BackendBody body, List<BackendBody> others);

    /// <summary>Appends the bodies the area overlaps and detects (each once); false when the area has no shapes.</summary>
    public abstract bool CollectOverlaps(BackendBody area, List<BackendBody> others);

    // ---- queries (areas are never reported) ----------------------------------------------------------------------

    /// <summary>The closest body along <paramref name="motion"/> whose layer is in <paramref name="mask"/>; with <paramref name="hitFromInside"/> false, convex shapes containing <paramref name="from"/> are skipped.</summary>
    public abstract bool RayCast(Vector3 from, Vector3 motion, uint mask, BackendBody exclude, bool hitFromInside, out BackendHit3D hit);

    /// <summary>Sweeps a convex shape (at scale 1) along <paramref name="motion"/>; the first body it touches.</summary>
    public abstract bool ShapeCast(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, Vector3 motion, uint mask, BackendBody exclude,
        out BackendHit3D hit);

    /// <summary>Appends the bodies overlapping a convex shape (at scale 1), each once.</summary>
    public abstract void IntersectShape(in ShapeDescription3D shape, Vector3 position, Quaternion rotation, uint mask, BackendBody exclude,
        List<BackendBody> output);

    /// <summary>Appends the bodies containing <paramref name="point"/>, each once.</summary>
    public abstract void IntersectPoint(Vector3 point, uint mask, BackendBody exclude, List<BackendBody> output);

    // ---- character primitives (MoveAndSlide; the body's own mask, the body itself excluded) ----------------------

    /// <summary>Sweeps every shape of the body from a pose along <paramref name="motion"/>; the closest hit wins.</summary>
    public abstract bool CastBody(BackendBody body, Vector3 position, Quaternion rotation, Vector3 motion, out BackendHit3D hit);

    /// <summary>Shapes of the body as built (the <c>shape</c> argument of <see cref="Penetration"/> runs over 0..count−1).</summary>
    public abstract int GetShapeCount(BackendBody body);

    /// <summary>Appends the other bodies' shapes whose bounds come within <paramref name="expand"/> of the body's shapes at the pose.</summary>
    public abstract void CollectNearbyShapes(BackendBody body, Vector3 position, Quaternion rotation, float expand, List<BackendShapeRef> output);

    /// <summary>Signed distance from shape <paramref name="shape"/> of the body at the pose to <paramref name="other"/> (a hint: nothing beyond <paramref name="maxDistance"/> matters).</summary>
    public abstract bool Penetration(BackendBody body, int shape, Vector3 position, Quaternion rotation, BackendShapeRef other, float maxDistance,
        out BackendContact3D contact);

    /// <summary>The smallest signed distance between the body at the pose and <paramref name="other"/>, and the normal from the body towards it.</summary>
    public abstract bool Distance(BackendBody body, Vector3 position, Quaternion rotation, BackendBody other, out float distance, out Vector3 normal);
}
