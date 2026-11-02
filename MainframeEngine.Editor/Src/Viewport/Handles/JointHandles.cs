using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// A joint's frame in the world, unscaled: its position and unit axes. In 2D, x, y in pixels with z = 0, X and Y the
/// node's rotated axes (Y down) and Z = +Z, so angles are measured as the 2D debug draw measures them.
/// </summary>
public readonly record struct JointFrame(Vector3 Origin, Vector3 X, Vector3 Y, Vector3 Z)
{
    /// <summary>The frame of a 3D pose (scale dropped).</summary>
    public static JointFrame Of(in Transform3D pose)
    {
        pose.Decompose(out var position, out var rotation, out _);
        rotation = Quaternion.Normalize(rotation);
        return new JointFrame(position, Vector3.Transform(Vector3.UnitX, rotation), Vector3.Transform(Vector3.UnitY, rotation),
            Vector3.Transform(Vector3.UnitZ, rotation));
    }

    /// <summary>The frame of a 2D pose (rotation only).</summary>
    public static JointFrame Of(in Transform2D pose)
    {
        var (sin, cos) = MathF.SinCos(pose.Rotation);
        return new JointFrame(new Vector3(pose.Origin, 0f), new Vector3(cos, sin, 0f), new Vector3(-sin, cos, 0f), Vector3.UnitZ);
    }

    /// <summary>The point at <paramref name="angle"/> radians from <paramref name="u"/> towards <paramref name="v"/>, <paramref name="radius"/> from the origin.</summary>
    public Vector3 OnCircle(Vector3 u, Vector3 v, float angle, float radius)
    {
        var (sin, cos) = MathF.SinCos(angle);
        return Origin + (u * cos + v * sin) * radius;
    }
}

/// <summary>
/// Joint limit handles (G9.7, ADR 0211): the math and drawing shared by the joint <see cref="IHandleProvider"/>s. Angle
/// handles sit on an arc and intersect the mouse ray with the arc's plane; the angle kept is the one nearest the previous
/// value, so a drag past ±180° stops there instead of jumping. Arcs are drawn at a view-relative radius (15 % of the
/// view's height, at least the debug draw's) so the handles stay apart at any zoom. A limit that is off has hollow
/// handles; dragging one turns the limit on in the same undo entry.
/// </summary>
public static class JointHandles
{
    /// <summary>The lower (or minimum) limit's handle.</summary>
    public const int Lower = 0;

    /// <summary>The upper (or maximum) limit's handle.</summary>
    public const int Upper = 1;

    /// <summary>A cone-twist's swing span.</summary>
    public const int Swing = 2;

    /// <summary>A cone-twist's twist span.</summary>
    public const int Twist = 3;

    /// <summary>A 2D groove's or spring's length.</summary>
    public const int Length = 4;

    internal static readonly Vector4 Color = new(0.55f, 0.8f, 1f, 0.9f);
    internal static readonly Vector4 OffColor = Color with { W = 0.35f };

    private const float FreeSpan = MathF.PI - 1e-3f;

    /// <summary>The handles' arc radius at <paramref name="origin"/>: 15 % of the view's height, at least <paramref name="minimum"/>.</summary>
    public static float Radius(EditorCamera camera, Vector3 origin, Vector2 viewPixels, float minimum)
    {
        ArgumentNullException.ThrowIfNull(camera);
        return MathF.Max(minimum, camera.WorldPerPixel(origin, viewPixels.Y) * viewPixels.Y * 0.15f);
    }

    /// <summary>
    /// The angle in radians, from <paramref name="u"/> towards <paramref name="v"/> about the origin, of where the mouse
    /// ray meets their plane (the dragged point when the ray runs along the plane). False when that point is the origin.
    /// </summary>
    public static bool TryAngle(in HandleDrag drag, Vector3 origin, Vector3 u, Vector3 v, out float radians)
    {
        var normal = Vector3.Cross(u, v);
        var point = drag.Point;
        var facing = Vector3.Dot(drag.RayDirection, normal);
        if (MathF.Abs(facing) > 0.02f)
        {
            var t = Vector3.Dot(origin - drag.RayOrigin, normal) / facing;
            if (t > 0f)
                point = drag.RayOrigin + drag.RayDirection * t;
        }

        var local = point - origin;
        var x = Vector3.Dot(local, u);
        var y = Vector3.Dot(local, v);
        radians = MathF.Atan2(y, x);
        return x * x + y * y > 1e-12f;
    }

    /// <summary><paramref name="radians"/> plus whole turns, to be the angle nearest <paramref name="previous"/>.</summary>
    public static float Unwrap(float radians, float previous)
    {
        var d = radians - previous;
        return previous + (d - MathF.Tau * MathF.Round(d / MathF.Tau));
    }

    /// <summary>A dragged angle in degrees: unwrapped near <paramref name="previousRadians"/>, snapped to the rotate step when Snap is on.</summary>
    public static float DragDegrees(in HandleDrag drag, float radians, float previousRadians) =>
        drag.SnapAngle(float.RadiansToDegrees(Unwrap(radians, previousRadians)));

    /// <summary>A linear limit's dragged value: the distance from the frame's origin along the drag axis, snapped.</summary>
    public static float DragDistance(in HandleDrag drag, Vector3 frameOrigin) =>
        drag.SnapDistance(drag.Along - Vector3.Dot(frameOrigin - drag.NodeOrigin, drag.Axis));

    // ── Angle ranges (hinges, 2D pins): an arc about Z from X towards Y ────────────────────────────────────────────

    public static void AddAngleHandles(List<EditorHandle> handles, in JointFrame frame, float radius, float lower, float upper, bool on)
    {
        ArgumentNullException.ThrowIfNull(handles);
        var style = on ? HandleStyle.Square : HandleStyle.Hollow;
        handles.Add(new EditorHandle(Lower, frame.OnCircle(frame.X, frame.Y, lower, radius), Tangent(frame.X, frame.Y, lower), style));
        handles.Add(new EditorHandle(Upper, frame.OnCircle(frame.X, frame.Y, upper, radius), Tangent(frame.X, frame.Y, upper), style));
    }

    public static void DrawAngleRange(DebugLines lines, in JointFrame frame, float radius, float lower, float upper, bool on)
    {
        ArgumentNullException.ThrowIfNull(lines);
        if (!on)
        {
            HandleDrawing.Circle(lines, frame.Origin, frame.X, frame.Y, radius, OffColor, 32);
            return;
        }

        Arc(lines, frame, frame.X, frame.Y, radius, MathF.Min(lower, upper), MathF.Max(lower, upper), Color);
        lines.AddLine(frame.Origin, frame.OnCircle(frame.X, frame.Y, lower, radius), Color);
        lines.AddLine(frame.Origin, frame.OnCircle(frame.X, frame.Y, upper, radius), Color);
    }

    // ── Linear ranges (sliders, wheels): along one axis from the origin ──────────────────────────────────────────

    public static void AddLinearHandles(List<EditorHandle> handles, Vector3 origin, Vector3 axis, float lower, float upper, bool on)
    {
        ArgumentNullException.ThrowIfNull(handles);
        var style = on ? HandleStyle.Square : HandleStyle.Hollow;
        handles.Add(new EditorHandle(Lower, origin + axis * lower, axis, style));
        handles.Add(new EditorHandle(Upper, origin + axis * upper, axis, style));
    }

    public static void DrawLinearRange(DebugLines lines, Vector3 origin, Vector3 axis, float lower, float upper, bool on)
    {
        ArgumentNullException.ThrowIfNull(lines);
        lines.AddLine(origin + axis * lower, origin + axis * upper, on ? Color : OffColor);
    }

    // ── Cone-twist: a swing cone about X and a twist arc from Y towards Z ────────────────────────────────────────

    /// <summary>The direction across the cone its swing handle sits on: the camera's right, made perpendicular to the cone's axis.</summary>
    public static Vector3 SwingRim(in JointFrame frame, EditorCamera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        var rim = camera.Right - frame.X * Vector3.Dot(camera.Right, frame.X);
        return rim.LengthSquared() > 1e-6f ? Vector3.Normalize(rim) : frame.Y;
    }

    /// <summary>A span of 180° is no limit (the joint switches it off).</summary>
    public static bool IsFree(float spanRadians) => spanRadians >= FreeSpan;

    public static void AddConeHandles(List<EditorHandle> handles, in JointFrame frame, Vector3 rim, float radius, float swing, float twist)
    {
        ArgumentNullException.ThrowIfNull(handles);
        handles.Add(new EditorHandle(Swing, frame.OnCircle(frame.X, rim, swing, radius), Tangent(frame.X, rim, swing),
            IsFree(swing) ? HandleStyle.Hollow : HandleStyle.Square));
        handles.Add(new EditorHandle(Twist, frame.OnCircle(frame.Y, frame.Z, twist, radius * 0.6f), Tangent(frame.Y, frame.Z, twist),
            IsFree(twist) ? HandleStyle.Hollow : HandleStyle.Square));
    }

    public static void DrawCone(DebugLines lines, in JointFrame frame, Vector3 rim, float radius, float swing, float twist)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var color = IsFree(swing) ? OffColor : Color;
        var (sin, cos) = MathF.SinCos(swing);
        HandleDrawing.Circle(lines, frame.Origin + frame.X * (cos * radius), frame.Y, frame.Z, sin * radius, color, 32);
        lines.AddLine(frame.Origin, frame.OnCircle(frame.X, rim, swing, radius), color);
        lines.AddLine(frame.Origin, frame.OnCircle(frame.X, rim, -swing, radius), color);
        if (IsFree(twist))
            HandleDrawing.Circle(lines, frame.Origin, frame.Y, frame.Z, radius * 0.6f, OffColor, 32);
        else
            Arc(lines, frame, frame.Y, frame.Z, radius * 0.6f, -twist, twist, Color);
    }

    /// <summary>A dragged span (swing or twist) in degrees, 0…180, from the angle of the mouse in the span's plane.</summary>
    public static float SpanDegrees(in HandleDrag drag, float radians) => drag.SnapAngle(float.RadiansToDegrees(MathF.Abs(radians)));

    // ── Shared ───────────────────────────────────────────────────────────────────────────────────────────────────

    // The direction an angle handle moves in: the circle's tangent at its angle.
    private static Vector3 Tangent(Vector3 u, Vector3 v, float angle)
    {
        var (sin, cos) = MathF.SinCos(angle);
        return v * cos - u * sin;
    }

    private static void Arc(DebugLines lines, in JointFrame frame, Vector3 u, Vector3 v, float radius, float from, float to, Vector4 color)
    {
        var segments = Math.Clamp((int)MathF.Ceiling(MathF.Abs(to - from) / (MathF.Tau / 48)), 1, 48);
        var previous = frame.OnCircle(u, v, from, radius);
        for (var i = 1; i <= segments; i++)
        {
            var next = frame.OnCircle(u, v, from + (to - from) * i / segments, radius);
            lines.AddLine(previous, next, color);
            previous = next;
        }
    }

    internal static string Off(string name, string state) => name + ": " + state + " — drag to set";
}

/// <summary><see cref="HingeJoint3D"/>: the lower and upper angle on an arc about the joint's local Z (hollow while <see cref="HingeJoint3D.UseLimit"/> is off).</summary>
[HandleProvider(typeof(HingeJoint3D))]
public sealed class HingeJoint3DHandles : IHandleProvider
{
    private static readonly string[] LowerProperties = ["LowerAngleDegrees", "UseLimit"];
    private static readonly string[] UpperProperties = ["UpperAngleDegrees", "UseLimit"];

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        if (node is not HingeJoint3D hinge)
            return;
        var frame = JointFrame.Of(hinge.GlobalTransform);
        JointHandles.AddAngleHandles(handles, frame, JointHandles.Radius(camera, frame.Origin, viewPixels, 0.2f), hinge.LowerAngle, hinge.UpperAngle,
            hinge.UseLimit);
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => id == JointHandles.Lower ? LowerProperties : UpperProperties;

    public string Describe(Node node, int id)
    {
        if (node is not HingeJoint3D hinge)
            return "";
        var name = id == JointHandles.Lower ? "Lower Angle" : "Upper Angle";
        if (!hinge.UseLimit)
            return JointHandles.Off(name, "no limit");
        return name + " " + HandleDrawing.Degrees(id == JointHandles.Lower ? hinge.LowerAngleDegrees : hinge.UpperAngleDegrees);
    }

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not HingeJoint3D hinge)
            return;
        var frame = JointFrame.Of(hinge.GlobalTransform);
        if (!JointHandles.TryAngle(drag, frame.Origin, frame.X, frame.Y, out var radians))
            return;
        if (id == JointHandles.Lower)
        {
            var degrees = HandleProviders.Clamp(node, "LowerAngleDegrees", JointHandles.DragDegrees(drag, radians, hinge.LowerAngle));
            hinge.LowerAngleDegrees = MathF.Min(degrees, hinge.UpperAngleDegrees);
        }
        else
        {
            var degrees = HandleProviders.Clamp(node, "UpperAngleDegrees", JointHandles.DragDegrees(drag, radians, hinge.UpperAngle));
            hinge.UpperAngleDegrees = MathF.Max(degrees, hinge.LowerAngleDegrees);
        }

        hinge.UseLimit = true;
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        if (node is not HingeJoint3D hinge)
            return;
        var frame = JointFrame.Of(hinge.GlobalTransform);
        JointHandles.DrawAngleRange(lines, frame, JointHandles.Radius(camera, frame.Origin, viewPixels, 0.2f), hinge.LowerAngle, hinge.UpperAngle,
            hinge.UseLimit);
    }
}

/// <summary><see cref="SliderJoint3D"/>: the lower and upper distance on the joint's local X (hollow while <see cref="SliderJoint3D.UseLimit"/> is off).</summary>
[HandleProvider(typeof(SliderJoint3D))]
public sealed class SliderJoint3DHandles : IHandleProvider
{
    private static readonly string[] LowerProperties = ["LowerDistance", "UseLimit"];
    private static readonly string[] UpperProperties = ["UpperDistance", "UseLimit"];

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        if (node is not SliderJoint3D slider)
            return;
        var frame = JointFrame.Of(slider.GlobalTransform);
        JointHandles.AddLinearHandles(handles, frame.Origin, frame.X, slider.LowerDistance, slider.UpperDistance, slider.UseLimit);
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => id == JointHandles.Lower ? LowerProperties : UpperProperties;

    public string Describe(Node node, int id)
    {
        if (node is not SliderJoint3D slider)
            return "";
        var name = id == JointHandles.Lower ? "Lower Distance" : "Upper Distance";
        if (!slider.UseLimit)
            return JointHandles.Off(name, "no limit");
        return name + " " + HandleDrawing.Metres(id == JointHandles.Lower ? slider.LowerDistance : slider.UpperDistance, false);
    }

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not SliderJoint3D slider)
            return;
        var value = JointHandles.DragDistance(drag, JointFrame.Of(slider.GlobalTransform).Origin);
        if (id == JointHandles.Lower)
            slider.LowerDistance = MathF.Min(HandleProviders.Clamp(node, "LowerDistance", value), slider.UpperDistance);
        else
            slider.UpperDistance = MathF.Max(HandleProviders.Clamp(node, "UpperDistance", value), slider.LowerDistance);
        slider.UseLimit = true;
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        if (node is not SliderJoint3D slider)
            return;
        var frame = JointFrame.Of(slider.GlobalTransform);
        JointHandles.DrawLinearRange(lines, frame.Origin, frame.X, slider.LowerDistance, slider.UpperDistance, slider.UseLimit);
    }
}

/// <summary>
/// <see cref="ConeTwistJoint3D"/>: the swing span on the cone's rim (towards the camera's right) and the twist span on
/// the twist arc (from local Y towards Z). A span of 180° (free) has a hollow handle.
/// </summary>
[HandleProvider(typeof(ConeTwistJoint3D))]
public sealed class ConeTwistJoint3DHandles : IHandleProvider
{
    private static readonly string[] SwingProperty = ["SwingSpanDegrees"];
    private static readonly string[] TwistProperty = ["TwistSpanDegrees"];

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        if (node is not ConeTwistJoint3D cone)
            return;
        var frame = JointFrame.Of(cone.GlobalTransform);
        JointHandles.AddConeHandles(handles, frame, JointHandles.SwingRim(frame, camera), JointHandles.Radius(camera, frame.Origin, viewPixels, 0.3f),
            cone.SwingSpan, cone.TwistSpan);
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => id == JointHandles.Twist ? TwistProperty : SwingProperty;

    public string Describe(Node node, int id) => node is ConeTwistJoint3D cone ? ConeLimits.Describe(id, cone.SwingSpan, cone.TwistSpan) : "";

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not ConeTwistJoint3D cone || !ConeLimits.TryDrag(JointFrame.Of(cone.GlobalTransform), id, drag, out var degrees))
            return;
        if (id == JointHandles.Twist)
            cone.TwistSpanDegrees = HandleProviders.Clamp(node, "TwistSpanDegrees", degrees);
        else
            cone.SwingSpanDegrees = HandleProviders.Clamp(node, "SwingSpanDegrees", degrees);
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        if (node is not ConeTwistJoint3D cone)
            return;
        var frame = JointFrame.Of(cone.GlobalTransform);
        JointHandles.DrawCone(lines, frame, JointHandles.SwingRim(frame, camera), JointHandles.Radius(camera, frame.Origin, viewPixels, 0.3f),
            cone.SwingSpan, cone.TwistSpan);
    }
}

// The cone-twist label and drag, shared by ConeTwistJoint3D and PhysicalBone3D.
internal static class ConeLimits
{
    public static string Describe(int id, float swing, float twist)
    {
        var name = id == JointHandles.Twist ? "Twist Span" : "Swing Span";
        var span = id == JointHandles.Twist ? twist : swing;
        return JointHandles.IsFree(span) ? JointHandles.Off(name, "free") : name + " " + HandleDrawing.Degrees(float.RadiansToDegrees(span));
    }

    public static bool TryDrag(in JointFrame frame, int id, in HandleDrag drag, out float degrees)
    {
        degrees = 0f;
        float radians;
        if (id == JointHandles.Twist)
        {
            if (!JointHandles.TryAngle(drag, frame.Origin, frame.Y, frame.Z, out radians))
                return false;
        }
        else if (!JointHandles.TryAngle(drag, frame.Origin, frame.X, JointHandles.SwingRim(frame, drag.Camera), out radians))
        {
            return false;
        }

        degrees = JointHandles.SpanDegrees(drag, radians);
        return true;
    }
}

/// <summary>
/// <see cref="DistanceJoint3D"/>: the min and max length on the line from the joint to <see cref="DistanceJoint3D.AnchorB"/>.
/// A max of 0 (the authored length for a rope, none for a spring) is hollow at the authored length; a min of 0 (slack) is
/// hollow at a quarter of the max. Min stays ≤ max.
/// </summary>
[HandleProvider(typeof(DistanceJoint3D))]
public sealed class DistanceJoint3DHandles : IHandleProvider
{
    private static readonly string[] MinProperty = ["MinLength"];
    private static readonly string[] MaxProperty = ["MaxLength"];

    // The line from the joint to its anchor on B (−Y when they coincide) and the authored distance.
    private static (Vector3 Origin, Vector3 Direction, float Authored) Line(DistanceJoint3D joint)
    {
        var pose = joint.GlobalTransform;
        var offset = pose.TransformPoint(joint.AnchorB) - pose.Origin;
        var length = offset.Length();
        return (pose.Origin, length > 1e-5f ? offset / length : -JointFrame.Of(pose).Y, length);
    }

    private static float ShownMax(DistanceJoint3D joint, float authored) => joint.MaxLength > 0f ? joint.MaxLength : authored;

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (node is not DistanceJoint3D joint)
            return;
        var (origin, direction, authored) = Line(joint);
        var max = ShownMax(joint, authored);
        var min = joint.MinLength > 0f ? joint.MinLength : max * 0.25f;
        handles.Add(new EditorHandle(JointHandles.Lower, origin + direction * min, direction,
            joint.MinLength > 0f ? HandleStyle.Square : HandleStyle.Hollow));
        handles.Add(new EditorHandle(JointHandles.Upper, origin + direction * max, direction,
            joint.MaxLength > 0f ? HandleStyle.Square : HandleStyle.Hollow));
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => id == JointHandles.Lower ? MinProperty : MaxProperty;

    public string Describe(Node node, int id)
    {
        if (node is not DistanceJoint3D joint)
            return "";
        if (id == JointHandles.Lower)
            return joint.MinLength > 0f ? "Min Length " + HandleDrawing.Metres(joint.MinLength, false) : JointHandles.Off("Min Length", "slack");
        if (joint.MaxLength > 0f)
            return "Max Length " + HandleDrawing.Metres(joint.MaxLength, false);
        return JointHandles.Off("Max Length", joint.SpringFrequency > 0f ? "none" : "the authored " + HandleDrawing.Metres(Line(joint).Authored, false));
    }

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not DistanceJoint3D joint)
            return;
        var value = HandleProviders.Clamp(node, id == JointHandles.Lower ? "MinLength" : "MaxLength", drag.SnapDistance(drag.Along));
        if (id == JointHandles.Lower)
            joint.MinLength = joint.MaxLength > 0f ? MathF.Min(value, joint.MaxLength) : value;
        else
            joint.MaxLength = MathF.Max(value, MathF.Max(joint.MinLength, 0.01f)); // 0 would mean "unset" again
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        if (node is not DistanceJoint3D joint)
            return;
        var (origin, direction, authored) = Line(joint);
        JointHandles.DrawLinearRange(lines, origin, direction, joint.MinLength, ShownMax(joint, authored), joint.MaxLength > 0f || joint.MinLength > 0f);
    }
}

/// <summary>
/// <see cref="PhysicalBone3D"/>: the limits of its <see cref="PhysicalBone3D.JointType"/> (cone-twist spans, hinge angles
/// or slider distances) at the joint's frame, the body's pose × <see cref="PhysicalBone3D.JointOffset"/>. Pins and None
/// have none.
/// </summary>
[HandleProvider(typeof(PhysicalBone3D))]
public sealed class PhysicalBone3DHandles : IHandleProvider
{
    private static readonly string[] SwingProperty = ["SwingSpanDegrees"];
    private static readonly string[] TwistProperty = ["TwistSpanDegrees"];
    private static readonly string[] LowerAngleProperty = ["LowerAngleDegrees"];
    private static readonly string[] UpperAngleProperty = ["UpperAngleDegrees"];
    private static readonly string[] LowerDistanceProperty = ["LowerDistance"];
    private static readonly string[] UpperDistanceProperty = ["UpperDistance"];

    private static JointFrame Frame(PhysicalBone3D bone) => JointFrame.Of(bone.GlobalTransform * bone.JointOffset);

    private static float Radians(float degrees) => float.DegreesToRadians(degrees);

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        if (node is not PhysicalBone3D bone)
            return;
        var frame = Frame(bone);
        switch (bone.JointType)
        {
            case PhysicalBoneJointType.ConeTwist:
                JointHandles.AddConeHandles(handles, frame, JointHandles.SwingRim(frame, camera), JointHandles.Radius(camera, frame.Origin, viewPixels, 0.3f),
                    Radians(bone.SwingSpanDegrees), Radians(bone.TwistSpanDegrees));
                break;
            case PhysicalBoneJointType.Hinge:
                JointHandles.AddAngleHandles(handles, frame, JointHandles.Radius(camera, frame.Origin, viewPixels, 0.2f), Radians(bone.LowerAngleDegrees),
                    Radians(bone.UpperAngleDegrees), true);
                break;
            case PhysicalBoneJointType.Slider:
                JointHandles.AddLinearHandles(handles, frame.Origin, frame.X, bone.LowerDistance, bone.UpperDistance, true);
                break;
        }
    }

    public ReadOnlySpan<string> Properties(Node node, int id)
    {
        var slider = node is PhysicalBone3D { JointType: PhysicalBoneJointType.Slider };
        return id switch
        {
            JointHandles.Swing => SwingProperty,
            JointHandles.Twist => TwistProperty,
            JointHandles.Lower => slider ? LowerDistanceProperty : LowerAngleProperty,
            _ => slider ? UpperDistanceProperty : UpperAngleProperty,
        };
    }

    public string Describe(Node node, int id)
    {
        if (node is not PhysicalBone3D bone)
            return "";
        return bone.JointType switch
        {
            PhysicalBoneJointType.ConeTwist => ConeLimits.Describe(id, Radians(bone.SwingSpanDegrees), Radians(bone.TwistSpanDegrees)),
            PhysicalBoneJointType.Slider => id == JointHandles.Lower
                ? "Lower Distance " + HandleDrawing.Metres(bone.LowerDistance, false)
                : "Upper Distance " + HandleDrawing.Metres(bone.UpperDistance, false),
            _ => id == JointHandles.Lower
                ? "Lower Angle " + HandleDrawing.Degrees(bone.LowerAngleDegrees)
                : "Upper Angle " + HandleDrawing.Degrees(bone.UpperAngleDegrees),
        };
    }

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not PhysicalBone3D bone)
            return;
        var frame = Frame(bone);
        switch (bone.JointType)
        {
            case PhysicalBoneJointType.ConeTwist when ConeLimits.TryDrag(frame, id, drag, out var span):
                if (id == JointHandles.Twist)
                    bone.TwistSpanDegrees = HandleProviders.Clamp(node, "TwistSpanDegrees", span);
                else
                    bone.SwingSpanDegrees = HandleProviders.Clamp(node, "SwingSpanDegrees", span);
                break;
            case PhysicalBoneJointType.Hinge when JointHandles.TryAngle(drag, frame.Origin, frame.X, frame.Y, out var radians):
                if (id == JointHandles.Lower)
                {
                    var degrees = HandleProviders.Clamp(node, "LowerAngleDegrees", JointHandles.DragDegrees(drag, radians, Radians(bone.LowerAngleDegrees)));
                    bone.LowerAngleDegrees = MathF.Min(degrees, bone.UpperAngleDegrees);
                }
                else
                {
                    var degrees = HandleProviders.Clamp(node, "UpperAngleDegrees", JointHandles.DragDegrees(drag, radians, Radians(bone.UpperAngleDegrees)));
                    bone.UpperAngleDegrees = MathF.Max(degrees, bone.LowerAngleDegrees);
                }

                break;
            case PhysicalBoneJointType.Slider:
                var value = JointHandles.DragDistance(drag, frame.Origin);
                if (id == JointHandles.Lower)
                    bone.LowerDistance = MathF.Min(HandleProviders.Clamp(node, "LowerDistance", value), bone.UpperDistance);
                else
                    bone.UpperDistance = MathF.Max(HandleProviders.Clamp(node, "UpperDistance", value), bone.LowerDistance);
                break;
        }
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        if (node is not PhysicalBone3D bone)
            return;
        var frame = Frame(bone);
        switch (bone.JointType)
        {
            case PhysicalBoneJointType.ConeTwist:
                JointHandles.DrawCone(lines, frame, JointHandles.SwingRim(frame, camera), JointHandles.Radius(camera, frame.Origin, viewPixels, 0.3f),
                    Radians(bone.SwingSpanDegrees), Radians(bone.TwistSpanDegrees));
                break;
            case PhysicalBoneJointType.Hinge:
                JointHandles.DrawAngleRange(lines, frame, JointHandles.Radius(camera, frame.Origin, viewPixels, 0.2f), Radians(bone.LowerAngleDegrees),
                    Radians(bone.UpperAngleDegrees), true);
                break;
            case PhysicalBoneJointType.Slider:
                JointHandles.DrawLinearRange(lines, frame.Origin, frame.X, bone.LowerDistance, bone.UpperDistance, true);
                break;
        }
    }
}

/// <summary>
/// <see cref="PinJoint2D"/>: the lower and upper angle (radians, labelled in degrees) on an arc about the pin (hollow
/// while <see cref="PinJoint2D.AngularLimitEnabled"/> is off).
/// </summary>
[HandleProvider(typeof(PinJoint2D))]
public sealed class PinJoint2DHandles : IHandleProvider
{
    private static readonly string[] LowerProperties = ["AngularLimitLower", "AngularLimitEnabled"];
    private static readonly string[] UpperProperties = ["AngularLimitUpper", "AngularLimitEnabled"];

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        if (node is not PinJoint2D pin)
            return;
        var frame = JointFrame.Of(pin.GlobalTransform);
        JointHandles.AddAngleHandles(handles, frame, JointHandles.Radius(camera, frame.Origin, viewPixels, 10f), pin.AngularLimitLower,
            pin.AngularLimitUpper, pin.AngularLimitEnabled);
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => id == JointHandles.Lower ? LowerProperties : UpperProperties;

    public string Describe(Node node, int id)
    {
        if (node is not PinJoint2D pin)
            return "";
        var name = id == JointHandles.Lower ? "Lower Angle" : "Upper Angle";
        if (!pin.AngularLimitEnabled)
            return JointHandles.Off(name, "no limit");
        return name + " " + HandleDrawing.Degrees(float.RadiansToDegrees(id == JointHandles.Lower ? pin.AngularLimitLower : pin.AngularLimitUpper));
    }

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not PinJoint2D pin)
            return;
        var frame = JointFrame.Of(pin.GlobalTransform);
        if (!JointHandles.TryAngle(drag, frame.Origin, frame.X, frame.Y, out var radians))
            return;
        var previous = id == JointHandles.Lower ? pin.AngularLimitLower : pin.AngularLimitUpper;
        var value = Math.Clamp(float.DegreesToRadians(JointHandles.DragDegrees(drag, radians, previous)), -MathF.PI, MathF.PI);
        if (id == JointHandles.Lower)
            pin.AngularLimitLower = MathF.Min(value, pin.AngularLimitUpper);
        else
            pin.AngularLimitUpper = MathF.Max(value, pin.AngularLimitLower);
        pin.AngularLimitEnabled = true;
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        if (node is not PinJoint2D pin)
            return;
        var frame = JointFrame.Of(pin.GlobalTransform);
        JointHandles.DrawAngleRange(lines, frame, JointHandles.Radius(camera, frame.Origin, viewPixels, 10f), pin.AngularLimitLower,
            pin.AngularLimitUpper, pin.AngularLimitEnabled);
    }
}

/// <summary>
/// A 2D joint's lower and upper translation on its local X, in pixels (hollow while its limit is off): the shared half
/// of <see cref="SliderJoint2DHandles"/> and <see cref="WheelJoint2DHandles"/>.
/// </summary>
public abstract class TranslationLimit2DHandles : IHandleProvider
{
    private static readonly string[] LowerProperties = ["LowerTranslation", "UseLimit"];
    private static readonly string[] UpperProperties = ["UpperTranslation", "UseLimit"];

    /// <summary>The node's limit, or false for another type.</summary>
    protected abstract bool TryGet(Node node, out bool useLimit, out float lower, out float upper);

    /// <summary>Writes the limit through the node's setters (only changed values take effect).</summary>
    protected abstract void Set(Node node, float lower, float upper);

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        if (node is Node2D node2D && TryGet(node, out var on, out var lower, out var upper))
        {
            var frame = JointFrame.Of(node2D.GlobalTransform);
            JointHandles.AddLinearHandles(handles, frame.Origin, frame.X, lower, upper, on);
        }
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => id == JointHandles.Lower ? LowerProperties : UpperProperties;

    public string Describe(Node node, int id)
    {
        if (!TryGet(node, out var on, out var lower, out var upper))
            return "";
        var name = id == JointHandles.Lower ? "Lower Translation" : "Upper Translation";
        return on ? name + " " + HandleDrawing.Metres(id == JointHandles.Lower ? lower : upper, true) : JointHandles.Off(name, "no limit");
    }

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is not Node2D node2D || !TryGet(node, out _, out var lower, out var upper))
            return;
        var value = JointHandles.DragDistance(drag, JointFrame.Of(node2D.GlobalTransform).Origin);
        if (id == JointHandles.Lower)
            Set(node, MathF.Min(HandleProviders.Clamp(node, "LowerTranslation", value), upper), upper);
        else
            Set(node, lower, MathF.Max(HandleProviders.Clamp(node, "UpperTranslation", value), lower));
    }

    public void Draw(Node node, EditorCamera camera, Vector2 viewPixels, DebugLines lines)
    {
        if (node is Node2D node2D && TryGet(node, out var on, out var lower, out var upper))
        {
            var frame = JointFrame.Of(node2D.GlobalTransform);
            JointHandles.DrawLinearRange(lines, frame.Origin, frame.X, lower, upper, on);
        }
    }
}

/// <summary><see cref="SliderJoint2D"/>'s translation limit.</summary>
[HandleProvider(typeof(SliderJoint2D))]
public sealed class SliderJoint2DHandles : TranslationLimit2DHandles
{
    protected override bool TryGet(Node node, out bool useLimit, out float lower, out float upper)
    {
        var slider = node as SliderJoint2D;
        useLimit = slider?.UseLimit ?? false;
        lower = slider?.LowerTranslation ?? 0f;
        upper = slider?.UpperTranslation ?? 0f;
        return slider is not null;
    }

    protected override void Set(Node node, float lower, float upper)
    {
        var slider = (SliderJoint2D)node;
        slider.LowerTranslation = lower;
        slider.UpperTranslation = upper;
        slider.UseLimit = true;
    }
}

/// <summary><see cref="WheelJoint2D"/>'s translation limit.</summary>
[HandleProvider(typeof(WheelJoint2D))]
public sealed class WheelJoint2DHandles : TranslationLimit2DHandles
{
    protected override bool TryGet(Node node, out bool useLimit, out float lower, out float upper)
    {
        var wheel = node as WheelJoint2D;
        useLimit = wheel?.UseLimit ?? false;
        lower = wheel?.LowerTranslation ?? 0f;
        upper = wheel?.UpperTranslation ?? 0f;
        return wheel is not null;
    }

    protected override void Set(Node node, float lower, float upper)
    {
        var wheel = (WheelJoint2D)node;
        wheel.LowerTranslation = lower;
        wheel.UpperTranslation = upper;
        wheel.UseLimit = true;
    }
}

/// <summary>
/// A 2D joint's <c>Length</c> on its local +Y (down), in pixels: the shared half of <see cref="GrooveJoint2DHandles"/> and
/// <see cref="DampedSpringJoint2DHandles"/>. Changing the length re-creates the joint.
/// </summary>
public abstract class Length2DHandles : IHandleProvider
{
    private static readonly string[] LengthProperty = ["Length"];

    /// <summary>The node's length, or false for another type.</summary>
    protected abstract bool TryGet(Node node, out float length);

    /// <summary>Writes the length.</summary>
    protected abstract void Set(Node node, float length);

    public void GetHandles(Node node, EditorCamera camera, Vector2 viewPixels, List<EditorHandle> handles)
    {
        ArgumentNullException.ThrowIfNull(handles);
        if (node is Node2D node2D && TryGet(node, out var length))
        {
            var frame = JointFrame.Of(node2D.GlobalTransform);
            handles.Add(new EditorHandle(JointHandles.Length, frame.Origin + frame.Y * length, frame.Y));
        }
    }

    public ReadOnlySpan<string> Properties(Node node, int id) => LengthProperty;

    public string Describe(Node node, int id) => TryGet(node, out var length) ? "Length " + HandleDrawing.Metres(length, true) : "";

    public void Drag(Node node, int id, in HandleDrag drag)
    {
        if (node is Node2D node2D && TryGet(node, out _))
            Set(node, HandleProviders.Clamp(node, "Length", JointHandles.DragDistance(drag, JointFrame.Of(node2D.GlobalTransform).Origin)));
    }
}

/// <summary><see cref="GrooveJoint2D.Length"/>: the groove's end.</summary>
[HandleProvider(typeof(GrooveJoint2D))]
public sealed class GrooveJoint2DHandles : Length2DHandles
{
    protected override bool TryGet(Node node, out float length)
    {
        length = (node as GrooveJoint2D)?.Length ?? 0f;
        return node is GrooveJoint2D;
    }

    protected override void Set(Node node, float length) => ((GrooveJoint2D)node).Length = length;
}

/// <summary><see cref="DampedSpringJoint2D.Length"/>: the spring's longest stretch, where body B is anchored.</summary>
[HandleProvider(typeof(DampedSpringJoint2D))]
public sealed class DampedSpringJoint2DHandles : Length2DHandles
{
    protected override bool TryGet(Node node, out float length)
    {
        length = (node as DampedSpringJoint2D)?.Length ?? 0f;
        return node is DampedSpringJoint2D;
    }

    protected override void Set(Node node, float length) => ((DampedSpringJoint2D)node).Length = length;
}
