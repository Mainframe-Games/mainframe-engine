using System.Numerics;

namespace MainframeEngine;

/// <summary>A collision reported by <see cref="CharacterBody3D.MoveAndSlide"/>.</summary>
/// <param name="Position">Contact point (world space).</param>
/// <param name="Normal">Surface normal at the contact, pointing away from the collider (towards the character).</param>
/// <param name="Collider">The body hit.</param>
public readonly record struct KinematicCollision3D(Vector3 Position, Vector3 Normal, CollisionObject3D Collider)
{
    /// <summary>Angle in radians between the normal and <paramref name="up"/> (0 for flat floor).</summary>
    public float GetAngle(Vector3 up) => MathF.Acos(Math.Clamp(Vector3.Dot(Normal, Vector3.Normalize(up)), -1f, 1f));
}

/// <summary>
/// A body moved by code with collision response (Godot's <c>CharacterBody3D</c>): set <see cref="Velocity"/> and call
/// <see cref="MoveAndSlide"/> from <see cref="Node.OnPhysicsProcess"/>. It sweeps the body's shapes along the motion,
/// slides along what it hits (up to <see cref="MaxSlides"/> times), classifies contacts as floor, wall or ceiling
/// against <see cref="UpDirection"/> and <see cref="FloorMaxAngle"/>, and snaps down to the floor
/// (<see cref="FloorSnapLength"/>) when walking over edges and down slopes. Dynamic bodies it walks into are pushed
/// (it is a kinematic body in the simulation).
/// </summary>
[EditorIcon("run")]
public class CharacterBody3D : PhysicsBody3D
{
    private const int MaxReportedCollisions = 16;

    private readonly KinematicCollision3D[] _collisions = new KinematicCollision3D[MaxReportedCollisions];
    private readonly CharacterContact3D[] _recoveryContacts = new CharacterContact3D[8];
    private int _collisionCount;
    private Vector3 _upDirection = Vector3.UnitY;
    private float _floorMaxAngle = MathF.PI / 4;
    private bool _onFloor, _onWall, _onCeiling;
    private Vector3 _floorNormal, _wallNormal;
    private Vector3 _lastMotion;
    private Vector3 _realVelocity;

    /// <summary>Velocity in m/s used (and updated) by <see cref="MoveAndSlide"/>.</summary>
    [Export]
    public Vector3 Velocity { get; set; }

    /// <summary>World "up" for floor/ceiling classification (normalized on set).</summary>
    [Export]
    public Vector3 UpDirection
    {
        get => _upDirection;
        set
        {
            if (value.LengthSquared() < 1e-12f)
                throw new ArgumentException("UpDirection must not be zero.", nameof(value));
            _upDirection = Vector3.Normalize(value);
        }
    }

    /// <summary>Steepest slope (degrees) that still counts as floor.</summary>
    [Export(Range = "0,180,0.1")]
    public float FloorMaxAngleDegrees
    {
        get => float.RadiansToDegrees(_floorMaxAngle);
        set => _floorMaxAngle = float.DegreesToRadians(Math.Clamp(value, 0f, 180f));
    }

    /// <summary>Steepest slope (radians) that still counts as floor (default 45°).</summary>
    public float FloorMaxAngle
    {
        get => _floorMaxAngle;
        set => _floorMaxAngle = Math.Clamp(value, 0f, MathF.PI);
    }

    /// <summary>
    /// While on the floor and not moving up, the body is pulled down onto floor up to this far below (m), so it
    /// sticks to slopes and steps down small ledges. 0 disables snapping.
    /// </summary>
    [Export(Range = "0,10,0.01")]
    public float FloorSnapLength { get; set; } = 0.1f;

    /// <summary>Don't slide down floor slopes when the only motion is into the floor (gravity).</summary>
    [Export]
    public bool FloorStopOnSlope { get; set; } = true;

    /// <summary>Maximum slide iterations per <see cref="MoveAndSlide"/>.</summary>
    [Export(Range = "1,16,1")]
    public int MaxSlides
    {
        get;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            field = value;
        }
    } = 4;

    /// <summary>
    /// Gap (m) kept between the body and what it touches; anything closer counts as touching. Jitter2's sweeps resolve
    /// contacts to a few millimetres, so the default is 1 cm.
    /// </summary>
    [Export(Range = "0.001,0.1,0.001")]
    public float SafeMargin
    {
        get;
        set
        {
            if (!(value > 0))
                throw new ArgumentOutOfRangeException(nameof(value), value, "SafeMargin must be positive.");
            field = value;
        }
    } = 0.01f;

    internal override PhysicsBodyKind Kind => PhysicsBodyKind.Character;

    public bool IsOnFloor() => _onFloor;

    public bool IsOnFloorOnly() => _onFloor && !_onWall && !_onCeiling;

    public bool IsOnWall() => _onWall;

    public bool IsOnWallOnly() => _onWall && !_onFloor && !_onCeiling;

    public bool IsOnCeiling() => _onCeiling;

    public bool IsOnCeilingOnly() => _onCeiling && !_onFloor && !_onWall;

    /// <summary>Normal of the floor touched in the last <see cref="MoveAndSlide"/> (zero if none).</summary>
    public Vector3 GetFloorNormal() => _floorNormal;

    /// <summary>Normal of the wall touched in the last <see cref="MoveAndSlide"/> (zero if none).</summary>
    public Vector3 GetWallNormal() => _wallNormal;

    /// <summary>Collisions in the last <see cref="MoveAndSlide"/> (at most 16 are kept).</summary>
    public int GetSlideCollisionCount() => _collisionCount;

    public KinematicCollision3D GetSlideCollision(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _collisionCount);
        return _collisions[index];
    }

    /// <summary>The last collision of the last <see cref="MoveAndSlide"/>, if any.</summary>
    public KinematicCollision3D? GetLastSlideCollision() => _collisionCount > 0 ? _collisions[_collisionCount - 1] : null;

    /// <summary>How far the last <see cref="MoveAndSlide"/> moved the body.</summary>
    public Vector3 GetPositionDelta() => _lastMotion;

    /// <summary>The velocity actually achieved by the last <see cref="MoveAndSlide"/> (position delta / step).</summary>
    public Vector3 GetRealVelocity() => _realVelocity;

    /// <summary>
    /// Moves the body by <see cref="Velocity"/> × the physics step, sliding along collisions, and updates
    /// <see cref="Velocity"/> (velocity into surfaces is removed) and the floor/wall/ceiling state. Returns true if it
    /// collided. Call from <see cref="Node.OnPhysicsProcess"/>; outside a tree it does nothing.
    /// </summary>
    public bool MoveAndSlide()
    {
        if (Record is not { } record || Tree is not { } tree)
            return false;

        var delta = 1f / tree.PhysicsTicksPerSecond;
        var wasOnFloor = _onFloor;
        _onFloor = _onWall = _onCeiling = false;
        _floorNormal = _wallNormal = Vector3.Zero;
        _collisionCount = 0;

        var space = record.Space;
        var global = GlobalTransform;
        global.Decompose(out var start, out var rotation, out _);
        var position = start;
        var margin = SafeMargin;
        var up = _upDirection;
        var velocity = Velocity;

        // 1. Recover: back out of penetrations and to the safe margin from anything closer (those are contacts too:
        //    resting on the floor is a contact within the margin).
        var contacts = _recoveryContacts.AsSpan();
        for (var pass = 0; pass < 4; pass++)
        {
            var found = space.RecoverBody(record, ref position, rotation, margin, contacts, out var pushed);
            for (var i = 0; i < found; i++)
            {
                // A contact the body is leaving (a jump off the floor) is not touched this step, as in Godot, where
                // only the motion's collisions set the floor/wall/ceiling state (ADR 0128).
                if (Vector3.Dot(velocity, contacts[i].Normal) > 1e-4f)
                    continue;
                Classify(contacts[i].Normal, contacts[i].Point, contacts[i].Collider, up, ref velocity, report: pass == 0);
            }
            if (!pushed)
                break; // nothing closer than the margin is left
        }

        // 2. Slide. Standing on a slope with no sideways intent (only gravity): stay put rather than slide down it.
        var intent = Velocity;
        var horizontalIntent = intent - up * Vector3.Dot(intent, up);
        var standingStill = horizontalIntent.LengthSquared() < 1e-10f;
        var motion = velocity * delta;
        if (_onFloor && FloorStopOnSlope && standingStill && Vector3.Dot(intent, up) <= 0)
            motion = Vector3.Zero;
        CollisionObject3D? floorHit = null;
        for (var slide = 0; slide < MaxSlides && motion.LengthSquared() > 1e-12f; slide++)
        {
            // Cast a margin further than the motion: anything within the margin counts as touched (floor while resting).
            var length = motion.Length();
            var direction = motion / length;
            if (!space.CastBody(record, position, rotation, direction * (length + margin), out var hit))
            {
                position += motion;
                break;
            }

            var travel = Math.Clamp(hit.Fraction * (length + margin) - margin, 0f, length);
            position += direction * travel;
            if (hit.Normal == Vector3.Zero)
                break; // started overlapping (recovery could not separate): stay put

            var kind = Classify(hit.Normal, hit.Point, hit.Collider, up, ref velocity, report: true);
            if (kind == SurfaceKind.Floor)
                floorHit = hit.Collider;
            var remaining = direction * (length - travel);
            if (kind == SurfaceKind.Floor && FloorStopOnSlope && standingStill)
                break; // standing still on a slope: don't slide down it

            remaining -= hit.Normal * Vector3.Dot(remaining, hit.Normal);
            motion = remaining;
        }

        // 3. Snap down to the floor (stick to slopes and small steps down).
        if (wasOnFloor && !_onFloor && FloorSnapLength > 0 && Vector3.Dot(velocity, up) <= 0)
        {
            var snapLength = FloorSnapLength + margin;
            if (space.CastBody(record, position, rotation, -up * snapLength, out var hit) && hit.Normal != Vector3.Zero &&
                IsFloor(hit.Normal, up))
            {
                position += -up * Math.Clamp(hit.Fraction * snapLength - margin, 0f, FloorSnapLength);
                Classify(hit.Normal, hit.Point, hit.Collider, up, ref velocity, report: false);
                floorHit = hit.Collider;
            }
        }

        // 4. A cast that found the floor stops a little short of it: settle onto the margin with EPA (ADR 0128).
        if (floorHit is not null)
            space.SettleBody(record, ref position, rotation, margin, floorHit, maxGap: 0.02f);

        // On the floor, vertical velocity picked up by sliding along it (walking up a slope) is dropped, so stopping on
        // a slope doesn't launch the body; a jump (upward intent) is kept.
        if (_onFloor && Vector3.Dot(intent, up) <= 0)
            velocity -= up * Vector3.Dot(velocity, up);

        Velocity = velocity;
        _lastMotion = position - start;
        _realVelocity = _lastMotion / delta;
        if (_lastMotion != Vector3.Zero)
            GlobalPosition = position; // pushed to the physics body as a kinematic move this step
        return _collisionCount > 0;
    }

    private enum SurfaceKind
    {
        Floor,
        Wall,
        Ceiling,
    }

    private bool IsFloor(Vector3 normal, Vector3 up) => MathF.Acos(Math.Clamp(Vector3.Dot(normal, up), -1f, 1f)) <= _floorMaxAngle + 0.001f;

    private SurfaceKind Classify(Vector3 normal, Vector3 point, CollisionObject3D collider, Vector3 up, ref Vector3 velocity, bool report)
    {
        SurfaceKind kind;
        if (IsFloor(normal, up))
        {
            kind = SurfaceKind.Floor;
            _onFloor = true;
            _floorNormal = normal;
        }
        else if (MathF.Acos(Math.Clamp(Vector3.Dot(normal, -up), -1f, 1f)) <= _floorMaxAngle + 0.001f)
        {
            kind = SurfaceKind.Ceiling;
            _onCeiling = true;
        }
        else
        {
            kind = SurfaceKind.Wall;
            _onWall = true;
            _wallNormal = normal;
        }

        // Remove the velocity going into the surface.
        var into = Vector3.Dot(velocity, normal);
        if (into < 0)
            velocity -= normal * into;

        if (report && _collisionCount < MaxReportedCollisions)
            _collisions[_collisionCount++] = new KinematicCollision3D(point, normal, collider);
        return kind;
    }
}

/// <summary>A penetration found while recovering a character body (internal to <see cref="CharacterBody3D"/>).</summary>
internal struct CharacterContact3D
{
    public Vector3 Normal;
    public Vector3 Point;
    public CollisionObject3D Collider;
}

/// <summary>The first thing a swept body hits.</summary>
internal struct BodyCastHit3D
{
    public float Fraction;
    public Vector3 Normal;
    public Vector3 Point;
    public CollisionObject3D Collider;
}
