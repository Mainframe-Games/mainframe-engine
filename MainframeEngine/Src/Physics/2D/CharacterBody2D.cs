using System.Numerics;

namespace MainframeEngine;

/// <summary>A collision reported by <see cref="CharacterBody2D.MoveAndSlide"/>.</summary>
/// <param name="Position">Contact point (world pixels).</param>
/// <param name="Normal">Surface normal at the contact, pointing towards the character.</param>
/// <param name="Collider">The body hit.</param>
public readonly record struct KinematicCollision2D(Vector2 Position, Vector2 Normal, CollisionObject2D Collider)
{
    /// <summary>Angle in radians between the normal and <paramref name="up"/>.</summary>
    public float GetAngle(Vector2 up) => MathF.Acos(Math.Clamp(Vector2.Dot(Normal, Vector2.Normalize(up)), -1f, 1f));
}

/// <summary>How <see cref="CharacterBody2D.MoveAndSlide"/> treats surfaces (Godot's <c>motion_mode</c>).</summary>
public enum CharacterMotionMode2D : byte
{
    /// <summary>Floors, walls and ceilings against <see cref="CharacterBody2D.UpDirection"/> (side-on games).</summary>
    Grounded,

    /// <summary>Every surface is a wall and velocity is kept (top-down games): Godot's <c>_move_and_slide_floating</c>.</summary>
    Floating,
}

/// <summary>
/// A 2D body moved by code with collision response (Godot's <c>CharacterBody2D</c>), in pixels. The same
/// <see cref="MoveAndSlide"/> algorithm as <see cref="CharacterBody3D"/>: recover to <see cref="SafeMargin"/>, sweep and
/// slide (Box2D shape casts), classify floor/wall/ceiling against <see cref="UpDirection"/> (−Y: 2D space is Y-down, as in Godot),
/// snap to the floor.
/// </summary>
[EditorIcon("run")]
public class CharacterBody2D : PhysicsBody2D
{
    private const int MaxReportedCollisions = 16;

    private readonly KinematicCollision2D[] _collisions = new KinematicCollision2D[MaxReportedCollisions];
    private readonly CharacterContact2D[] _recoveryContacts = new CharacterContact2D[8];
    private int _collisionCount;
    private Vector2 _upDirection = -Vector2.UnitY;
    private float _floorMaxAngle = MathF.PI / 4;
    private bool _onFloor, _onWall, _onCeiling;
    private Vector2 _floorNormal, _wallNormal;
    private Vector2 _lastMotion;
    private Vector2 _realVelocity;

    /// <summary>Grounded (default) or floating, as Godot's <c>motion_mode</c>.</summary>
    [Export]
    public CharacterMotionMode2D MotionMode { get; set; }

    /// <summary>Floating mode: hits closer than this to head-on stop the motion instead of sliding (Godot's 15°).</summary>
    [Export(Range = "0,180,0.1")]
    public float WallMinSlideAngleDegrees { get; set; } = 15f;

    /// <summary>Velocity in px/s used (and updated) by <see cref="MoveAndSlide"/>.</summary>
    [Export]
    public Vector2 Velocity { get; set; }

    /// <summary>"Up" for floor/ceiling classification (normalized on set; default −Y, screen up).</summary>
    [Export]
    public Vector2 UpDirection
    {
        get => _upDirection;
        set
        {
            if (value.LengthSquared() < 1e-12f)
                throw new ArgumentException("UpDirection must not be zero.", nameof(value));
            _upDirection = Vector2.Normalize(value);
        }
    }

    [Export(Range = "0,180,0.1")]
    public float FloorMaxAngleDegrees
    {
        get => float.RadiansToDegrees(_floorMaxAngle);
        set => _floorMaxAngle = float.DegreesToRadians(Math.Clamp(value, 0f, 180f));
    }

    public float FloorMaxAngle
    {
        get => _floorMaxAngle;
        set => _floorMaxAngle = Math.Clamp(value, 0f, MathF.PI);
    }

    /// <summary>Snap distance (px) down onto the floor while walking (0 disables).</summary>
    [Export(Range = "0,100,0.1")]
    public float FloorSnapLength { get; set; } = 1f;

    [Export]
    public bool FloorStopOnSlope { get; set; } = true;

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

    /// <summary>Gap (px) kept between the body and what it touches; anything closer counts as touching.</summary>
    [Export(Range = "0.01,10,0.01")]
    public float SafeMargin
    {
        get;
        set
        {
            if (!(value > 0))
                throw new ArgumentOutOfRangeException(nameof(value), value, "SafeMargin must be positive.");
            field = value;
        }
    } = 1f;

    internal override PhysicsBodyKind Kind => PhysicsBodyKind.Character;

    public bool IsOnFloor() => _onFloor;

    public bool IsOnFloorOnly() => _onFloor && !_onWall && !_onCeiling;

    public bool IsOnWall() => _onWall;

    public bool IsOnWallOnly() => _onWall && !_onFloor && !_onCeiling;

    public bool IsOnCeiling() => _onCeiling;

    public bool IsOnCeilingOnly() => _onCeiling && !_onFloor && !_onWall;

    public Vector2 GetFloorNormal() => _floorNormal;

    public Vector2 GetWallNormal() => _wallNormal;

    public int GetSlideCollisionCount() => _collisionCount;

    public KinematicCollision2D GetSlideCollision(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _collisionCount);
        return _collisions[index];
    }

    public KinematicCollision2D? GetLastSlideCollision() => _collisionCount > 0 ? _collisions[_collisionCount - 1] : null;

    public Vector2 GetPositionDelta() => _lastMotion;

    public Vector2 GetRealVelocity() => _realVelocity;

    /// <summary>
    /// Moves the body by <see cref="Velocity"/> × the physics step, sliding along collisions; see
    /// <see cref="CharacterBody3D.MoveAndSlide"/>. Returns true if it collided.
    /// </summary>
    public bool MoveAndSlide()
    {
        if (Record is not { } record || Tree is not { } tree)
            return false;

        var delta = 1f / tree.PhysicsTicksPerSecond;
        if (MotionMode == CharacterMotionMode2D.Floating)
            return MoveAndSlideFloating(record, delta);
        var wasOnFloor = _onFloor;
        _onFloor = _onWall = _onCeiling = false;
        _floorNormal = _wallNormal = Vector2.Zero;
        _collisionCount = 0;

        var space = record.Space;
        var global = GlobalTransform;
        var start = global.Origin;
        var rotation = global.Rotation;
        var position = start;
        var margin = SafeMargin;
        var up = _upDirection;
        var velocity = Velocity;
        var intent = velocity;

        var contacts = _recoveryContacts.AsSpan();
        for (var pass = 0; pass < 4; pass++)
        {
            var found = space.RecoverBody(record, ref position, rotation, margin, contacts, out var pushed);
            for (var i = 0; i < found; i++)
                Classify(contacts[i].Normal, contacts[i].Point, contacts[i].Collider, up, ref velocity, report: pass == 0);
            if (!pushed)
                break;
        }

        var horizontalIntent = intent - up * Vector2.Dot(intent, up);
        var standingStill = horizontalIntent.LengthSquared() < 1e-6f;
        var motion = velocity * delta;
        if (_onFloor && FloorStopOnSlope && standingStill && Vector2.Dot(intent, up) <= 0)
            motion = Vector2.Zero;

        for (var slide = 0; slide < MaxSlides && motion.LengthSquared() > 1e-8f; slide++)
        {
            var length = motion.Length();
            var direction = motion / length;
            if (!space.CastBody(record, position, rotation, direction * (length + margin), out var hit))
            {
                position += motion;
                break;
            }

            var travel = Math.Clamp(hit.Fraction * (length + margin) - margin, 0f, length);
            position += direction * travel;
            if (hit.Normal == Vector2.Zero)
                break;

            var kind = Classify(hit.Normal, hit.Point, hit.Collider, up, ref velocity, report: true);
            var remaining = direction * (length - travel);
            if (kind == SurfaceKind.Floor && FloorStopOnSlope && standingStill)
                break;

            remaining -= hit.Normal * Vector2.Dot(remaining, hit.Normal);
            motion = remaining;
        }

        if (wasOnFloor && !_onFloor && FloorSnapLength > 0 && Vector2.Dot(velocity, up) <= 0)
        {
            var snapLength = FloorSnapLength + margin;
            if (space.CastBody(record, position, rotation, -up * snapLength, out var hit) && hit.Normal != Vector2.Zero &&
                IsFloor(hit.Normal, up))
            {
                position += -up * Math.Clamp(hit.Fraction * snapLength - margin, 0f, FloorSnapLength);
                Classify(hit.Normal, hit.Point, hit.Collider, up, ref velocity, report: false);
            }
        }

        if (_onFloor && Vector2.Dot(intent, up) <= 0)
            velocity -= up * Vector2.Dot(velocity, up);

        Velocity = velocity;
        _lastMotion = position - start;
        _realVelocity = _lastMotion / delta;
        if (_lastMotion != Vector2.Zero)
            GlobalPosition = position;
        return _collisionCount > 0;
    }

    // Godot's _move_and_slide_floating: each iteration is a move_and_collide (recovery counts as a collision), every hit
    // is a wall, the first slide keeps the motion's remaining length, velocity itself is never changed.
    private bool MoveAndSlideFloating(BodyRecord2D record, float delta)
    {
        const float AngleThreshold = 0.01f;
        _onFloor = _onWall = _onCeiling = false;
        _floorNormal = _wallNormal = Vector2.Zero;
        _collisionCount = 0;
        var space = record.Space;
        var global = GlobalTransform;
        var start = global.Origin;
        var rotation = global.Rotation;
        var position = start;
        var margin = SafeMargin;
        var velocity = Velocity;
        var motion = velocity * delta;
        var minSlide = float.DegreesToRadians(WallMinSlideAngleDegrees);
        var contacts = _recoveryContacts.AsSpan();
        var firstSlide = true;
        for (var iteration = 0; iteration < MaxSlides; iteration++)
        {
            var collided = false;
            var normal = Vector2.Zero;
            var point = position;
            CollisionObject2D? collider = null;
            var found = space.RecoverBody(record, ref position, rotation, margin, contacts, out _);
            if (found > 0)
            {
                collided = true;
                (normal, point, collider) = (contacts[0].Normal, contacts[0].Point, contacts[0].Collider);
            }

            var length = motion.Length();
            var travel = Vector2.Zero;
            if (length > 1e-8f)
            {
                var direction = motion / length;
                if (space.CastBody(record, position, rotation, direction * (length + margin), out var hit) && hit.Normal != Vector2.Zero)
                {
                    travel = direction * Math.Clamp(hit.Fraction * (length + margin) - margin, 0f, length);
                    collided = true;
                    (normal, point, collider) = (hit.Normal, hit.Point, hit.Collider);
                }
                else
                {
                    travel = motion;
                }
            }

            position += travel;
            if (collided)
            {
                _onWall = true;
                _wallNormal = normal;
                if (collider is not null && _collisionCount < MaxReportedCollisions)
                    _collisions[_collisionCount++] = new KinematicCollision2D(point, normal, collider);
                var remainder = motion - travel;
                if (remainder.LengthSquared() < 1e-10f)
                {
                    motion = Vector2.Zero;
                    break;
                }

                var head = velocity.LengthSquared() > 0 ? Vector2.Normalize(-velocity) : Vector2.Zero;
                if (minSlide != 0 && MathF.Acos(Math.Clamp(Vector2.Dot(normal, head), -1f, 1f)) < minSlide + AngleThreshold)
                    motion = Vector2.Zero;
                else if (firstSlide)
                {
                    var slide = remainder - normal * Vector2.Dot(remainder, normal);
                    motion = slide.LengthSquared() > 0 ? Vector2.Normalize(slide) * (motion.Length() - travel.Length()) : Vector2.Zero;
                }
                else
                {
                    motion = remainder - normal * Vector2.Dot(remainder, normal);
                }

                if (Vector2.Dot(motion, velocity) <= 0)
                    motion = Vector2.Zero;
            }

            if (!collided || motion.LengthSquared() < 1e-10f)
                break;
            firstSlide = false;
        }

        _lastMotion = position - start;
        _realVelocity = _lastMotion / delta;
        if (_lastMotion != Vector2.Zero)
            GlobalPosition = position;
        return _collisionCount > 0;
    }

    private enum SurfaceKind
    {
        Floor,
        Wall,
        Ceiling,
    }

    private bool IsFloor(Vector2 normal, Vector2 up) => MathF.Acos(Math.Clamp(Vector2.Dot(normal, up), -1f, 1f)) <= _floorMaxAngle + 0.001f;

    private SurfaceKind Classify(Vector2 normal, Vector2 point, CollisionObject2D collider, Vector2 up, ref Vector2 velocity, bool report)
    {
        SurfaceKind kind;
        if (IsFloor(normal, up))
        {
            kind = SurfaceKind.Floor;
            _onFloor = true;
            _floorNormal = normal;
        }
        else if (MathF.Acos(Math.Clamp(Vector2.Dot(normal, -up), -1f, 1f)) <= _floorMaxAngle + 0.001f)
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

        var into = Vector2.Dot(velocity, normal);
        if (into < 0)
            velocity -= normal * into;

        if (report && _collisionCount < MaxReportedCollisions)
            _collisions[_collisionCount++] = new KinematicCollision2D(point, normal, collider);
        return kind;
    }
}

internal struct CharacterContact2D
{
    public Vector2 Normal;
    public Vector2 Point;
    public CollisionObject2D Collider;
}

internal struct BodyCastHit2D
{
    public float Fraction;
    public Vector2 Normal;
    public Vector2 Point;
    public CollisionObject2D Collider;
}
