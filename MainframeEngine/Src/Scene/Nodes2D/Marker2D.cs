namespace MainframeEngine;

/// <summary>
/// A position in 2D space and nothing else (Godot's <c>Marker2D</c>): spawn points and waypoints, found by type. The
/// editor draws a cross along its local axes, <see cref="GizmoExtents"/> long; nothing is drawn in game.
/// </summary>
[EditorIcon("crosshair", Family = EditorIconFamily.Space2D)]
public class Marker2D : Node2D
{
    /// <summary>Half-length of the editor's cross, canvas units (Godot's default 10).</summary>
    [Export]
    public float GizmoExtents { get; set; } = 10f;
}
