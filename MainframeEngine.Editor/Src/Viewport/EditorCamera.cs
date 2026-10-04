using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's own camera, independent of the scene's <see cref="Camera3D"/> nodes (Godot's editor camera): it orbits
/// a pivot at a distance (Alt+LMB or MMB), pans (Shift+MMB), zooms (wheel), flies (hold RMB + WASD/QE, mouse look) and
/// frames a selection (F). It drives a <see cref="PerspectiveCamera"/> set as the edited viewport's
/// <see cref="SceneViewport.CameraOverride"/>. Pure math, so it is unit-tested.
/// </summary>
public sealed class EditorCamera
{
    /// <summary>Degrees of rotation per pixel of mouse movement (orbit and look).</summary>
    public const float RotateDegreesPerPixel = 0.3f;

    private const float MinDistance = 0.05f;
    private const float MaxDistance = 20000f;

    public EditorCamera()
    {
        Reset();
    }

    /// <summary>The point the camera orbits around and looks at.</summary>
    public Vector3 Pivot { get; set; }

    /// <summary>Distance from the pivot to the camera.</summary>
    public float Distance
    {
        get;
        set => field = Math.Clamp(value, MinDistance, MaxDistance);
    }

    /// <summary>Heading in degrees around +Y (0 looks along -Z).</summary>
    public float Yaw { get; set; }

    /// <summary>Elevation in degrees (negative looks down), clamped to ±89.</summary>
    public float Pitch
    {
        get;
        set => field = Math.Clamp(value, -89f, 89f);
    }

    /// <summary>Vertical field of view in degrees.</summary>
    public float FieldOfView { get; set; } = 50f;

    /// <summary>Fly speed in units per second (the wheel changes it while flying).</summary>
    public float FlySpeed
    {
        get;
        set => field = Math.Clamp(value, 0.1f, 1000f);
    } = 6f;

    /// <summary>The math camera the viewport renders with (updated by <see cref="Apply"/>).</summary>
    public PerspectiveCamera RenderCamera { get; } = new() { Near = 0.05f, Far = 4000f };

    /// <summary>Unit view direction from <see cref="Yaw"/> and <see cref="Pitch"/>.</summary>
    public Vector3 Forward
    {
        get
        {
            var yaw = float.DegreesToRadians(Yaw);
            var pitch = float.DegreesToRadians(Pitch);
            return Vector3.Normalize(new Vector3(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch)));
        }
    }

    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public Vector3 Up => Vector3.Cross(Right, Forward);

    /// <summary>World position of the camera.</summary>
    public Vector3 Position => Pivot - Forward * Distance;

    /// <summary>Default view: looking at the origin from above and to the front.</summary>
    public void Reset()
    {
        Pivot = Vector3.Zero;
        Distance = 12f;
        Yaw = -30f;
        Pitch = -25f;
    }

    /// <summary>Rotates around the pivot by a mouse delta in pixels.</summary>
    public void Orbit(float dx, float dy)
    {
        Yaw -= dx * RotateDegreesPerPixel;
        Pitch -= dy * RotateDegreesPerPixel;
    }

    /// <summary>Rotates in place (fly mode mouse look): the camera position stays, the pivot moves.</summary>
    public void Look(float dx, float dy)
    {
        var position = Position;
        Orbit(dx, dy);
        Pivot = position + Forward * Distance;
    }

    /// <summary>
    /// Moves the camera and pivot in the view plane so the scene follows the mouse: <paramref name="dx"/>,
    /// <paramref name="dy"/> in pixels of a view <paramref name="viewHeightPixels"/> tall.
    /// </summary>
    public void Pan(float dx, float dy, float viewHeightPixels)
    {
        var unitsPerPixel = 2f * Distance * MathF.Tan(float.DegreesToRadians(FieldOfView) * 0.5f) / MathF.Max(1f, viewHeightPixels);
        Pivot += (-Right * dx + Up * dy) * unitsPerPixel;
    }

    /// <summary>Dolly towards (positive steps) or away from the pivot.</summary>
    public void Zoom(float steps) => Distance *= MathF.Pow(0.85f, steps);

    /// <summary>Flies: <paramref name="move"/> is (right, up, forward) in -1..1, scaled by <see cref="FlySpeed"/>.</summary>
    public void Fly(Vector3 move, float deltaTime, bool fast = false)
    {
        var speed = FlySpeed * (fast ? 4f : 1f) * deltaTime;
        Pivot += (Right * move.X + Vector3.UnitY * move.Y + Forward * move.Z) * speed;
    }

    /// <summary>Looks at <paramref name="center"/> from a distance that fits a sphere of <paramref name="radius"/>.</summary>
    public void Frame(Vector3 center, float radius)
    {
        Pivot = center;
        var halfFov = float.DegreesToRadians(FieldOfView) * 0.5f;
        Distance = MathF.Max(radius, 0.25f) / MathF.Sin(halfFov) * 1.6f;
    }

    /// <summary>Axis-aligned views: front (looking along -Z), right (along -X), top (down).</summary>
    public void SetView(EditorView view)
    {
        (Yaw, Pitch) = view switch
        {
            EditorView.Front => (0f, 0f),
            EditorView.Back => (180f, 0f),
            EditorView.Right => (90f, 0f),
            EditorView.Left => (-90f, 0f),
            EditorView.Top => (0f, -89f),
            EditorView.Bottom => (0f, 89f),
            _ => (Yaw, Pitch),
        };
    }

    /// <summary>Writes the pose into <see cref="RenderCamera"/> (call once per frame before rendering).</summary>
    public void Apply()
    {
        var camera = RenderCamera;
        camera.Position = Position;
        camera.Forward = Forward;
        camera.Up = Vector3.UnitY;
        camera.FieldOfView = FieldOfView;
        camera.Far = MathF.Max(4000f, Distance * 4f);
    }

    /// <summary>
    /// The world-space ray through pixel (<paramref name="x"/>, <paramref name="y"/>) of a <paramref name="width"/> ×
    /// <paramref name="height"/> view (origin top-left).
    /// </summary>
    public (Vector3 Origin, Vector3 Direction) Ray(float x, float y, float width, float height)
    {
        var ndcX = x / MathF.Max(1f, width) * 2f - 1f;
        var ndcY = 1f - y / MathF.Max(1f, height) * 2f;
        var tan = MathF.Tan(float.DegreesToRadians(FieldOfView) * 0.5f);
        var aspect = width / MathF.Max(1f, height);
        var direction = Forward + Right * (ndcX * tan * aspect) + Up * (ndcY * tan);
        return (Position, Vector3.Normalize(direction));
    }

    /// <summary>
    /// Projects a world point to view pixels (origin top-left); false when it is behind the camera.
    /// </summary>
    public bool Project(Vector3 point, float width, float height, out Vector2 pixel)
    {
        var relative = point - Position;
        var depth = Vector3.Dot(relative, Forward);
        pixel = default;
        if (depth <= 1e-4f)
            return false;
        var tan = MathF.Tan(float.DegreesToRadians(FieldOfView) * 0.5f);
        var aspect = width / MathF.Max(1f, height);
        var ndcX = Vector3.Dot(relative, Right) / (depth * tan * aspect);
        var ndcY = Vector3.Dot(relative, Up) / (depth * tan);
        pixel = new Vector2((ndcX + 1f) * 0.5f * width, (1f - ndcY) * 0.5f * height);
        return true;
    }

    /// <summary>World units covered by one pixel at <paramref name="point"/> (constant-size gizmos and icons).</summary>
    public float WorldPerPixel(Vector3 point, float viewHeightPixels)
    {
        var depth = MathF.Max(0.01f, Vector3.Dot(point - Position, Forward));
        return 2f * depth * MathF.Tan(float.DegreesToRadians(FieldOfView) * 0.5f) / MathF.Max(1f, viewHeightPixels);
    }
}

/// <summary>Axis-aligned editor views.</summary>
public enum EditorView
{
    Front,
    Back,
    Right,
    Left,
    Top,
    Bottom,
}
