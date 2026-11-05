using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>Which part of a curve point the tool hit or drags.</summary>
public enum CurvePart : byte
{
    None,
    Point,
    In,
    Out,
}

/// <summary>
/// The curve tool's pure operations on an <see cref="ICurveTarget"/> (G13): insert by de Casteljau, smooth a point, and
/// hit tests in the view's world space. No undo here: <see cref="CurveEditor"/> wraps them in gestures.
/// </summary>
public static class CurveEditing
{
    /// <summary>Samples per segment for the segment hit test (refined afterwards).</summary>
    private const int SegmentSamples = 48;

    /// <summary>The number of segments (the seam counts when closed).</summary>
    public static int SegmentCount(ICurveTarget target) =>
        target.PointCount < 2 ? 0 : target.Closed ? target.PointCount : target.PointCount - 1;

    /// <summary>The segment's absolute control points in local space (p0, p0 + out, p1 + in, p1).</summary>
    public static void Controls(ICurveTarget target, int segment, out Vector3 p0, out Vector3 c0, out Vector3 c1, out Vector3 p1)
    {
        var end = segment + 1 == target.PointCount ? 0 : segment + 1;
        p0 = target.GetPosition(segment);
        c0 = p0 + target.GetOut(segment);
        p1 = target.GetPosition(end);
        c1 = p1 + target.GetIn(end);
    }

    /// <summary>The Bézier position on <paramref name="segment"/> at <paramref name="t"/>, in local space.</summary>
    public static Vector3 Sample(ICurveTarget target, int segment, float t)
    {
        Controls(target, segment, out var p0, out var c0, out var c1, out var p1);
        var u = 1f - t;
        return p0 * (u * u * u) + c0 * (3f * u * u * t) + c1 * (3f * u * t * t) + p1 * (t * t * t);
    }

    /// <summary>
    /// Inserts a point on <paramref name="segment"/> at <paramref name="t"/> (0..1) without changing the shape: the
    /// segment is split by de Casteljau, the new point takes the inner handles and its neighbours' handles shorten.
    /// Returns the new point's index.
    /// </summary>
    public static int Insert(ICurveTarget target, int segment, float t)
    {
        ArgumentNullException.ThrowIfNull(target);
        if ((uint)segment >= (uint)SegmentCount(target))
            throw new ArgumentOutOfRangeException(nameof(segment), segment, "No such segment.");
        t = Math.Clamp(t, 0f, 1f);
        Controls(target, segment, out var p0, out var c0, out var c1, out var p1);
        var a = Vector3.Lerp(p0, c0, t);
        var b = Vector3.Lerp(c0, c1, t);
        var c = Vector3.Lerp(c1, p1, t);
        var l2 = Vector3.Lerp(a, b, t);
        var r1 = Vector3.Lerp(b, c, t);
        var m = Vector3.Lerp(l2, r1, t);
        var end = segment + 1 == target.PointCount ? 0 : segment + 1;
        target.SetOut(segment, a - p0);
        target.SetIn(end, c - p1);
        var index = segment + 1; // the seam's new point goes last
        target.AddPoint(m, l2 - m, r1 - m, index);
        return index;
    }

    /// <summary>
    /// Gives point <paramref name="index"/> smooth, mirrored handles from its neighbours (Catmull-Rom with
    /// <paramref name="tension"/>, as <c>SetSmoothPoints</c>); an open curve's ends point at their one neighbour.
    /// </summary>
    public static void Smooth(ICurveTarget target, int index, float tension = 0.5f)
    {
        ArgumentNullException.ThrowIfNull(target);
        var count = target.PointCount;
        if (count < 2)
            return;
        var p = target.GetPosition(index);
        var previous = index > 0 ? target.GetPosition(index - 1) : target.Closed && count > 2 ? target.GetPosition(count - 1) : p;
        var next = index < count - 1 ? target.GetPosition(index + 1) : target.Closed && count > 2 ? target.GetPosition(0) : p;
        var handle = (next - previous) * (tension / 3f);
        target.SetIn(index, -handle);
        target.SetOut(index, handle);
    }

    /// <summary>The point or handle nearest <paramref name="global"/> within <paramref name="radius"/> (points win ties; later points are on top).</summary>
    public static CurvePart Hit(ICurveTarget target, Vector3 global, float radius, bool handles, out int index)
    {
        ArgumentNullException.ThrowIfNull(target);
        var found = -1;
        var part = CurvePart.None;
        var best = radius * radius;
        for (var i = target.PointCount - 1; i >= 0; i--)
        {
            var p = target.GetPosition(i);
            Consider(target.ToGlobal(p), global, CurvePart.Point, i, ref best, ref part, ref found);
            if (!handles)
                continue;
            if (target.GetIn(i) != Vector3.Zero)
                Consider(target.ToGlobal(p + target.GetIn(i)), global, CurvePart.In, i, ref best, ref part, ref found);
            if (target.GetOut(i) != Vector3.Zero)
                Consider(target.ToGlobal(p + target.GetOut(i)), global, CurvePart.Out, i, ref best, ref part, ref found);
        }

        index = found;
        return part;
    }

    private static void Consider(Vector3 at, Vector3 global, CurvePart kind, int i, ref float best, ref CurvePart part, ref int index)
    {
        var d = Vector3.DistanceSquared(at, global);
        if (d < best || d == best && kind == CurvePart.Point && part != CurvePart.Point)
        {
            best = d;
            part = kind;
            index = i;
        }
    }

    /// <summary>
    /// The segment nearest <paramref name="global"/> within <paramref name="radius"/>: its index, the parameter there and
    /// the point (view world space). False when no segment is that close.
    /// </summary>
    public static bool HitSegment(ICurveTarget target, Vector3 global, float radius, out int segment, out float t, out Vector3 point)
    {
        ArgumentNullException.ThrowIfNull(target);
        segment = -1;
        t = 0;
        point = default;
        var best = float.PositiveInfinity;
        var segments = SegmentCount(target);
        for (var s = 0; s < segments; s++)
        {
            for (var i = 0; i <= SegmentSamples; i++)
            {
                var u = i / (float)SegmentSamples;
                var d = Vector3.DistanceSquared(target.ToGlobal(Sample(target, s, u)), global);
                if (d < best)
                {
                    best = d;
                    segment = s;
                    t = u;
                }
            }
        }

        if (segment < 0)
            return false;
        // Refine: bisect the bracket around the best sample.
        var step = 1f / SegmentSamples;
        for (var k = 0; k < 16; k++)
        {
            step *= 0.5f;
            var lo = Math.Max(0f, t - step);
            var hi = Math.Min(1f, t + step);
            var dLo = Vector3.DistanceSquared(target.ToGlobal(Sample(target, segment, lo)), global);
            var dHi = Vector3.DistanceSquared(target.ToGlobal(Sample(target, segment, hi)), global);
            if (dLo < best && dLo <= dHi)
            {
                best = dLo;
                t = lo;
            }
            else if (dHi < best)
            {
                best = dHi;
                t = hi;
            }
        }

        point = target.ToGlobal(Sample(target, segment, t));
        return best <= radius * radius;
    }
}
