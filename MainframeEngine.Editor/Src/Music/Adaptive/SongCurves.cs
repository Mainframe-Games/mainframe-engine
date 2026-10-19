using System.Globalization;
using System.Numerics;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The Curves pane's state and gestures (the third bottom-pane editor, beside the piano roll and the mixer; A5): the
/// curve being edited (<see cref="SongTab.SelectedCurve"/>), the mapping between the canvas and the curve's parameter (x)
/// and target (y: dB −80…+6 for volume, log Hz 20 Hz…20 kHz for cutoff), and the point gestures — drag a point (Shift:
/// one axis), double-click to add or remove one — each one <see cref="SongDocument"/> entry. Positions are the canvas's
/// local dp, as the <see cref="SongView"/> routes them.
/// </summary>
public sealed class SongCurves(SongTab tab)
{
    public const float Left = 52;
    public const float Right = 16;
    public const float Top = 26;
    public const float Bottom = 24;
    public const float MinDb = -80;
    public const float MaxDb = 6;
    public const float PointReach = 8;

    private readonly Dictionary<MusicCurveInfo, MusicCurve> _compiled = new(ReferenceEqualityComparer.Instance);
    private int _compiledVersion = -1;
    private List<MusicCurvePoint>? _dragFrom;
    private int _dragIndex = -1;
    private Vector2 _press;
    private int _dragCounter;

    /// <summary>The canvas size in dp (set by the <see cref="SongView"/>).</summary>
    public Vector2 Size { get; set; } = new(800, 300);

    /// <summary>The point being dragged (its index in the curve's points), or −1.</summary>
    public int DraggedPoint => _dragIndex;

    /// <summary>Shows the curve's shape menu at a local point (a right-click; the <see cref="SongView"/> hooks it).</summary>
    public Action<MusicCurveInfo, Vector2>? RequestMenu { get; set; }

    private SongDocument Doc => tab.Document;

    /// <summary>The curve shown (null: none, or it went away).</summary>
    public MusicCurveInfo? Curve => tab.SelectedCurve is { } c && Doc.Song.Adaptive?.Curves.Contains(c) == true ? c : null;

    /// <summary>The curve's parameter, or null.</summary>
    public MusicParameterInfo? Parameter => Curve is { } c ? Doc.Song.Adaptive?.FindParameter(c.Parameter) : null;

    // ── Geometry ─────────────────────────────────────────────────────────────────────────────────────────────────

    public float PlotWidth => MathF.Max(10, Size.X - Left - Right);

    public float PlotHeight => MathF.Max(10, Size.Y - Top - Bottom);

    /// <summary>The parameter's range on x: continuous min…max; discrete and labeled one column per value.</summary>
    public static (float Min, float Max, int Columns) Range(MusicParameterInfo p) => p.Kind switch
    {
        MusicParameterKind.Labeled => (0, Math.Max(0, (p.Labels?.Count ?? 1) - 1), Math.Max(1, p.Labels?.Count ?? 1)),
        MusicParameterKind.Discrete => (p.Min, Math.Max(p.Min, p.Max), Math.Max(1, (int)(p.Max - p.Min) + 1)),
        _ => (p.Min, p.Max > p.Min ? p.Max : p.Min + 1, 0),
    };

    /// <summary>The x (dp) of parameter value <paramref name="value"/>.</summary>
    public float X(MusicParameterInfo p, float value)
    {
        var (min, max, columns) = Range(p);
        if (columns > 0)
            return Left + (value - min + 0.5f) / columns * PlotWidth;
        return Left + (value - min) / (max - min) * PlotWidth;
    }

    /// <summary>The parameter value at x (dp), clamped; whole values for discrete and labeled parameters.</summary>
    public float ValueAt(MusicParameterInfo p, float x)
    {
        var (min, max, columns) = Range(p);
        var t = (x - Left) / PlotWidth;
        if (columns > 0)
            return Math.Clamp(MathF.Floor(t * columns) + min, min, max);
        return Math.Clamp(min + t * (max - min), min, max);
    }

    /// <summary>The y (dp) of a volume (dB) or cutoff (Hz).</summary>
    public float Y(MusicCurveTarget target, float value)
    {
        float t;
        if (target == MusicCurveTarget.Cutoff)
        {
            var hz = Math.Clamp(value, MusicCurve.MinCutoffHz, MusicCurve.MaxCutoffHz);
            t = MathF.Log(hz / MusicCurve.MinCutoffHz) / MathF.Log(MusicCurve.MaxCutoffHz / MusicCurve.MinCutoffHz);
        }
        else
        {
            t = (Math.Clamp(value, MinDb, MaxDb) - MinDb) / (MaxDb - MinDb);
        }

        return Top + (1 - t) * PlotHeight;
    }

    /// <summary>The volume (dB, whole tenths) or cutoff (Hz, whole) at y (dp), clamped to the target's range.</summary>
    public float TargetAt(MusicCurveTarget target, float y)
    {
        var t = Math.Clamp(1 - (y - Top) / PlotHeight, 0, 1);
        if (target == MusicCurveTarget.Cutoff)
            return MathF.Round(MusicCurve.MinCutoffHz * MathF.Pow(MusicCurve.MaxCutoffHz / MusicCurve.MinCutoffHz, t));
        return MathF.Round((MinDb + t * (MaxDb - MinDb)) * 10) / 10;
    }

    /// <summary>The compiled curve (for drawing; recompiled when the song changed), or null.</summary>
    public MusicCurve? Compiled(MusicCurveInfo curve)
    {
        if (Doc.Version != _compiledVersion)
        {
            _compiled.Clear();
            _compiledVersion = Doc.Version;
        }

        if (_compiled.TryGetValue(curve, out var compiled))
            return compiled;
        if (curve.Points.Count == 0)
            return null;
        var p = Doc.Song.Adaptive?.FindParameter(curve.Parameter);
        compiled = new MusicCurve(0, curve.Target, 0, curve.Shape, p is { Kind: not MusicParameterKind.Continuous }, [.. curve.Points]);
        _compiled[curve] = compiled;
        return compiled;
    }

    /// <summary>The point of the curve under <paramref name="at"/> (dp), or −1.</summary>
    public int PointAt(Vector2 at)
    {
        if (Curve is not { } curve || Parameter is not { } p)
            return -1;
        var best = -1;
        var reach = PointReach;
        for (var i = 0; i < curve.Points.Count; i++)
        {
            var pt = curve.Points[i];
            var d = Vector2.Distance(new Vector2(X(p, pt.X), Y(curve.Target, pt.Y)), at);
            if (d <= reach)
                (best, reach) = (i, d);
        }

        return best;
    }

    // ── Gestures ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A press at <paramref name="at"/>: grabs a point; a double-click removes it, or adds one on empty ground.</summary>
    public void Press(Vector2 at, EditorModifiers modifiers, bool doubleClick)
    {
        _dragIndex = -1;
        _dragFrom = null;
        if (Curve is not { } curve || Parameter is not { } p || Doc.IsReadOnly)
            return;
        var hit = PointAt(at);
        if (doubleClick)
        {
            if (hit >= 0 && curve.Points.Count > 1)
            {
                var points = new List<MusicCurvePoint>(curve.Points);
                points.RemoveAt(hit);
                Doc.SetCurvePoints(curve, points);
            }
            else if (hit < 0 && at.X >= Left && at.X <= Size.X - Right)
            {
                var points = new List<MusicCurvePoint>(curve.Points) { new(ValueAt(p, at.X), TargetAt(curve.Target, at.Y)) };
                Doc.SetCurvePoints(curve, points);
            }

            return;
        }

        if (hit < 0)
            return;
        Doc.History.EndMerge();
        _dragCounter++;
        _dragIndex = hit;
        _dragFrom = new List<MusicCurvePoint>(curve.Points);
        _press = at;
    }

    /// <summary>The dragged point follows the mouse (Shift: only along the axis it moved most on).</summary>
    public void Drag(Vector2 at, EditorModifiers modifiers)
    {
        if (_dragIndex < 0 || _dragFrom is not { } from || Curve is not { } curve || Parameter is not { } p)
            return;
        var original = from[_dragIndex];
        var x = ValueAt(p, at.X);
        var y = TargetAt(curve.Target, at.Y);
        if ((modifiers & EditorModifiers.Shift) != 0)
        {
            if (MathF.Abs(at.X - _press.X) >= MathF.Abs(at.Y - _press.Y))
                y = original.Y;
            else
                x = original.X;
        }

        var points = new List<MusicCurvePoint>(from);
        points[_dragIndex] = new MusicCurvePoint(x, y);
        Doc.SetCurvePoints(curve, points, "curve-" + _dragCounter.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>The drag ends: one history entry.</summary>
    public void Release()
    {
        _dragIndex = -1;
        _dragFrom = null;
        Doc.History.EndMerge();
    }

    /// <summary>A right-click: the curve's menu (shape, remove).</summary>
    public void ContextMenu(Vector2 at)
    {
        if (Curve is { } curve)
            RequestMenu?.Invoke(curve, at);
    }

    /// <summary>A curve made by <c>+ Curve</c>: volume from silent to full, or cutoff from 800 Hz to open, over the parameter's range.</summary>
    public static MusicCurveInfo NewCurve(string layer, MusicCurveTarget target, MusicParameterInfo p)
    {
        var (min, max, _) = Range(p);
        return new MusicCurveInfo
        {
            Layer = layer,
            Target = target,
            Parameter = p.Name,
            Shape = MusicCurveShape.Smooth,
            Points = target == MusicCurveTarget.Volume ? [new(min, MinDb), new(max, 0)] : [new(min, 800), new(max, MusicCurve.MaxCutoffHz)],
        };
    }
}
