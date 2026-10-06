using Jitter2;
using Jitter2.Collision;
using Jitter2.Collision.Shapes;
using Jitter2.LinearMath;

namespace MainframeEngine;

/// <summary>
/// One exact contact for spheres (ADR 0134). For overlapping convex pairs Jitter2 builds a manifold of auxiliary contact
/// points from support points sampled around the normal; on a sphere those land about 1 % of the radius off the line
/// through its centre, so the normal force carries a torque: a 4.5 cm ball resting on the ground spun up, rolled away
/// and slowly sank. A sphere touches at one point on that line (Godot's sphere collision guarantees it), so for every
/// overlapping pair with a sphere this filter registers that single contact itself and skips Jitter2's manifold. Against
/// a box the normal, points and depth are computed exactly (closest point on the box); against other shapes Jitter2's
/// normal and the other shape's point are kept. Speculative (not yet touching) contacts and spheres under a scale are
/// left to Jitter2. Runs after the filter it replaces (Jitter2's triangle edge filter). Thread-safe (no state).
/// </summary>
internal sealed class SphereContactFilter(World world, INarrowPhaseFilter? inner) : INarrowPhaseFilter
{
    public bool Filter(RigidBodyShape shapeA, RigidBodyShape shapeB, ref JVector pointA, ref JVector pointB, ref JVector normal,
        ref float penetration)
    {
        if (inner is not null && !inner.Filter(shapeA, shapeB, ref pointA, ref pointB, ref normal, ref penetration))
            return false;
        if (penetration < 0f)
            return true; // speculative: Jitter2 registers it with its own solve mode

        // Jitter2 2.9 passes the normal from A to B (its documentation says B to A; checked: a box A under a sphere B
        // gives +Y), A's point, B's point and a positive depth for overlap.
        var sphereA = TrySphere(shapeA, out var centerA, out var radiusA);
        var sphereB = TrySphere(shapeB, out var centerB, out var radiusB);
        if (!sphereA && !sphereB)
            return true;

        if (sphereA && TryBox(shapeB, out var boxB))
        {
            // The box's normal points to the sphere (B to A here).
            if (boxB.Contact(centerA, radiusA, out var onBox, out var n, out var depth))
            {
                normal = -n;
                pointB = onBox;
                penetration = depth;
            }
        }
        else if (sphereB && TryBox(shapeA, out var boxA))
        {
            if (boxA.Contact(centerB, radiusB, out var onBox, out var n, out var depth))
            {
                normal = n;
                pointA = onBox;
                penetration = depth;
            }
        }

        if (sphereA)
            pointA = centerA + normal * radiusA;
        if (sphereB)
            pointB = centerB - normal * radiusB;
        world.RegisterContact(shapeA.ShapeId, shapeB.ShapeId, shapeA.RigidBody, shapeB.RigidBody, in pointA, in pointB, in normal);
        return false;
    }

    private static bool TrySphere(RigidBodyShape shape, out JVector center, out float radius)
    {
        center = default;
        radius = 0f;
        if (shape.RigidBody is not { } body)
            return false;
        switch (shape)
        {
            case SphereShape sphere:
                center = body.Position;
                radius = sphere.Radius;
                return true;
            case TransformedShape { OriginalShape: SphereShape sphere } transformed when IsRotation(transformed.Transformation):
                center = body.Position + JVector.Transform(transformed.Translation, body.Orientation);
                radius = sphere.Radius;
                return true;
            default:
                return false;
        }
    }

    private static bool TryBox(RigidBodyShape shape, out Box box)
    {
        box = default;
        if (shape.RigidBody is not { } body)
            return false;
        switch (shape)
        {
            case BoxShape b:
                box = new Box(body.Position, body.Orientation, JVector.Zero, JMatrix.Identity, b.Size * 0.5f);
                return true;
            case TransformedShape { OriginalShape: BoxShape b } transformed when IsRotation(transformed.Transformation):
                box = new Box(body.Position, body.Orientation, transformed.Translation, transformed.Transformation, b.Size * 0.5f);
                return true;
            default:
                return false;
        }
    }

    private static bool IsRotation(in JMatrix m)
    {
        var x = JVector.Transform(JVector.UnitX, m);
        var y = JVector.Transform(JVector.UnitY, m);
        var z = JVector.Transform(JVector.UnitZ, m);
        return MathF.Abs(x.LengthSquared() - 1f) < 1e-4f && MathF.Abs(y.LengthSquared() - 1f) < 1e-4f &&
               MathF.Abs(z.LengthSquared() - 1f) < 1e-4f && MathF.Abs(JVector.Dot(x, y)) < 1e-4f &&
               MathF.Abs(JVector.Dot(y, z)) < 1e-4f && MathF.Abs(JVector.Dot(z, x)) < 1e-4f;
    }

    // A box in the world: body pose, then the shape's offset and rotation inside the body.
    private readonly record struct Box(JVector BodyPosition, JQuaternion BodyOrientation, JVector Translation, JMatrix Rotation, JVector Half)
    {
        /// <summary>The closest point on the box to <paramref name="center"/>, the normal towards the sphere and the depth.</summary>
        public bool Contact(JVector center, float radius, out JVector onBox, out JVector normal, out float depth)
        {
            onBox = normal = default;
            depth = 0f;
            var inBody = JVector.ConjugatedTransform(center - BodyPosition, BodyOrientation);
            var local = JVector.TransposedTransform(inBody - Translation, Rotation);
            var closest = new JVector(
                Math.Clamp(local.X, -Half.X, Half.X),
                Math.Clamp(local.Y, -Half.Y, Half.Y),
                Math.Clamp(local.Z, -Half.Z, Half.Z));
            var delta = local - closest;
            var distance = delta.Length();
            if (distance < 1e-6f)
                return false; // the centre is inside the box: Jitter2's EPA normal and depth stand

            var localNormal = delta * (1f / distance);
            normal = JVector.Transform(JVector.Transform(localNormal, Rotation), BodyOrientation);
            onBox = BodyPosition + JVector.Transform(JVector.Transform(closest, Rotation) + Translation, BodyOrientation);
            depth = radius - distance;
            return true;
        }
    }
}
