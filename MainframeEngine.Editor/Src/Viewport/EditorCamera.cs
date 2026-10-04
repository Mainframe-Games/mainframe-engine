using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's own camera, independent of the scene's <see cref="Camera3D"/> nodes (Godot's editor camera): it orbits
/// a pivot at a distance (Alt+LMB or MMB), pans (Shift+MMB), zooms (wheel), flies (hold RMB + WASD/QE, mouse look) and
/// frames a selection (F). It drives a <see cref="PerspectiveCamera"/> set as the edited viewport's
/// <see cref="SceneViewport.CameraOverride"/>. In <see cref="Is2D"/> mode (scenes whose root is a <see cref="Node2D"/>)
/// it is an orthographic view of the z = 0 plane in pixels, y up: <see cref="Center2D"/> and <see cref="Zoom2D"/>
/// (screen pixels per world unit) drive an <see cref="OrthographicCamera"/>, and the projection helpers (<see cref="Ray"/>,
/// <see cref="Project"/>, <see cref="WorldPerPixel"/>) follow. Pure math, so it is unit-tested.
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

    /// <summary>The orthographic camera of the 2D mode (updated by <see cref="Apply(Vector2)"/>).</summary>
    public OrthographicCamera OrthoCamera { get; } = new();

    /// <summary>The camera to render with: <see cref="OrthoCamera"/> in 2D mode, else <see cref="RenderCamera"/>.</summary>
    public ICamera ActiveCamera => Is2D ? OrthoCamera : RenderCamera;

    /// <summary>2D editing: an orthographic view of the z = 0 plane (Node2D scenes; units are pixels, y up).</summary>
    public bool Is2D { get; set; }

    /// <summary>The 2D view's centre in world units.</summary>
    public Vector2 Center2D { get; set; }

    /// <summary>The 2D view's scale: screen (framebuffer) pixels per world unit, 1/64 … 64.</summary>
    public float Zoom2D
    {
        get;
        set => field = Math.Clamp(value, 1f / 64f, 64f);
    } = 1f;

    /// <summary>Distance of the 2D camera from the z = 0 plane (inside the orthographic depth range).</summary>
    public const float Depth2D = 500f;

    /// <summary>Unit view direction from <see cref="Yaw"/> and <see cref="Pitch"/> (-Z in 2D mode).</summary>
    public Vector3 Forward
    {
        get
        {
            if (Is2D)
                return -Vector3.UnitZ;
            var yaw = float.DegreesToRadians(Yaw);
            var pitch = float.DegreesToRadians(Pitch);
            return Vector3.Normalize(new Vector3(-MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), -MathF.Cos(yaw) * MathF.Cos(pitch)));
        }
    }

    public Vector3 Right => Is2D ? Vector3.UnitX : Vector3.Normalize(Vector3.Cross(Forward, Vector3.UnitY));

    public Vector3 Up => Is2D ? Vector3.UnitY : Vector3.Cross(Right, Forward);

    /// <summary>World position of the camera.</summary>
    public Vector3 Position => Is2D ? new Vector3(Center2D, Depth2D) : Pivot - Forward * Distance;

    /// <summary>Copies the whole view state (3D pose, 2D view, fly speed) from <paramref name="other"/>.</summary>
    public void CopyFrom(EditorCamera other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Pivot = other.Pivot;
        Distance = other.Distance;
        Yaw = other.Yaw;
        Pitch = other.Pitch;
        FieldOfView = other.FieldOfView;
        FlySpeed = other.FlySpeed;
        Is2D = other.Is2D;
        Center2D = other.Center2D;
        Zoom2D = other.Zoom2D;
    }

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

    /// <summary>
    /// Writes the pose into the active camera for a view of <paramref name="viewPixels"/> (call once per frame before
    /// rendering): the perspective camera, or in 2D mode the orthographic one (its size is the view in world units).
    /// </summary>
    public void Apply(Vector2 viewPixels)
    {
        if (!Is2D)
        {
            Apply();
            return;
        }

        var camera = OrthoCamera;
        camera.Position = new Vector3(Center2D, Depth2D);
        camera.Forward = -Vector3.UnitZ;
        camera.Up = Vector3.UnitY;
        camera.Zoom = 1f;
        camera.Size = new Vector2(MathF.Max(1f, viewPixels.X), MathF.Max(1f, viewPixels.Y)) / Zoom2D;
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
        if (Is2D)
            return (new Vector3(ScreenToWorld2D(new Vector2(x, y), new Vector2(width, height)), Depth2D), -Vector3.UnitZ);
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
        if (Is2D)
        {
            pixel = WorldToScreen2D(new Vector2(point.X, point.Y), new Vector2(width, height));
            return true;
        }

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
        if (Is2D)
            return 1f / Zoom2D;
        var depth = MathF.Max(0.01f, Vector3.Dot(point - Position, Forward));
        return 2f * depth * MathF.Tan(float.DegreesToRadians(FieldOfView) * 0.5f) / MathF.Max(1f, viewHeightPixels);
    }

    // ── 2D mode ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>View pixel (origin top-left) → world point on the z = 0 plane (2D mode).</summary>
    public Vector2 ScreenToWorld2D(Vector2 pixel, Vector2 viewPixels) =>
        Center2D + new Vector2(pixel.X - viewPixels.X * 0.5f, viewPixels.Y * 0.5f - pixel.Y) / Zoom2D;

    /// <summary>World point → view pixel (origin top-left) in 2D mode.</summary>
    public Vector2 WorldToScreen2D(Vector2 world, Vector2 viewPixels)
    {
        var d = (world - Center2D) * Zoom2D;
        return new Vector2(viewPixels.X * 0.5f + d.X, viewPixels.Y * 0.5f - d.Y);
    }

    /// <summary>Moves the 2D view so the scene follows the mouse (<paramref name="dx"/>, <paramref name="dy"/> in pixels, y down).</summary>
    public void Pan2D(float dx, float dy) => Center2D += new Vector2(-dx, dy) / Zoom2D;

    /// <summary>
    /// Zooms the 2D view by <paramref name="steps"/> wheel steps (positive: closer) keeping the world point under view
    /// pixel <paramref name="anchor"/> where it is.
    /// </summary>
    public void Zoom2DAt(float steps, Vector2 anchor, Vector2 viewPixels)
    {
        var before = ScreenToWorld2D(anchor, viewPixels);
        Zoom2D *= MathF.Pow(1.25f, steps);
        var after = ScreenToWorld2D(anchor, viewPixels);
        Center2D += before - after;
    }

    /// <summary>Centres the 2D view on a world rectangle and zooms so it fills ~80% of the view (at most 4×).</summary>
    public void Frame2D(Vector2 min, Vector2 max, Vector2 viewPixels)
    {
        Center2D = (min + max) * 0.5f;
        var size = Vector2.Max(max - min, new Vector2(16f));
        var fit = MathF.Min(viewPixels.X / size.X, viewPixels.Y / size.Y) * 0.8f;
        Zoom2D = MathF.Min(4f, fit);
    }

    /// <summary>The 2D view's default: the origin at 1:1.</summary>
    public void Reset2D()
    {
        Center2D = Vector2.Zero;
        Zoom2D = 1f;
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
