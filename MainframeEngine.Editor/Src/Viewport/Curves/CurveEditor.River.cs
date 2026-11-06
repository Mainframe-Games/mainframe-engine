using System.Globalization;
using System.Numerics;

namespace MainframeEngine.Editor;

// G8c.4 (docs/design/water.md#editor): a River3D's sections in the curve tool. Every point shows its cross-section (bank
// to bank, and a tick down to the bed); the selected point gets width handles at both banks (dragged along the
// horizontal perpendicular to the curve, the width staying symmetric) and a depth handle below it (dragged up and
// down). A drag is one SetCurvePointsAction like any curve gesture; Esc puts the section back.
public sealed partial class CurveEditor
{
    private static readonly Vector4 SectionColor = new(0.35f, 0.75f, 1f, 0.55f);
    private static readonly Vector4 SectionHandleColor = new(0.35f, 0.75f, 1f, 1f);

    private Vector3 _sectionCenter, _sectionAxis;

    // The selected point's section frame: its world position and the horizontal across-the-river unit vector.
    private static bool SectionFrame(ICurveTarget target, int index, out Vector3 center, out Vector3 side)
    {
        center = side = default;
        if ((uint)index >= (uint)target.PointCount)
            return false;
        center = target.ToGlobal(target.GetPosition(index));
        target.FrameAt(index, out var tangent, out _);
        var across = Vector3.Cross(Vector3.UnitY, tangent);
        side = across.LengthSquared() > 1e-8f ? Vector3.Normalize(across) : Vector3.UnitX;
        return true;
    }

    // Where a section's handles are drawn: the two banks and the bed (at least 0.25 m down, so a flat stream's handle
    // does not hide under its point).
    private static (Vector3 Left, Vector3 Right, Vector3 Bed) SectionHandles(ICurveTarget target, int index, Vector3 center, Vector3 side)
    {
        var half = target.GetWidth(index) * 0.5f;
        return (center - side * half, center + side * half, center - Vector3.UnitY * MathF.Max(target.GetDepth(index), 0.25f));
    }

    // The selected point's width or depth handle under the pointer (null: neither).
    private CurvePart? SectionHit(ICurveTarget target, in View3D view, Vector2 pixel, float radius)
    {
        if (!SectionFrame(target, SelectedPoint, out var center, out var side))
            return null;
        var (left, right, bed) = SectionHandles(target, SelectedPoint, center, side);
        var size = view.ViewPixels;
        _sectionCenter = center;
        if (Near(view.Camera, bed, pixel, radius, size))
        {
            _sectionAxis = -Vector3.UnitY;
            return CurvePart.Depth;
        }

        if (Near(view.Camera, left, pixel, radius, size) || Near(view.Camera, right, pixel, radius, size))
        {
            _sectionAxis = side;
            return CurvePart.Width;
        }

        return null;

        static bool Near(EditorCamera camera, Vector3 p, Vector2 pixel, float radius, Vector2 size) =>
            camera.Project(p, size.X, size.Y, out var at) && Vector2.Distance(at, pixel) <= radius;
    }

    // A width or depth drag: the point on the handle's axis closest to the mouse ray sets the value.
    private void DragSection(ICurveTarget target, Vector3 origin, Vector3 direction)
    {
        var u = _sectionAxis;
        var w0 = _sectionCenter - origin;
        var b = Vector3.Dot(u, direction);
        var c = Vector3.Dot(direction, direction);
        var d = Vector3.Dot(u, w0);
        var e = Vector3.Dot(direction, w0);
        var denominator = c - b * b; // |u| = 1
        if (MathF.Abs(denominator) < 1e-6f)
            return; // looking straight along the axis
        var along = (b * e - c * d) / denominator;
        if (_dragPart == CurvePart.Width)
            target.SetWidth(_dragIndex, MathF.Max(0.1f, SnapScalar(2f * MathF.Abs(along))));
        else
            target.SetDepth(_dragIndex, MathF.Max(0f, SnapScalar(along)));
        UpdateSectionLabel(target, _dragPart);
    }

    private float SnapScalar(float value)
    {
        var (enabled, step) = _workspace is { } workspace ? (workspace.Gizmo.Snap.Enabled, workspace.Gizmo.Snap.Translate) : (SnapEnabled, SnapStep);
        return enabled && step > 0 ? MathF.Round(value / step) * step : value;
    }

    private void UpdateSectionLabel(ICurveTarget target, CurvePart part)
    {
        if ((uint)SelectedPoint >= (uint)target.PointCount)
        {
            _label = null;
            return;
        }

        _label = part == CurvePart.Width
            ? "Width " + target.GetWidth(SelectedPoint).ToString("0.00", CultureInfo.InvariantCulture) + " m"
            : "Depth " + target.GetDepth(SelectedPoint).ToString("0.00", CultureInfo.InvariantCulture) + " m";
    }

    // Every point's cross-section; the selected point's handles.
    private void DrawSections(DebugLines lines, ICurveTarget target, in View3D view)
    {
        var camera = view.Camera;
        for (var i = 0; i < target.PointCount; i++)
        {
            if (!SectionFrame(target, i, out var center, out var side))
                continue;
            var (left, right, bed) = SectionHandles(target, i, center, side);
            lines.AddLine(left, right, SectionColor);
            lines.AddLine(center, center - Vector3.UnitY * target.GetDepth(i), SectionColor);
            if (i != SelectedPoint)
                continue;
            var widthColor = Hot(CurvePart.Width, i) ? HotColor : SectionHandleColor;
            var depthColor = Hot(CurvePart.Depth, i) ? HotColor : SectionHandleColor;
            HandleDrawing.Square(lines, left, camera.Right, camera.Up, HandlePoints * view.Scale * camera.WorldPerPixel(left, view.ViewPixels.Y), widthColor);
            HandleDrawing.Square(lines, right, camera.Right, camera.Up, HandlePoints * view.Scale * camera.WorldPerPixel(right, view.ViewPixels.Y), widthColor);
            HandleDrawing.Square(lines, bed, camera.Right, camera.Up, HandlePoints * view.Scale * camera.WorldPerPixel(bed, view.ViewPixels.Y), depthColor);
        }
    }
}
