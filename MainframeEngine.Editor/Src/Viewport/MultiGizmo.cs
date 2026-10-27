using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>A node's global pose at the start of a multi-node gizmo drag (3D).</summary>
public readonly record struct GizmoPose3D(Vector3 Position, Quaternion Rotation, Vector3 Scale);

/// <summary>A node's global pose at the start of a multi-node gizmo drag (2D; rotation in radians).</summary>
public readonly record struct GizmoPose2D(Vector2 Position, float Rotation, Vector2 Scale);

/// <summary>
/// The math of the multi-node gizmo (editor-viewport-tools G7.1): <see cref="TransformGizmo"/> and
/// <see cref="TransformGizmo2D"/> drag one pose — the pivot's — and this turns the dragged pivot into a new pose for each
/// target. Translate moves every node by the pivot's (snapped) delta, along each node's own axis in Local space; rotate
/// and scale act around the pivot, or around each node's origin with <see cref="GizmoPivot.Individual"/>. Scale multiplies
/// each node's own scale by the factor per local axis (no shear, as in Godot). Pure functions, unit-tested.
/// </summary>
public static class MultiGizmo
{
    /// <summary>
    /// The gizmo's origin for <paramref name="positions"/>: their average (median and individual modes) or the active
    /// one's (<paramref name="active"/>, an index into the span).
    /// </summary>
    public static Vector3 Pivot(GizmoPivot mode, ReadOnlySpan<Vector3> positions, int active)
    {
        if (positions.IsEmpty)
            return Vector3.Zero;
        if (mode == GizmoPivot.Active && (uint)active < (uint)positions.Length)
            return positions[active];
        var sum = Vector3.Zero;
        foreach (var p in positions)
            sum += p;
        return sum / positions.Length;
    }

    /// <summary>
    /// The new global pose of a node that started at <paramref name="start"/>, for a gizmo whose pivot pose
    /// (<paramref name="pivot"/>, <paramref name="axes"/>, scale one) was dragged to (<paramref name="dragged"/>,
    /// <paramref name="draggedRotation"/>, <paramref name="factor"/>).
    /// </summary>
    public static GizmoPose3D Apply(GizmoMode mode, GizmoPivot pivotMode, GizmoHandle handle, bool local, Vector3 pivot, Quaternion axes,
        Vector3 dragged, Quaternion draggedRotation, Vector3 factor, in GizmoPose3D start)
    {
        switch (mode)
        {
            case GizmoMode.Translate:
                {
                    var move = dragged - pivot;
                    if (local && handle is GizmoHandle.X or GizmoHandle.Y or GizmoHandle.Z)
                    {
                        // Local: the same distance along each node's own corresponding axis (Godot).
                        var unit = handle switch { GizmoHandle.X => Vector3.UnitX, GizmoHandle.Y => Vector3.UnitY, _ => Vector3.UnitZ };
                        var along = Vector3.Dot(move, Vector3.Transform(unit, axes));
                        move = Vector3.Transform(unit, start.Rotation) * along;
                    }

                    return start with { Position = start.Position + move };
                }

            case GizmoMode.Rotate:
                {
                    var turn = Quaternion.Normalize(draggedRotation * Quaternion.Inverse(axes));
                    var origin = pivotMode == GizmoPivot.Individual ? start.Position : pivot;
                    return start with
                    {
                        Position = origin + Vector3.Transform(start.Position - origin, turn),
                        Rotation = Quaternion.Normalize(turn * start.Rotation),
                    };
                }

            case GizmoMode.Scale:
                {
                    var position = start.Position;
                    if (pivotMode != GizmoPivot.Individual)
                    {
                        // Positions scale about the pivot in the gizmo's (the active node's local) frame.
                        var inverse = Quaternion.Inverse(axes);
                        var offset = Vector3.Transform(start.Position - pivot, inverse) * factor;
                        position = pivot + Vector3.Transform(offset, axes);
                    }

                    return new GizmoPose3D(position, start.Rotation, start.Scale * factor);
                }

            default:
                return start;
        }
    }

    /// <summary>The 2D <see cref="Pivot(GizmoPivot, ReadOnlySpan{Vector3}, int)"/>.</summary>
    public static Vector2 Pivot(GizmoPivot mode, ReadOnlySpan<Vector2> positions, int active)
    {
        if (positions.IsEmpty)
            return Vector2.Zero;
        if (mode == GizmoPivot.Active && (uint)active < (uint)positions.Length)
            return positions[active];
        var sum = Vector2.Zero;
        foreach (var p in positions)
            sum += p;
        return sum / positions.Length;
    }

    /// <summary>
    /// The 2D <see cref="Apply(GizmoMode, GizmoPivot, GizmoHandle, bool, Vector3, Quaternion, Vector3, Quaternion, Vector3, in GizmoPose3D)"/>:
    /// <paramref name="move"/> is the pivot's (snapped) delta, <paramref name="angle"/> the turn in radians,
    /// <paramref name="factor"/> the scale factor along the gizmo's axes (rotated by <paramref name="axesRotation"/>).
    /// </summary>
    public static GizmoPose2D Apply(GizmoMode mode, GizmoPivot pivotMode, GizmoHandle handle, bool local, Vector2 pivot, float axesRotation,
        Vector2 move, float angle, Vector2 factor, in GizmoPose2D start)
    {
        switch (mode)
        {
            case GizmoMode.Translate:
                if (local && handle is GizmoHandle.X or GizmoHandle.Y)
                {
                    var along = Vector2.Dot(move, Axis(axesRotation, handle));
                    move = Axis(start.Rotation, handle) * along;
                }

                return start with { Position = start.Position + move };

            case GizmoMode.Rotate:
                {
                    var origin = pivotMode == GizmoPivot.Individual ? start.Position : pivot;
                    var (sin, cos) = MathF.SinCos(angle);
                    var offset = start.Position - origin;
                    var turned = new Vector2(offset.X * cos - offset.Y * sin, offset.X * sin + offset.Y * cos);
                    return start with { Position = origin + turned, Rotation = start.Rotation + angle };
                }

            case GizmoMode.Scale:
                {
                    var position = start.Position;
                    if (pivotMode != GizmoPivot.Individual)
                    {
                        var x = Axis(axesRotation, GizmoHandle.X);
                        var y = Axis(axesRotation, GizmoHandle.Y);
                        var offset = start.Position - pivot;
                        position = pivot + x * (Vector2.Dot(offset, x) * factor.X) + y * (Vector2.Dot(offset, y) * factor.Y);
                    }

                    return new GizmoPose2D(position, start.Rotation, start.Scale * factor);
                }

            default:
                return start;
        }
    }

    // The node's X or Y axis in the world for a global rotation (y down, as on screen).
    private static Vector2 Axis(float rotation, GizmoHandle handle)
    {
        var (sin, cos) = MathF.SinCos(rotation);
        return handle == GizmoHandle.Y ? new Vector2(-sin, cos) : new Vector2(cos, sin);
    }
}
