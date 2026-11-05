using System.Numerics;

namespace MainframeEngine.Editor;

/// <summary>
/// The Animation panel's timeline (G1.6), on the shared timeline component: the seconds (or frames) ruler, one row per
/// track with its keys as diamonds (selected keys blue, method keys labelled, audio keys as clips), the area past the
/// animation's length shaded, the rubber band and the playhead. Draws from <see cref="AnimationEditor"/>; allocates
/// nothing per frame beyond the cached ruler labels.
/// </summary>
[Tool]
public sealed class AnimationTimelineCanvas(AnimationEditor editor) : TimelineCanvasBase
{
    private static readonly Vector4 RowEven = Rgb(0x1b, 0x1d, 0x23);
    private static readonly Vector4 RowOdd = Rgb(0x20, 0x23, 0x2a);
    private static readonly Vector4 KeyColor = Rgb(0xe5, 0xe7, 0xeb);
    private static readonly Vector4 KeySelected = Rgb(0x60, 0xa5, 0xfa);
    private static readonly Vector4 KeyDisabled = Rgb(0x6b, 0x72, 0x80);
    private static readonly Vector4 MethodColor = Rgb(0xf5, 0x9e, 0x0b);
    private static readonly Vector4 AudioColor = Rgb(0x34, 0xd3, 0x99);
    private static readonly Vector4 PastEnd = Rgb(0x0b, 0x0c, 0x10, 0.55f);
    private static readonly Vector4 BandFill = Rgb(0x60, 0xa5, 0xfa, 0.15f);
    private static readonly Vector4 BandLine = Rgb(0x60, 0xa5, 0xfa, 0.8f);
    private static readonly Vector4 Empty = Rgb(0x6b, 0x74, 0x88);

    /// <summary>The rubber band in dp while one is dragged (x0, y0, x1, y1), else null.</summary>
    public Vector4? Band { get; set; }

    protected override void OnDraw()
    {
        var w = ViewSize.X;
        var h = ViewSize.Y;
        Box(0, 0, w, h, RowEven);
        var view = editor.View;
        var ruler = AnimationEditor.RulerHeight;
        if (editor.Animation is not { } animation)
        {
            Text("Select an AnimationPlayer and create an animation.", 12, ruler + 18, 11, Empty);
            return;
        }

        var tracks = animation.Tracks;
        var rows = tracks.Count;
        var bottom = MathF.Min(h, ruler + rows * AnimationEditor.RowHeight);
        for (var i = 0; i < rows; i++)
            Box(0, ruler + i * AnimationEditor.RowHeight, w, AnimationEditor.RowHeight, i % 2 == 0 ? RowEven : RowOdd);
        DrawSecondsRuler(view, ruler, MathF.Max(bottom, ruler), editor.ShowFrames ? 1.0 / animation.Step : 0);
        DrawRegion(view, animation.Length, view.TimeAt(w) + 1, ruler, h, PastEnd);

        for (var t = 0; t < rows; t++)
        {
            var track = tracks[t];
            var mid = ruler + (t + 0.5f) * AnimationEditor.RowHeight;
            var times = track.Times;
            for (var k = 0; k < times.Length; k++)
            {
                var x = view.X(times[k]);
                if (x < -40 || x > w + 8)
                    continue;
                var selected = IsSelected(t, k);
                switch (track)
                {
                    case MethodTrack method:
                        Diamond(x, mid, 5.5f, selected ? KeySelected : MethodColor);
                        if (k < method.Methods.Length)
                            Text(method.Methods[k], x + 8, mid + 4, 10, MethodColor);
                        break;
                    case AudioTrack audio:
                        var length = k < audio.Streams.Length && audio.Streams[k] is { } stream ? Math.Max(0.2, stream.Length) : 0.5;
                        Box(x, mid - 7, MathF.Max(6f, view.X(times[k] + length) - x), 14, WithAlpha(AudioColor, 0.35f));
                        Outline(x, mid - 7, MathF.Max(6f, view.X(times[k] + length) - x), 14, selected ? KeySelected : AudioColor);
                        break;
                    default:
                        Diamond(x, mid, 5f, selected ? KeySelected : track.Enabled ? KeyColor : KeyDisabled);
                        break;
                }
            }
        }

        if (Band is { } band)
        {
            var (x0, y0) = (MathF.Min(band.X, band.Z), MathF.Min(band.Y, band.W));
            var (x1, y1) = (MathF.Max(band.X, band.Z), MathF.Max(band.Y, band.W));
            Box(x0, y0, x1 - x0, y1 - y0, BandFill);
            Outline(x0, y0, x1 - x0, y1 - y0, BandLine);
        }

        DrawPlayhead(view, editor.Playhead, ruler, h);
    }

    private bool IsSelected(int track, int key)
    {
        var selected = editor.Selected;
        for (var i = 0; i < selected.Count; i++)
            if (selected[i].Track == track && selected[i].Key == key)
                return true;
        return false;
    }

    private void Diamond(float x, float y, float r, Vector4 color)
    {
        // A filled diamond from horizontal strips (the canvas draws boxes and lines).
        for (var i = -r; i <= r; i += 1f)
        {
            var half = r - MathF.Abs(i);
            Box(x - half, y + i, half * 2, 1.2f, color);
        }
    }
}
