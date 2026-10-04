namespace MainframeEngine;

/// <summary>
/// A region that detects bodies (Godot's <c>Area3D</c>): <see cref="BodyEntered"/>/<see cref="BodyExited"/> fire when a
/// static, rigid or character body whose layer is in <see cref="CollisionObject3D.CollisionMask"/> starts or stops
/// overlapping one of the area's shapes. Areas never collide or block. Overlaps are computed after each physics step
/// and signalled after it, on the main thread.
/// </summary>
/// <remarks>Areas detect bodies, not other areas (Box2D 3.1 sensors cannot see sensors; both servers match).</remarks>
public class Area3D : CollisionObject3D
{
    private bool _monitoring = true;

    /// <summary>Detect bodies (when false, current overlaps are dropped with <see cref="BodyExited"/>).</summary>
    [Export]
    public bool Monitoring
    {
        get => _monitoring;
        set => _monitoring = value;
    }

    /// <summary>A body started overlapping the area.</summary>
    [Signal]
    public event Action<Node3D>? BodyEntered;

    /// <summary>A body stopped overlapping the area, or left the tree while inside it.</summary>
    [Signal]
    public event Action<Node3D>? BodyExited;

    internal override PhysicsBodyKind Kind => PhysicsBodyKind.Area;

    /// <summary>Adds the bodies overlapping the area (as of the last physics step) to <paramref name="results"/>; returns how many.</summary>
    public int GetOverlappingBodies(List<Node3D> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        return Record is { } record ? record.GetOverlaps(results) : 0;
    }

    /// <summary>True if any body overlaps the area (as of the last physics step).</summary>
    public bool HasOverlappingBodies() => Record is { Overlaps.Count: > 0 };

    /// <summary>True if <paramref name="body"/> overlaps the area (as of the last physics step).</summary>
    public bool OverlapsBody(Node3D body) =>
        body is CollisionObject3D { Record: { } other } && Record?.Overlaps is { } overlaps && overlaps.ContainsKey(other);

    internal override void EmitBodyEntered(Node3D body) => BodyEntered?.Invoke(body);

    internal override void EmitBodyExited(Node3D body) => BodyExited?.Invoke(body);
}
