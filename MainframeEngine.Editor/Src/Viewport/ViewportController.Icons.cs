using System.Numerics;

namespace MainframeEngine.Editor;

// Scene icons (lights, cameras, audio, decals): an SVG badge per kind, rasterised at the view's pixel scale into a gizmo icon
// atlas and drawn on top as a billboard with a coloured ring (the light's colour, or the selection colour); their shapes
// (direction, cone, frustum, ranges) as depth-tested gizmo lines.
public sealed partial class ViewportController
{
    /// <summary>Scene icon diameter, in points.</summary>
    internal const float IconPoints = 28f;

    private const float ShapeWidth = 1.5f;    // points: a light's direction, cone and range, a camera's frustum
    private const float RingWidth = 2f;       // points: the ring around an icon
    private const float SelectedRingWidth = 3f;

    private static readonly string[] IconFiles = ["light-directional", "light-omni", "light-spot", "camera", "audio", "audio-listener", "decal"];
    private static byte[][]? s_iconSvgs;

    private GizmoIconAtlas? _iconAtlas;
    private float _iconAtlasScale;

    private enum SceneIcon
    {
        DirectionalLight,
        OmniLight,
        SpotLight,
        Camera,
        Audio,
        AudioListener,
        Decal,
    }

    // The atlas at this view's scale: the SVGs rasterised once per scale (load time; the view scale changes rarely).
    private void EnsureIconAtlas()
    {
        var scale = ViewScale;
        if (_iconAtlas is not null && _iconAtlasScale == scale)
            return;
        s_iconSvgs ??= Array.ConvertAll(IconFiles, static name => File.ReadAllBytes(ContentPaths.Resolve($"Assets/gizmos/{name}.svg")));
        var size = Math.Max(8, (int)MathF.Round(IconPoints * scale));
        // One row after the atlas's white block.
        var side = (int)BitOperations.RoundUpToPowerOf2((uint)(IconFiles.Length * (size + 2) + 16));
        var height = (int)BitOperations.RoundUpToPowerOf2((uint)(size + 16));
        _iconAtlas?.Dispose();
        _iconAtlas = new GizmoIconAtlas(side, height);
        foreach (var svg in s_iconSvgs)
        {
            var (rgba, width, h) = Svg.Rasterize(svg, size / Svg.Size(svg).Width);
            _iconAtlas.Add(rgba, width, h);
        }

        _iconAtlasScale = scale;
    }

    private void DrawIcon(EditedScene scene, DebugLines lines, Vector2 pixels, Node3D node, bool selected)
    {
        var camera = scene.Camera;
        var shapes = scene.Viewport.Gizmos;
        var icons = scene.Viewport.OverlayGizmos;
        var p = node.GlobalPosition;
        var unit = camera.WorldPerPixel(p, pixels.Y) * ViewScale; // world units per point here
        var width = ShapeWidth * ViewScale;
        var forward = node.GlobalForward;
        Vector4 accent;
        SceneIcon icon;
        switch (node)
        {
            case DirectionalLight3D light:
                icon = SceneIcon.DirectionalLight;
                accent = LightAccent(light);
                Arrow(shapes, p + forward * (unit * IconPoints * 0.6f), forward, unit * 70f, unit, accent, width);
                break;
            case OmniLight3D omni:
                icon = SceneIcon.OmniLight;
                accent = LightAccent(omni);
                if (selected)
                    RangeSphere(shapes, camera, p, omni.Range, accent, width);
                break;
            case SpotLight3D spot:
                icon = SceneIcon.SpotLight;
                accent = LightAccent(spot);
                var length = selected ? spot.Range : unit * 60f;
                var radius = MathF.Tan(float.DegreesToRadians(Math.Clamp(spot.OuterConeAngle, 1f, 89f))) * length;
                var (u, v) = TransformGizmo.Perpendiculars(forward);
                var end = p + forward * length;
                var cone = accent with { W = accent.W * (selected ? 0.9f : 0.6f) };
                for (var i = 0; i < 4; i++)
                {
                    var a = i * MathF.Tau / 4f;
                    shapes.Line(p, end + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius, cone, width);
                }

                shapes.Circle(end, u, v, radius, cone, width, 48);
                break;
            case Camera3D cam:
                icon = SceneIcon.Camera;
                accent = CameraColor;
                DrawCamera(shapes, cam, unit, selected, width);
                break;
            case Marker3D marker:
                DrawMarker3D(lines, marker, selected);
                return;
            case AudioPlayer3D audio:
                icon = SceneIcon.Audio;
                accent = AudioColor;
                if (selected && audio.MaxDistance > 0)
                    RangeSphere(shapes, camera, p, audio.MaxDistance, AudioColor, width);
                break;
            case Decal decal:
                icon = SceneIcon.Decal;
                accent = DecalColor;
                if (!selected) // selected, its handle provider draws the box and the projection arrow
                    Arrow(shapes, p, -decal.GlobalUp, unit * 40f, unit, DecalColor with { W = 0.6f }, width);
                break;
            default:
                icon = SceneIcon.AudioListener;
                accent = AudioColor;
                break;
        }

        // The badge: a billboard IconPoints across, ringed in the accent colour (the selection colour when selected).
        var half = unit * IconPoints * 0.5f;
        icons.Icon((int)icon, p, camera.Right * half, camera.Up * half, Vector4.One);
        var ring = selected ? SelectionColor : accent with { W = 1f };
        icons.Circle(p, camera.Right, camera.Up, half - unit * 0.5f, ring, (selected ? SelectedRingWidth : RingWidth) * ViewScale, 48);
    }

    // The light's colour as an sRGB ring colour, at full brightness (a dim or deep colour still reads).
    private static Vector4 LightAccent(Light3D light)
    {
        var c = light.Color;
        var max = MathF.Max(MathF.Max(c.X, c.Y), MathF.Max(c.Z, 1e-4f));
        c /= max;
        return new Vector4(MathF.Pow(c.X, 1f / 2.2f), MathF.Pow(c.Y, 1f / 2.2f), MathF.Pow(c.Z, 1f / 2.2f), 0.95f);
    }

    // A shaft with an arrow head of four lines (a directional light's direction).
    private static void Arrow(GizmoDrawList list, Vector3 from, Vector3 direction, float length, float unit, Vector4 color, float width)
    {
        var tip = from + direction * length;
        list.Line(from, tip, color, width);
        var (u, v) = TransformGizmo.Perpendiculars(direction);
        var back = tip - direction * (unit * 10f);
        var spread = unit * 5f;
        list.Line(tip, back + u * spread, color, width);
        list.Line(tip, back - u * spread, color, width);
        list.Line(tip, back + v * spread, color, width);
        list.Line(tip, back - v * spread, color, width);
    }

    // A range: three faint great circles and the outline as the camera sees the sphere.
    private static void RangeSphere(GizmoDrawList list, EditorCamera camera, Vector3 center, float radius, Vector4 color, float width)
    {
        var faint = color with { W = 0.3f };
        list.Circle(center, Vector3.UnitX, Vector3.UnitY, radius, faint, width, 64);
        list.Circle(center, Vector3.UnitY, Vector3.UnitZ, radius, faint, width, 64);
        list.Circle(center, Vector3.UnitZ, Vector3.UnitX, radius, faint, width, 64);
        var toCenter = center - camera.Position;
        var distance = toCenter.Length();
        if (distance <= radius)
            return;
        // The silhouette circle: where the view rays graze the sphere.
        var axis = toCenter / distance;
        var silhouetteRadius = radius * MathF.Sqrt(1f - radius * radius / (distance * distance));
        var (u, v) = TransformGizmo.Perpendiculars(axis);
        list.Circle(center - axis * (radius * radius / distance), u, v, silhouetteRadius, color with { W = 0.6f }, width, 96);
    }

    private static void DrawCamera(GizmoDrawList list, Camera3D camera, float unit, bool selected, float width)
    {
        var global = camera.GlobalTransform;
        var p = global.Origin;
        var forward = -Vector3.Normalize(global.Basis.Z);
        var right = Vector3.Normalize(global.Basis.X);
        var up = Vector3.Normalize(global.Basis.Y);
        var depth = selected ? MathF.Min(camera.Far, 6f) : unit * 40f;
        var halfHeight = MathF.Tan(float.DegreesToRadians(camera.Fov) * 0.5f) * depth;
        var halfWidth = halfHeight * 16f / 9f;
        var center = p + forward * depth;
        Span<Vector3> corners =
        [
            center + right * halfWidth + up * halfHeight,
            center - right * halfWidth + up * halfHeight,
            center - right * halfWidth - up * halfHeight,
            center + right * halfWidth - up * halfHeight,
        ];
        var color = CameraColor with { W = selected ? 0.95f : 0.7f };
        for (var i = 0; i < 4; i++)
        {
            list.Line(p, corners[i], color, width);
            list.Line(corners[i], corners[(i + 1) % 4], color, width);
        }

        // A solid "up" triangle over the frame.
        var top = center + up * halfHeight * 1.4f;
        var inset = halfWidth * 0.35f;
        list.Triangle(center + up * halfHeight * 1.08f + right * inset, top, center + up * halfHeight * 1.08f - right * inset, color with { W = color.W * 0.8f });
    }
}
