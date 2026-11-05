using System.Globalization;
using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>Line helpers for handle providers (overlay lines, no depth test).</summary>
public static class HandleDrawing
{
    /// <summary>A circle of <paramref name="radius"/> around <paramref name="center"/> in the plane of <paramref name="u"/> and <paramref name="v"/>.</summary>
    public static void Circle(DebugLines lines, Vector3 center, Vector3 u, Vector3 v, float radius, Vector4 color, int segments = 48)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var previous = center + u * radius;
        for (var i = 1; i <= segments; i++)
        {
            var a = i * MathF.Tau / segments;
            var point = center + (u * MathF.Cos(a) + v * MathF.Sin(a)) * radius;
            lines.AddLine(previous, point, color);
            previous = point;
        }
    }

    /// <summary>The handle's square, <paramref name="half"/> world units from its centre along the camera's axes.</summary>
    public static void Square(DebugLines lines, Vector3 p, Vector3 right, Vector3 up, float half, Vector4 color)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var r = right * half;
        var u = up * half;
        lines.AddLine(p - r - u, p + r - u, color);
        lines.AddLine(p + r - u, p + r + u, color);
        lines.AddLine(p + r + u, p - r + u, color);
        lines.AddLine(p - r + u, p - r - u, color);
    }

    internal static string Metres(float value, bool is2D) =>
        value.ToString("0.00", CultureInfo.InvariantCulture) + (is2D ? " px" : " m");

    internal static string Degrees(float value) => value.ToString("0.0", CultureInfo.InvariantCulture) + "°";
}

/// <summary><see cref="OmniLight3D.Range"/>: a handle on a view-facing circle at the range, dragged along the camera's right.</summary>
[HandleProvider(typeof(OmniLight3D))]
public sealed class OmniLight3DHandles : IHandleProvider
{
    private static readonly string[] RangeProperty = ["Range"];

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(handles);
        if (node is OmniLight3D omni)
            handles.Add(new EditorHandle(0, omni.GlobalPosition + camera.Right * omni.Range, camera.Right));
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => RangeProperty;

    public string Describe(Node node, int id) => node is OmniLight3D omni ? "Range " + HandleDrawing.Metres(omni.Range, false) : "";

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is OmniLight3D omni)
            omni.Range = HandleProviders.Clamp(node, "Range", drag.SnapDistance(MathF.Max(0f, drag.Along)));
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (node is OmniLight3D omni)
            HandleDrawing.Circle(lines, omni.GlobalPosition, camera.Right, camera.Up, omni.Range, BuiltInHandleColors.Light);
    }
}

/// <summary>
/// <see cref="SpotLight3D"/>: the range at the cone's tip (along its forward), the outer angle on the end circle's rim
/// (towards the camera's right) and the inner angle on a dimmer inner circle (towards the camera's up); angle =
/// atan(rim offset / range). The outer angle pushes the inner one down; the inner one stays ≤ the outer.
/// </summary>
[HandleProvider(typeof(SpotLight3D))]
public sealed class SpotLight3DHandles : IHandleProvider
{
    public const int RangeHandle = 0;
    public const int OuterHandle = 1;
    public const int InnerHandle = 2;

    private static readonly string[] RangeProperty = ["Range"];
    private static readonly string[] OuterProperties = ["OuterConeAngle", "InnerConeAngle"];
    private static readonly string[] InnerProperty = ["InnerConeAngle"];

    /// <summary>The spot's forward and the two rim directions (towards the camera's right and up, across the cone).</summary>
    public static (Vector3 Forward, Vector3 Rim, Vector3 RimUp) Frame(SpotLight3D spot, EditorCamera camera)
    {
        ArgumentNullException.ThrowIfNull(spot);
        ArgumentNullException.ThrowIfNull(camera);
        var forward = spot.GlobalForward;
        var rim = camera.Right - forward * Vector3.Dot(camera.Right, forward);
        rim = rim.LengthSquared() > 1e-6f ? Vector3.Normalize(rim) : TransformGizmo.Perpendiculars(forward).U;
        return (forward, rim, Vector3.Normalize(Vector3.Cross(rim, forward)));
    }

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (node is not SpotLight3D spot)
            return;
        var (forward, rim, rimUp) = Frame(spot, camera);
        var tip = spot.GlobalPosition + forward * spot.Range;
        handles.Add(new EditorHandle(RangeHandle, tip, forward));
        handles.Add(new EditorHandle(OuterHandle, tip + rim * RimRadius(spot.Range, spot.OuterConeAngle), rim));
        handles.Add(new EditorHandle(InnerHandle, tip + rimUp * RimRadius(spot.Range, spot.InnerConeAngle), rimUp));
    }

    private static float RimRadius(float range, float degrees) => MathF.Tan(float.DegreesToRadians(Math.Clamp(degrees, 0f, 89f))) * range;

    public ReadOnlySpan<string> Properties(Node node, int id) => id switch
    {
        OuterHandle => OuterProperties,
        InnerHandle => InnerProperty,
        _ => RangeProperty,
    };

    public string Describe(Node node, int id)
    {
        if (node is not SpotLight3D spot)
            return "";
        return id switch
        {
            OuterHandle => "Outer Angle " + HandleDrawing.Degrees(spot.OuterConeAngle),
            InnerHandle => "Inner Angle " + HandleDrawing.Degrees(spot.InnerConeAngle),
            _ => "Range " + HandleDrawing.Metres(spot.Range, false),
        };
    }

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not SpotLight3D spot)
            return;
        if (id == RangeHandle)
        {
            spot.Range = HandleProviders.Clamp(node, "Range", drag.SnapDistance(MathF.Max(0f, drag.Along)));
            return;
        }

        // The rim handle moves across the cone: its distance from the cone's axis over the range is the angle's tangent.
        var degrees = float.RadiansToDegrees(MathF.Atan2(MathF.Max(0f, drag.Along), MathF.Max(1e-4f, spot.Range)));
        degrees = drag.SnapAngle(degrees);
        if (id == OuterHandle)
        {
            spot.OuterConeAngle = HandleProviders.Clamp(node, "OuterConeAngle", degrees);
            if (spot.InnerConeAngle > spot.OuterConeAngle)
                spot.InnerConeAngle = spot.OuterConeAngle;
        }
        else
        {
            spot.InnerConeAngle = MathF.Min(HandleProviders.Clamp(node, "InnerConeAngle", degrees), spot.OuterConeAngle);
        }
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        if (node is not SpotLight3D spot)
            return;
        var (forward, _, _) = Frame(spot, camera);
        var (u, v) = TransformGizmo.Perpendiculars(forward);
        var tip = spot.GlobalPosition + forward * spot.Range;
        HandleDrawing.Circle(lines, tip, u, v, RimRadius(spot.Range, spot.InnerConeAngle), BuiltInHandleColors.Light with { W = 0.4f }, 32);
    }
}

/// <summary>
/// <see cref="AudioPlayer3D"/>: the unit size on an inner sphere and the max distance on the outer one, both along the
/// camera's right. With no max distance (0, unlimited) its handle is hollow at twice the unit size. Unit size stays ≤ the
/// max distance.
/// </summary>
[HandleProvider(typeof(AudioPlayer3D))]
public sealed class AudioPlayer3DHandles : IHandleProvider
{
    public const int UnitSizeHandle = 0;
    public const int MaxDistanceHandle = 1;

    private static readonly string[] UnitSizeProperty = ["UnitSize"];
    private static readonly string[] MaxDistanceProperty = ["MaxDistance"];

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(camera);
        ArgumentNullException.ThrowIfNull(handles);
        if (node is not AudioPlayer3D audio)
            return;
        var p = audio.GlobalPosition;
        handles.Add(new EditorHandle(UnitSizeHandle, p + camera.Right * audio.UnitSize, camera.Right));
        var unlimited = audio.MaxDistance <= 0f;
        handles.Add(new EditorHandle(MaxDistanceHandle, p + camera.Right * (unlimited ? audio.UnitSize * 2f : audio.MaxDistance), camera.Right,
            unlimited ? HandleStyle.Hollow : HandleStyle.Square));
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => id == MaxDistanceHandle ? MaxDistanceProperty : UnitSizeProperty;

    public string Describe(Node node, int id)
    {
        if (node is not AudioPlayer3D audio)
            return "";
        if (id == UnitSizeHandle)
            return "Unit Size " + HandleDrawing.Metres(audio.UnitSize, false);
        return audio.MaxDistance <= 0f ? "Max Distance: unlimited — drag to set" : "Max Distance " + HandleDrawing.Metres(audio.MaxDistance, false);
    }

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not AudioPlayer3D audio)
            return;
        var value = drag.SnapDistance(MathF.Max(0f, drag.Along));
        if (id == UnitSizeHandle)
        {
            value = HandleProviders.Clamp(node, "UnitSize", value);
            audio.UnitSize = audio.MaxDistance > 0f ? MathF.Min(value, audio.MaxDistance) : value;
        }
        else
        {
            audio.MaxDistance = HandleProviders.Clamp(node, "MaxDistance", MathF.Max(value, audio.UnitSize));
        }
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        ArgumentNullException.ThrowIfNull(camera);
        if (node is AudioPlayer3D audio)
            HandleDrawing.Circle(lines, audio.GlobalPosition, camera.Right, camera.Up, audio.UnitSize, BuiltInHandleColors.Audio with { W = 0.45f }, 32);
    }
}

/// <summary><see cref="AudioPlayer2D.MaxDistance"/>: a circle at the distance with a handle on its +X side.</summary>
[HandleProvider(typeof(AudioPlayer2D))]
public sealed class AudioPlayer2DHandles : IHandleProvider
{
    private static readonly string[] MaxDistanceProperty = ["MaxDistance"];

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (node is AudioPlayer2D audio)
            handles.Add(new EditorHandle(0, new Vector3(audio.GlobalPosition + new Vector2(audio.MaxDistance, 0), 0f), Vector3.UnitX));
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => MaxDistanceProperty;

    public string Describe(Node node, int id) => node is AudioPlayer2D audio ? "Max Distance " + HandleDrawing.Metres(audio.MaxDistance, true) : "";

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is AudioPlayer2D audio)
            audio.MaxDistance = HandleProviders.Clamp(node, "MaxDistance", drag.SnapDistance(drag.Along));
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        if (node is AudioPlayer2D audio)
            HandleDrawing.Circle(lines, new Vector3(audio.GlobalPosition, 0f), Vector3.UnitX, Vector3.UnitY, audio.MaxDistance, BuiltInHandleColors.Audio, 64);
    }
}

/// <summary>
/// <see cref="PointLight2D.TextureScale"/> (only with a <see cref="PointLight2D.Texture"/>): a handle on the right edge of
/// the texture's rectangle, dragged along the node's +X.
/// </summary>
[HandleProvider(typeof(PointLight2D))]
public sealed class PointLight2DHandles : IHandleProvider
{
    private static readonly string[] TextureScaleProperty = ["TextureScale"];

    // The texture's centre and the node's unit +X in the world, and the world length of half the texture's width at scale 1.
    private static bool TryFrame(PointLight2D light, out Vector2 center, out Vector2 axis, out float halfWidth)
    {
        center = axis = Vector2.Zero;
        halfWidth = 0f;
        if (light.Texture is not { Width: > 0 } texture)
            return false;
        var global = light.GlobalTransform;
        center = global.TransformPoint(light.Offset);
        var x = global.X;
        var length = x.Length();
        if (length < 1e-6f)
            return false;
        axis = x / length;
        halfWidth = texture.Width * 0.5f * length;
        return true;
    }

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (node is PointLight2D light && TryFrame(light, out var center, out var axis, out var half))
            handles.Add(new EditorHandle(0, new Vector3(center + axis * half * light.TextureScale, 0f), new Vector3(axis, 0f)));
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => TextureScaleProperty;

    public string Describe(Node node, int id) =>
        node is PointLight2D light ? "Texture Scale " + light.TextureScale.ToString("0.00", CultureInfo.InvariantCulture) : "";

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not PointLight2D light || !TryFrame(light, out var center, out var axis, out var half))
            return;
        // Along is measured from the node's origin; the texture's centre may sit at an offset along the same axis.
        var along = drag.Along - Vector2.Dot(center - new Vector2(drag.NodeOrigin.X, drag.NodeOrigin.Y), axis);
        var scale = along / half;
        if (drag.Snap)
            scale = TransformGizmo.SnapTo(scale, 0.1f);
        light.TextureScale = HandleProviders.Clamp(node, "TextureScale", scale);
    }
}

internal static class BuiltInHandleColors
{
    public static readonly Vector4 Light = new(1f, 0.9f, 0.35f, 0.7f);
    public static readonly Vector4 Audio = new(0.3f, 0.85f, 0.95f, 0.7f);
    public static readonly Vector4 Decal = new(0.95f, 0.55f, 0.9f, 0.8f);
}

/// <summary>
/// <see cref="Decal"/> (G6.5): the projection box and an arrow along its −Y projection, with a handle on the +X, +Y and +Z
/// faces that sets <see cref="Decal.Size"/> on that axis (the box stays centred, so the size is twice the handle's distance).
/// </summary>
[HandleProvider(typeof(Decal))]
public sealed class DecalHandles : IHandleProvider
{
    private static readonly string[] SizeProperty = ["Size"];

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (node is not Decal decal)
            return;
        var box = decal.BoxMatrix;
        var center = box.Translation;
        for (var axis = 0; axis < 3; axis++)
        {
            var edge = Row(box, axis) * 0.5f;
            if (edge.LengthSquared() > 0f)
                handles.Add(new EditorHandle(axis, center + edge, Vector3.Normalize(edge)));
        }
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => SizeProperty;

    public string Describe(Node node, int id) => node is Decal decal
        ? (id == 0 ? "Width " : id == 1 ? "Depth " : "Length ") + HandleDrawing.Metres(Component(decal.Size, id), false)
        : "";

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not Decal decal || id is < 0 or > 2)
            return;
        // The handle's distance from the centre, in the decal's local units (its node scale divided out).
        var scale = Row(decal.ModelMatrix, id).Length();
        var half = scale > 0f ? MathF.Max(0f, drag.Along) / scale : 0f;
        var size = decal.Size;
        var value = MathF.Max(0.001f, drag.SnapDistance(half * 2f));
        decal.Size = id switch { 0 => size with { X = value }, 1 => size with { Y = value }, _ => size with { Z = value } };
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (node is not Decal decal)
            return;
        var box = decal.BoxMatrix;
        Span<Vector3> corners = stackalloc Vector3[8];
        for (var i = 0; i < 8; i++)
            corners[i] = Vector3.Transform(new Vector3((i & 1) - 0.5f, ((i >> 1) & 1) - 0.5f, ((i >> 2) & 1) - 0.5f), box);
        for (var i = 0; i < 8; i++)
        {
            for (var bit = 1; bit < 8; bit <<= 1)
                if ((i & bit) == 0)
                    lines.AddLine(corners[i], corners[i | bit], BuiltInHandleColors.Decal);
        }

        // The projection: from the top face's centre down through the box, with an arrow head.
        var top = Vector3.Transform(new Vector3(0, 0.5f, 0), box);
        var bottom = Vector3.Transform(new Vector3(0, -0.5f, 0), box);
        var head = (bottom - top) * 0.2f;
        var side = Vector3.Transform(new Vector3(0.15f, 0, 0), box) - box.Translation;
        lines.AddLine(top, bottom, BuiltInHandleColors.Decal);
        lines.AddLine(bottom, bottom - head + side, BuiltInHandleColors.Decal);
        lines.AddLine(bottom, bottom - head - side, BuiltInHandleColors.Decal);
    }

    private static Vector3 Row(in Matrix4x4 m, int axis) => axis switch
    {
        0 => new Vector3(m.M11, m.M12, m.M13),
        1 => new Vector3(m.M21, m.M22, m.M23),
        _ => new Vector3(m.M31, m.M32, m.M33),
    };

    private static float Component(Vector3 v, int axis) => axis switch { 0 => v.X, 1 => v.Y, _ => v.Z };
}
