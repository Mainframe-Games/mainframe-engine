using System.Globalization;
using System.Numerics;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The Curves pane's canvas (drawn in the bottom pane's view, <see cref="SongTab.RollViewport"/>): the edited curve of a
/// layer over its parameter, the other layers' curves on the same parameter and target as ghosts, its points, and a line
/// at the parameter's current value (the music's while it plays, else the Adaptive panel's audition value). Labels are
/// cached; steady frames allocate nothing.
/// </summary>
public sealed class CurvesCanvas(SongTab tab) : SongCanvas(tab)
{
    private static readonly (float Db, string Label)[] DbTicks =
        [(-80, "-80"), (-60, "-60"), (-40, "-40"), (-24, "-24"), (-12, "-12"), (-6, "-6"), (0, "0 dB")];

    private static readonly (float Hz, string Label)[] HzTicks =
        [(20, "20"), (50, "50"), (100, "100"), (200, "200"), (500, "500"), (1000, "1k"), (2000, "2k"), (5000, "5k"), (10000, "10k"), (20000, "20k Hz")];

    private static readonly Vector4 Back = Rgb(0x1b, 0x1d, 0x23);
    private static readonly Vector4 Plot = Rgb(0x15, 0x17, 0x1c);
    private static readonly Vector4 ValueLine = Rgb(0xfb, 0xbf, 0x24);

    private MusicCurveInfo? _labelsFor;
    private int _labelsVersion = -1;
    private string _header = "";
    private string[] _xLabels = [];
    private float[] _xValues = [];

    protected override void OnDraw()
    {
        var w = ViewSize.X;
        var h = ViewSize.Y;
        var curves = Tab.Curves;
        Box(0, 0, w, h, Back);
        var curve = curves.Curve;
        var p = curves.Parameter;
        if (curve is null || p is null || Song.Adaptive is not { } adaptive)
        {
            Text(Song.Adaptive is null
                ? "Curves belong to adaptive songs: Song › Make Adaptive, then a layer's + Curve in the Adaptive tab (A)"
                : "No curve selected: click a curve under a layer in the Adaptive tab (A), or add one with its + Curve", 14, 24, 12, RulerText);
            return;
        }

        Labels(curve, p, adaptive);
        var left = SongCurves.Left;
        var top = SongCurves.Top;
        var pw = curves.PlotWidth;
        var ph = curves.PlotHeight;
        Box(left, top, pw, ph, Plot);
        Text(_header, 12, 17, 11.5f, White);

        // The grid: the target's ticks on y, the parameter's values on x.
        if (curve.Target == MusicCurveTarget.Cutoff)
        {
            foreach (var (hz, label) in HzTicks)
                YTick(curves, curve.Target, hz, label, left, pw);
        }
        else
        {
            foreach (var (db, label) in DbTicks)
                YTick(curves, curve.Target, db, label, left, pw);
        }

        for (var i = 0; i < _xValues.Length; i++)
        {
            var x = curves.X(p, _xValues[i]);
            Line(x, top, x, top + ph, GridBeat);
            Text(_xLabels[i], x - 8, top + ph + 15, 10, RulerText);
        }

        // Ghosts: the other curves on this parameter and target, in their layers' colours.
        foreach (var other in adaptive.Curves)
        {
            if (ReferenceEquals(other, curve) || other.Parameter != curve.Parameter || other.Target != curve.Target)
                continue;
            DrawCurve(curves, other, p, WithAlpha(LayerColor(adaptive, other.Layer), 0.35f), 1.2f);
        }

        var color = LayerColor(adaptive, curve.Layer);
        DrawCurve(curves, curve, p, color, 2f);
        for (var i = 0; i < curve.Points.Count; i++)
        {
            var pt = curve.Points[i];
            var x = curves.X(p, pt.X);
            var y = curves.Y(curve.Target, pt.Y);
            var dragged = i == curves.DraggedPoint;
            Box(x - 4, y - 4, 8, 8, dragged ? White : color);
            Outline(x - 4, y - 4, 8, 8, Plot, 1);
        }

        // Where the parameter is now.
        var value = Tab.ParameterValue(p.Name);
        var vx = curves.X(p, value);
        Line(vx, top, vx, top + ph, ValueLine, 1.5f);
    }

    private void YTick(SongCurves curves, MusicCurveTarget target, float value, string label, float left, float pw)
    {
        var y = curves.Y(target, value);
        Line(left, y, left + pw, y, GridBeat);
        TextRight(label, left - 6, y + 4, 10, RulerText, 44);
    }

    private void DrawCurve(SongCurves curves, MusicCurveInfo curve, MusicParameterInfo p, Vector4 color, float width)
    {
        if (curves.Compiled(curve) is not { } compiled)
            return;
        var (min, max, columns) = SongCurves.Range(p);
        if (columns > 0)
        {
            // A step function: one level per value.
            for (var v = min; v <= max; v++)
            {
                var y = curves.Y(curve.Target, compiled.Evaluate(v));
                var x0 = curves.X(p, v - 0.5f);
                var x1 = curves.X(p, v + 0.5f);
                Line(x0, y, x1, y, color, width);
            }

            return;
        }

        var steps = Math.Max(2, (int)(curves.PlotWidth / 3));
        var px = curves.X(p, min);
        var py = curves.Y(curve.Target, compiled.Evaluate(min));
        for (var i = 1; i <= steps; i++)
        {
            var v = min + (max - min) * i / steps;
            var x = curves.X(p, v);
            var y = curves.Y(curve.Target, compiled.Evaluate(v));
            Line(px, py, x, y, color, width);
            (px, py) = (x, y);
        }
    }

    private static Vector4 LayerColor(SongAdaptive adaptive, string id)
    {
        for (var i = 0; i < adaptive.Layers.Count; i++)
        {
            if (adaptive.Layers[i].Id == id)
                return ColorOf(adaptive.Layers[i].Color, i);
        }

        return White;
    }

    // The header and x labels, made again only when the curve or the song changed.
    private void Labels(MusicCurveInfo curve, MusicParameterInfo p, SongAdaptive adaptive)
    {
        if (ReferenceEquals(curve, _labelsFor) && _labelsVersion == Tab.Document.Version)
            return;
        _labelsFor = curve;
        _labelsVersion = Tab.Document.Version;
        MakeLabels(curve, p, adaptive);
    }

    // Separate from Labels: its lambdas' closure is allocated on entry, so only when the labels are made.
    private void MakeLabels(MusicCurveInfo curve, MusicParameterInfo p, SongAdaptive adaptive)
    {
        var layer = adaptive.FindLayer(curve.Layer)?.Name ?? curve.Layer;
        _header = $"{layer} · {(curve.Target == MusicCurveTarget.Volume ? "volume" : "filter cutoff")} over {p.Name}" +
                  " · drag a point (Shift: one axis) · double-click adds or removes · right-click: shape";
        var (min, max, columns) = SongCurves.Range(p);
        if (p.Kind == MusicParameterKind.Labeled)
        {
            _xLabels = [.. p.Labels ?? []];
            _xValues = [.. Enumerable.Range(0, _xLabels.Length).Select(static i => (float)i)];
        }
        else if (columns > 0)
        {
            var count = Math.Min(columns, 32);
            _xValues = [.. Enumerable.Range(0, count).Select(i => min + i * (float)Math.Ceiling(columns / (double)count))];
            _xLabels = [.. _xValues.Select(static v => v.ToString("0", CultureInfo.InvariantCulture))];
        }
        else
        {
            _xValues = [.. Enumerable.Range(0, 5).Select(i => min + (max - min) * i / 4f)];
            _xLabels = [.. _xValues.Select(static v => v.ToString("0.##", CultureInfo.InvariantCulture))];
        }
    }
}
