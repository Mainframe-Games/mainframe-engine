using System.Numerics;

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
    /// <paramref name="collisionMask"/>, skipping <paramref name="exclude"/>. Returns the closest hit. A convex shape
    /// containing <paramref name="from"/> is hit at fraction 0 with a zero normal, or skipped when
    /// <paramref name="hitFromInside"/> is false (Godot's default for <c>intersect_ray</c> and <see cref="RayCast3D"/>).
    /// </summary>
    public bool RayCast(Vector3 from, Vector3 to, out RayHit3D hit, uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null,
        bool hitFromInside = true)
    {
        hit = default;
        var direction = to - from;
        if (Space is not { } space || direction.LengthSquared() < 1e-14f)
            return false;
        space.FlushForQuery();
        if (!space.Backend.RayCast(from, direction, collisionMask, space.HandleOf(exclude), hitFromInside, out var result) ||
            space.RecordOf(result.Body) is not { } record)
            return false;
        hit = new RayHit3D(record.Node, record.ShapeOwner(result.ShapeIndex), result.Position, result.Normal, result.Fraction);
        return true;
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
        var query = shape.DescribeQuery();
        space.FlushForQuery();
        from.Decompose(out var position, out var rotation, out _);
        if (!space.Backend.ShapeCast(query, position, rotation, motion, collisionMask, space.HandleOf(exclude), out var result) ||
            space.RecordOf(result.Body) is not { } record)
            return false;
        hit = new ShapeCastHit3D(record.Node, record.ShapeOwner(result.ShapeIndex), result.Position, result.Normal, result.Fraction);
        return true;
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
        var query = shape.DescribeQuery();
        space.FlushForQuery();
        transform.Decompose(out var position, out var rotation, out _);
        var hits = space.QueryScratch;
        hits.Clear();
        space.Backend.IntersectShape(query, position, rotation, collisionMask, space.HandleOf(exclude), hits);
        return space.AddHits(hits, results);
    }

    /// <summary>Adds the bodies containing <paramref name="point"/> to <paramref name="results"/>; returns how many were added.</summary>
    public int IntersectPoint(Vector3 point, List<CollisionObject3D> results, uint collisionMask = CollisionLayers.All, CollisionObject3D? exclude = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (Space is not { } space)
            return 0;
        space.FlushForQuery();
        var hits = space.QueryScratch;
        hits.Clear();
        space.Backend.IntersectPoint(point, collisionMask, space.HandleOf(exclude), hits);
        return space.AddHits(hits, results);
    }
}
