using System.Numerics;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>A ray hit from <see cref="PhysicsDirectSpaceState3D.RayCast"/>.</summary>
/// <param name="Collider">The body hit.</param>
/// <param name="Shape">The <see cref="CollisionShape3D"/> hit, when known.</param>
/// <param name="Position">Hit point (world space).</param>
/// <param name="Normal">Surface normal at the hit (zero when the ray starts inside the shape).</param>
/// <param name="Fraction">Where along <c>from → to</c> the hit is (0..1).</param>
public readonly record struct RayHit3D(CollisionObject3D Collider, CollisionShape3D? Shape, Vector3 Position, Vector3 Normal, float Fraction);

/// <summary>The first contact of a swept shape from <see cref="PhysicsDirectSpaceState3D.ShapeCast"/>.</summary>
/// <param name="Collider">The body hit.</param>
/// <param name="Shape">The <see cref="CollisionShape3D"/> hit, when known.</param>
/// <param name="Position">Contact point on the hit body (world space).</param>
/// <param name="Normal">Surface normal of the hit body at the contact (zero when the shape starts overlapping).</param>
/// <param name="Fraction">Safe fraction of the motion (0..1) before contact.</param>
public readonly record struct ShapeCastHit3D(CollisionObject3D Collider, CollisionShape3D? Shape, Vector3 Position, Vector3 Normal, float Fraction);

/// <summary>
/// Immediate queries against a <see cref="World3D"/>'s physics (Godot's <c>PhysicsDirectSpaceState3D</c>): raycasts,
/// shape casts and overlap tests. Obtain it from <see cref="World3D.DirectSpaceState"/>. Queries see the bodies as of
/// the last physics step plus node moves made since (pushed before the query); kinematic and character moves apply at
/// the next step. Areas are never reported. Main thread only; queries do not allocate.
/// </summary>
public sealed class PhysicsDirectSpaceState3D
{
    private readonly World3D _world;

    internal PhysicsDirectSpaceState3D(World3D world)
    {
        _world = world;
    }

    private PhysicsSpace3D? Space => _world.PhysicsSpace;

    /// <summary>
    /// Casts a ray from <paramref name="from"/> to <paramref name="to"/> against bodies whose layer is in
    /// <paramref name="collisionMask"/>, skipping <paramref name="exclude"/>. Returns the closest hit.
    /// </summary>
    public bool RayCast(Vector3 from, Vector3 to, out RayHit3D hit, uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null)
    {
        hit = default;
        var direction = to - from;
        if (Space is not { } space || direction.LengthSquared() < 1e-14f)
            return false;
        space.FlushForQuery();
        var filters = space.Filters;
        filters.Begin(collisionMask, exclude?.Record?.Body);
        try
        {
            if (!space.Tree.RayCast(from.ToJ(), direction.ToJ(), 1f, filters.RayPre, null, out var proxy, out var normal, out var lambda) ||
                proxy is not RigidBodyShape { RigidBody.Tag: BodyRecord3D record })
                return false;
            hit = new RayHit3D(record.Node, space.FindShapeOwner(proxy), from + direction * lambda, normal.ToNumerics(), lambda);
            return true;
        }
        finally
        {
            filters.End();
        }
    }

    /// <summary>
    /// Sweeps <paramref name="shape"/> (convex; at scale 1) from <paramref name="from"/> along <paramref name="motion"/>
    /// and returns the first body it would touch.
    /// </summary>
    public bool ShapeCast(Shape3D shape, in Transform3D from, Vector3 motion, out ShapeCastHit3D hit,
        uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        hit = default;
        if (Space is not { } space || motion.LengthSquared() < 1e-14f)
            return false;
        var query = shape.GetQueryShape();
        space.FlushForQuery();
        from.Decompose(out var position, out var rotation, out _);
        var filters = space.Filters;
        filters.Begin(collisionMask, exclude?.Record?.Body);
        try
        {
            if (!space.Tree.SweepCast(query, rotation.ToJ(), position.ToJ(), motion.ToJ(), 1f, filters.SweepPre, null,
                    out var proxy, out _, out var pointB, out var normal, out var lambda) ||
                proxy is not RigidBodyShape { RigidBody.Tag: BodyRecord3D record })
                return false;
            var surface = normal.ToNumerics() == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(-normal.ToNumerics());
            hit = new ShapeCastHit3D(record.Node, space.FindShapeOwner(proxy), pointB.ToNumerics(), surface, lambda);
            return true;
        }
        finally
        {
            filters.End();
        }
    }

    /// <summary>
    /// Adds the bodies overlapping <paramref name="shape"/> (convex; at scale 1) placed at <paramref name="transform"/> to
    /// <paramref name="results"/> (each once) and returns how many were added.
    /// </summary>
    public int IntersectShape(Shape3D shape, in Transform3D transform, List<CollisionObject3D> results,
        uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(results);
        if (Space is not { } space)
            return 0;
        var query = shape.GetQueryShape();
        space.FlushForQuery();
        transform.Decompose(out var position, out var rotation, out _);
        var positionJ = position.ToJ();
        var rotationJ = rotation.ToJ();
        query.CalculateBoundingBox(rotationJ, positionJ, out var box);
        return Collect(space, box, results, collisionMask, exclude, (query, positionJ, rotationJ),
            static (state, other, otherBody) => NarrowPhase.Overlap(state.query, other, state.rotationJ, otherBody.Orientation, state.positionJ, otherBody.Position));
    }

    /// <summary>Adds the bodies containing <paramref name="point"/> to <paramref name="results"/>; returns how many were added.</summary>
    public int IntersectPoint(Vector3 point, List<CollisionObject3D> results, uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (Space is not { } space)
            return 0;
        space.FlushForQuery();
        var p = point.ToJ();
        var box = new JBoundingBox(p - new JVector(1e-4f), p + new JVector(1e-4f));
        return Collect(space, box, results, collisionMask, exclude, p,
            static (state, other, otherBody) =>
                NarrowPhase.PointTest(other, JMatrix.CreateFromQuaternion(otherBody.Orientation), otherBody.Position, state));
    }

    private static int Collect<TState>(PhysicsSpace3D space, in JBoundingBox box, List<CollisionObject3D> results, uint mask,
        CollisionObject3D? exclude, TState state, Func<TState, RigidBodyShape, Jitter2.Dynamics.RigidBody, bool> test)
    {
        var proxies = space.ProxyScratch;
        proxies.Clear();
        var sink = new PhysicsSpace3D.ProxySink(proxies);
        space.Tree.Query(ref sink, box);
        var excluded = exclude?.Record?.Body;
        var added = 0;
        try
        {
            foreach (var proxy in proxies)
            {
                if (proxy is not RigidBodyShape { RigidBody: { } body } shape || ReferenceEquals(body, excluded) ||
                    body.Tag is not BodyRecord3D { Removed: false } record || (record.Layer & mask) == 0 || results.Contains(record.Node))
                    continue;
                if (!test(state, shape, body))
                    continue;
                results.Add(record.Node);
                added++;
            }
        }
        finally
        {
            proxies.Clear();
        }

        return added;
    }
}
