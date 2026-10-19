namespace MainframeEngine;

/// <summary>
/// A region that detects bodies and other areas (Godot's <c>Area3D</c>): <see cref="BodyEntered"/>/<see cref="BodyExited"/>
/// fire when a static, rigid or character body whose layer is in <see cref="CollisionObject3D.CollisionMask"/> starts or
/// stops overlapping one of the area's shapes, and <see cref="AreaEntered"/>/<see cref="AreaExited"/> for a
/// <see cref="Monitorable"/> area on such a layer. Areas never collide or block. Overlaps are computed after each physics
/// step and signalled after it, on the main thread.
/// </summary>
[EditorIcon("border-corners")]
public class Area3D : CollisionObject3D
{
    private bool _monitoring = true;

    /// <summary>
    /// Detect bodies and other areas (when false, current overlaps are dropped with <see cref="BodyExited"/> and
    /// <see cref="AreaExited"/>).
    /// </summary>
    [Export]
    public bool Monitoring
    {
        get => _monitoring;
        set => _monitoring = value;
    }

    /// <summary>Other monitoring areas can detect this one (when false, they report it as exited after the next step).</summary>
    [Export]
    public bool Monitorable { get; set; } = true;

    /// <summary>A body started overlapping the area.</summary>
    [Signal]
    public event Action<Node3D>? BodyEntered;

    /// <summary>A body stopped overlapping the area, or left the tree while inside it.</summary>
    [Signal]
    public event Action<Node3D>? BodyExited;

    /// <summary>A monitorable area started overlapping this one.</summary>
    [Signal]
    public event Action<Area3D>? AreaEntered;

    /// <summary>
    /// An area stopped overlapping this one: it moved away, stopped being monitorable, lost its shapes, or left the tree
    /// while inside it.
    /// </summary>
    [Signal]
    public event Action<Area3D>? AreaExited;

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

    /// <summary>Adds the areas overlapping this one (as of the last physics step) to <paramref name="results"/>; returns how many.</summary>
    public int GetOverlappingAreas(List<Area3D> results)
    {
        ArgumentNullException.ThrowIfNull(results);
        if (Record?.AreaOverlaps is not { } overlaps)
            return 0;
        foreach (var other in overlaps.Keys)
            results.Add((Area3D)other.Node);
        return overlaps.Count;
    }

    /// <summary>True if any area overlaps this one (as of the last physics step).</summary>
    public bool HasOverlappingAreas() => Record is { AreaOverlaps.Count: > 0 };

    /// <summary>True if <paramref name="area"/> overlaps this one (as of the last physics step).</summary>
    public bool OverlapsArea(Area3D area) =>
        area is { Record: { } other } && Record?.AreaOverlaps is { } overlaps && overlaps.ContainsKey(other);

    internal override void EmitBodyEntered(Node3D body) => BodyEntered?.Invoke(body);

    internal override void EmitBodyExited(Node3D body) => BodyExited?.Invoke(body);

    internal void EmitAreaEntered(Area3D area) => AreaEntered?.Invoke(area);

    internal void EmitAreaExited(Area3D area) => AreaExited?.Invoke(area);
}
