namespace MainframeEngine;

/// <summary>
/// A position in 3D space and nothing else (Godot's <c>Marker3D</c>): spawn points and waypoints, found by type ("the
/// markers under X"). The editor draws its local axes, <see cref="GizmoExtents"/> long; nothing is drawn in game.
/// </summary>
[EditorIcon("crosshair")]
public class Marker3D : Node3D
{
    /// <summary>Length of the editor's axis cross along each local axis, world units (Godot's default 0.25).</summary>
    [Export]
    public float GizmoExtents { get; set; } = 0.25f;
}
