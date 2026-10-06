using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A ray from the node's origin to <see cref="TargetPosition"/> (local space) that reports the closest body it hits
/// (Godot's <c>RayCast3D</c>). It is updated once per physics step, in its own <see cref="OnPhysicsProcess"/> (so in
/// tree order, after the nodes before it have moved, like Godot's internal physics process), or on demand with
/// <see cref="ForceRaycastUpdate"/>. The results are read with <see cref="IsColliding"/>, <see cref="GetCollider"/>,
/// <see cref="GetCollisionPoint"/> and <see cref="GetCollisionNormal"/>.
/// </summary>
/// <remarks>
/// Subclasses that override <see cref="OnPhysicsProcess"/> must call the base. Areas are never reported (the engine's
/// queries don't see them yet), so <see cref="CollideWithAreas"/> is stored only. Only the parent can be excluded.
/// </remarks>
[EditorIcon("target", Family = EditorIconFamily.Physics)]
public class RayCast3D : Node3D
{
    private RayHit3D _hit;
    private bool _colliding;

    /// <summary>Casts every physics step while true; when false, nothing is reported.</summary>
    [Export]
    public bool Enabled { get; set; } = true;

    /// <summary>The ray's end, in local space (Godot's default points 1 m down).</summary>
    [Export]
    public Vector3 TargetPosition { get; set; } = new(0, -1, 0);

    /// <summary>Layers the ray hits (bit i = layer i + 1).</summary>
    [Export(Flags = true)]
    public uint CollisionMask { get; set; } = CollisionLayers.Default;

    /// <summary>Skip the parent when it is a <see cref="CollisionObject3D"/>.</summary>
    [Export]
    public bool ExcludeParent { get; set; } = true;

    /// <summary>Report bodies.</summary>
    [Export]
    public bool CollideWithBodies { get; set; } = true;

    /// <summary>Report areas. Stored for Godot scenes; the engine's ray queries never see areas yet.</summary>
    [Export]
    public bool CollideWithAreas { get; set; }

    /// <summary>Report a convex shape that contains the ray's origin (at the origin, zero normal) instead of skipping it.</summary>
    [Export]
    public bool HitFromInside { get; set; }

    /// <summary>Whether the last update hit something.</summary>
    public bool IsColliding() => _colliding;

    /// <summary>The body hit by the last update, or null.</summary>
    public CollisionObject3D? GetCollider() => _colliding ? _hit.Collider : null;

    /// <summary>The shape hit by the last update, when known.</summary>
    public CollisionShape3D? GetColliderShape() => _colliding ? _hit.Shape : null;

    /// <summary>The hit point in world space (zero when nothing is hit).</summary>
    public Vector3 GetCollisionPoint() => _colliding ? _hit.Position : Vector3.Zero;

    /// <summary>The surface normal at the hit (zero when nothing is hit, or the ray started inside the shape).</summary>
    public Vector3 GetCollisionNormal() => _colliding ? _hit.Normal : Vector3.Zero;

    /// <summary>Casts now, from the current global transform, without waiting for the next physics step.</summary>
    public void ForceRaycastUpdate() => Cast();

    protected override void OnPhysicsProcess(float delta)
    {
        if (Enabled)
            Cast();
        else
            _colliding = false;
    }

    private void Cast()
    {
        _colliding = false;
        _hit = default;
        if (!CollideWithBodies || GetViewport()?.World3D is not { } world)
            return;
        var transform = GlobalTransform;
        var from = transform.Origin;
        var to = transform.TransformPoint(TargetPosition);
        var exclude = ExcludeParent ? Parent as CollisionObject3D : null;
        _colliding = world.DirectSpaceState.RayCast(from, to, out _hit, CollisionMask, exclude, HitFromInside);
    }
}
