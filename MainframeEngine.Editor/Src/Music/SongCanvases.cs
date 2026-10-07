using System.Globalization;
using System.Numerics;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// Base of the song tab's two canvases (drawn into their own <see cref="SubViewport"/>s with the engine's 2D canvas):
/// helpers that take dp and draw in framebuffer pixels (<see cref="PixelScale"/>), so text stays sharp on HiDPI screens.
/// Drawing allocates nothing in steady state: labels come from caches.
/// </summary>
[Tool]
public abstract class SongCanvas : Node2D
{
    private static readonly Dictionary<string, Vector4> Colors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] BarLabels = new string[1000];
    private static Font? _font;
    private static bool _fontLoaded;

    /// <summary>Track colours used when a track has none (by track index).</summary>
    public static readonly string[] Palette = ["#22d3ee", "#a78bfa", "#f59e0b", "#5eead4", "#f472b6", "#a3e635", "#60a5fa", "#fb923c"];

    protected static readonly Vector4 Ruler = Rgb(0x26, 0x29, 0x31);
    protected static readonly Vector4 RulerText = Rgb(0x8b, 0x93, 0xa5);
    protected static readonly Vector4 GridBar = Rgb(0x3b, 0x3f, 0x4b);
    protected static readonly Vector4 GridBeat = Rgb(0x2a, 0x2d, 0x36);
    protected static readonly Vector4 GridFine = Rgb(0x23, 0x26, 0x2d);
    protected static readonly Vector4 Playhead = Rgb(0xf8, 0x71, 0x71);
    protected static readonly Vector4 White = new(1, 1, 1, 1);

    protected SongCanvas(SongTab tab)
    {
        Tab = tab;
    }

    public SongTab Tab { get; }

    /// <summary>Framebuffer pixels per dp.</summary>
    public float PixelScale { get; set; } = 1;

    /// <summary>The canvas size in dp.</summary>
    public Vector2 ViewSize { get; set; } = new(800, 300);

    protected SongCanvasController Controller => Tab.Controller;

    protected Song Song => Tab.Document.Song;

    /// <summary>The editor UI font (null when the file is missing: text is skipped).</summary>
    protected static Font? TextFont
    {
        get
        {
            if (_fontLoaded)
                return _font;
            _fontLoaded = true;
            var path = Path.Combine(AppContext.BaseDirectory, "Content", "UI", "fonts", "LatoLatin-Regular.ttf");
            if (File.Exists(path))
                _font = Font.FromData(File.ReadAllBytes(path));
            return _font;
        }
    }

    protected void Box(float x, float y, float w, float h, Vector4 color)
    {
        if (w > 0 && h > 0)
            DrawRect(new Rect2(x * PixelScale, y * PixelScale, w * PixelScale, h * PixelScale), color);
    }

    protected void Outline(float x, float y, float w, float h, Vector4 color, float width = 1)
    {
        if (w > 0 && h > 0)
            DrawRect(new Rect2(x * PixelScale, y * PixelScale, w * PixelScale, h * PixelScale), color, filled: false, width * PixelScale);
    }

    protected void Line(float x0, float y0, float x1, float y1, Vector4 color, float width = 1) =>
        DrawLine(new Vector2(x0 * PixelScale, y0 * PixelScale), new Vector2(x1 * PixelScale, y1 * PixelScale), color, width * PixelScale);

    protected void Text(string text, float x, float baseline, float size, Vector4 color)
    {
        if (TextFont is { } font)
            DrawString(font, new Vector2(MathF.Round(x * PixelScale), MathF.Round(baseline * PixelScale)), text, HorizontalAlignment.Left, -1,
                (int)MathF.Round(size * PixelScale), color);
    }

    protected static Vector4 Rgb(int r, int g, int b, float a = 1) => new(r / 255f, g / 255f, b / 255f, a);

    /// <summary>A track's colour (its <c>#rrggbb</c>, else the palette by index).</summary>
    public static Vector4 TrackColor(SongTrack track, int index)
    {
        var hex = track.Color ?? Palette[index % Palette.Length];
        if (Colors.TryGetValue(hex, out var color))
            return color;
        color = TryParse(hex, out var parsed) ? parsed : TryParse(Palette[index % Palette.Length], out var fallback) ? fallback : White;
        Colors[hex] = color;
        return color;
    }

    private static bool TryParse(string hex, out Vector4 color)
    {
        color = default;
        var s = hex.AsSpan().TrimStart('#');
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return false;
        color = Rgb((v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff);
        return true;
    }

    protected static Vector4 WithAlpha(Vector4 c, float a) => new(c.X, c.Y, c.Z, a);

    protected static string BarLabel(long bar)
    {
        if (bar < 0 || bar >= BarLabels.Length)
            return "";
        return BarLabels[bar] ??= bar.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The playhead tick shown (the heard position).</summary>
    protected double PlayheadTick => Tab.Player.PositionTicks;
}

/// <summary>The arrangement: ruler (bar numbers, loop region, playhead), one lane per track with its clips.</summary>
public sealed class ArrangementCanvas : SongCanvas
{
    private static readonly Vector4 LaneEven = Rgb(0x1b, 0x1d, 0x23);
    private static readonly Vector4 LaneOdd = Rgb(0x1e, 0x20, 0x27);
    private static readonly Vector4 LaneSelected = Rgb(0x23, 0x26, 0x2e);
    private static readonly Vector4 LoopOn = Rgb(0x1d, 0x4e, 0xd8, 0.9f);
    private static readonly Vector4 LoopOff = Rgb(0x4b, 0x52, 0x63, 0.7f);

    public ArrangementCanvas(SongTab tab)
        : base(tab)
    {
    }

    protected override void OnDraw()
    {
        var c = Controller;
        var w = ViewSize.X;
        var h = ViewSize.Y;
        var song = Song;
        var tracks = song.Tracks;
        const float ruler = SongCanvasController.RulerHeight;
        const float lane = SongCanvasController.LaneHeight;

        Box(0, 0, w, h, LaneEven);

        // Lanes.
        for (var i = 0; i < tracks.Count; i++)
        {
            var y = c.LaneY(i);
            if (y + lane < ruler || y > h)
                continue;
            Box(0, y, w, lane, ReferenceEquals(tracks[i], Tab.SelectedTrack) ? LaneSelected : (i & 1) == 0 ? LaneEven : LaneOdd);
            Line(0, y + lane - 0.5f, w, y + lane - 0.5f, GridBeat);
        }

        DrawGrid(c, w, ruler, h);

        // Clips (the dragged one is drawn at its preview position).
        for (var i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            var color = TrackColor(track, i);
            foreach (var clip in track.Clips)
            {
                var moving = ReferenceEquals(clip, c.GhostClip) && !c.GhostCopy;
                var lanes = moving ? tracks.IndexOf(c.GhostTrack ?? track) : i;
                var start = moving ? c.GhostStart : clip.Start;
                var length = moving ? c.GhostLength : clip.Length;
                DrawClip(c, clip, lanes, start, length, color, ReferenceEquals(clip, Tab.SelectedClip), w, h);
            }
        }

        if (c.GhostClip is { } ghost && c.GhostCopy && c.GhostTrack is { } ghostTrack)
        {
            var index = tracks.IndexOf(ghostTrack);
            DrawClip(c, ghost, index, c.GhostStart, c.GhostLength, TrackColor(ghostTrack, index), true, w, h);
        }

        if (tracks.Count == 0)
            Text("No tracks · add one with + in the track list", 12, ruler + 24, 12, RulerText);

        DrawRuler(c, w);

        var x = c.ArrangeX(PlayheadTick);
        if (x >= 0 && x <= w)
        {
            Line(x, 0, x, h, Playhead, 1.5f);
            Box(x - 4, ruler - 7, 8, 7, Playhead);
        }
    }

    private void DrawGrid(SongCanvasController c, float w, float top, float h)
    {
        var bar = Song.TicksPerBar;
        var beat = SongGrid.TicksPerBeat(Song);
        var barWidth = (float)(bar * Tab.ArrangePixelsPerTick);
        var every = barWidth < 14 ? 8 : barWidth < 30 ? 4 : barWidth < 60 ? 2 : 1;
        var first = (long)Math.Floor(Tab.ArrangeScrollTick / bar);
        for (var b = first; ; b++)
        {
            var x = c.ArrangeX(b * bar);
            if (x > w)
                break;
            if (b % every == 0)
                Line(x, top, x, h, GridBar);
            if (barWidth >= 120)
                for (var t = beat; t < bar; t += beat)
                {
                    var bx = c.ArrangeX(b * bar + t);
                    if (bx >= 0 && bx <= w)
                        Line(bx, top, bx, h, GridBeat);
                }
        }
    }

    private void DrawRuler(SongCanvasController c, float w)
    {
        const float ruler = SongCanvasController.RulerHeight;
        Box(0, 0, w, ruler, Ruler);
        Line(0, ruler - 0.5f, w, ruler - 0.5f, GridBar);
        var loop = Song.Loop;
        var lx = c.ArrangeX(loop.Start);
        var lw = c.ArrangeX(loop.End) - lx;
        Box(lx, 2, lw, SongCanvasController.LoopBandHeight - 2, loop.Enabled ? LoopOn : LoopOff);
        var bar = Song.TicksPerBar;
        var barWidth = (float)(bar * Tab.ArrangePixelsPerTick);
        var every = barWidth < 14 ? 8 : barWidth < 30 ? 4 : barWidth < 60 ? 2 : 1;
        var first = (long)Math.Floor(Tab.ArrangeScrollTick / bar);
        for (var b = first; ; b++)
        {
            var x = c.ArrangeX(b * bar);
            if (x > w)
                break;
            if (b % every != 0)
                continue;
            Line(x, ruler - 10, x, ruler, GridBar);
            Text(BarLabel(b + 1), x + 3, ruler - 3, 10, RulerText);
        }
    }

    private void DrawClip(SongCanvasController c, SongClip clip, int lane, long start, long length, Vector4 color, bool selected, float w, float h)
    {
        var y = c.LaneY(lane) + 3;
        var height = SongCanvasController.LaneHeight - 6;
        var x0 = c.ArrangeX(start);
        var x1 = c.ArrangeX(start + length);
        if (x1 < 0 || x0 > w || y + height < SongCanvasController.RulerHeight || y > h)
            return;
        var cx = MathF.Max(x0, -2);
        var cw = MathF.Min(x1, w + 2) - cx;
        Box(cx + 1, y, cw - 2, height, WithAlpha(color, 0.28f));
        Box(cx + 1, y, cw - 2, 4, color);
        if (clip is MidiClip midi && midi.Notes.Count > 0)
        {
            int lo = 127, hi = 0;
            foreach (var n in midi.Notes)
            {
                lo = Math.Min(lo, n.Pitch);
                hi = Math.Max(hi, n.Pitch);
            }

            var span = Math.Max(12, hi - lo + 1);
            var top = y + 7;
            var area = height - 10;
            foreach (var n in midi.Notes)
            {
                if (n.Start >= length)
                    continue;
                var nx = c.ArrangeX(start + n.Start);
                var nw = MathF.Max(1.5f, (float)(Math.Min(n.Length, length - n.Start) * Tab.ArrangePixelsPerTick));
                if (nx + nw < 0 || nx > w)
                    continue;
                var ny = top + area * (1 - (n.Pitch - lo + 0.5f) / span);
                Box(nx, ny - 1, nw, 2.4f, color);
            }
        }
        else if (clip is AudioClip audio)
        {
            DrawWaveform(c, audio, start, x0, x1, y + 6, height - 8, color, w);
        }

        if (selected)
            Outline(x0 + 1, y, x1 - x0 - 2, height, White, 1.5f);
    }

    private void DrawWaveform(SongCanvasController c, AudioClip clip, long start, float x0, float x1, float top, float height,
        Vector4 color, float w)
    {
        if (SongWaveforms.Get(clip.File) is not { } wave || wave.Seconds <= 0)
            return;
        var mid = top + height * 0.5f;
        var half = height * 0.5f;
        var tint = WithAlpha(color, 0.85f);
        for (var x = MathF.Max(x0 + 1, 0); x < MathF.Min(x1 - 1, w); x += 2)
        {
            var ticks = c.ArrangeTickAt(x) - start + clip.Offset;
            var seconds = Song.TicksToSeconds(ticks);
            var bin = (int)(seconds / wave.Seconds * wave.Bins);
            if (bin < 0 || bin >= wave.Bins)
                continue;
            var lo = wave.Min[bin];
            var hi = wave.Max[bin];
            Box(x, mid - hi * half, 1.4f, MathF.Max(1, (hi - lo) * half), tint);
        }
    }
}

/// <summary>The piano roll of the selected MIDI clip: ruler, keyboard, note grid and the velocity lane.</summary>
public sealed class PianoRollCanvas : SongCanvas
{
    private static readonly Vector4 RowWhite = Rgb(0x1f, 0x21, 0x28);
    private static readonly Vector4 RowBlack = Rgb(0x19, 0x1b, 0x20);
    private static readonly Vector4 Outside = Rgb(0x10, 0x11, 0x15, 0.55f);
    private static readonly Vector4 KeyWhite = Rgb(0xe5, 0xe7, 0xeb);
    private static readonly Vector4 KeyBlack = Rgb(0x1f, 0x29, 0x37);
    private static readonly Vector4 KeyText = Rgb(0x37, 0x41, 0x51);
    private static readonly Vector4 VelocityBack = Rgb(0x17, 0x19, 0x1e);
    private static readonly Vector4 Band = Rgb(0x3b, 0x82, 0xf6, 0.18f);
    private static readonly Vector4 BandEdge = Rgb(0x3b, 0x82, 0xf6, 0.9f);

    public PianoRollCanvas(SongTab tab)
        : base(tab)
    {
    }

    protected override void OnDraw()
    {
        var c = Controller;
        var w = ViewSize.X;
        var h = ViewSize.Y;
        const float key = SongCanvasController.KeyboardWidth;
        const float ruler = SongCanvasController.RollRulerHeight;
        const float row = SongCanvasController.RowHeight;
        var gridBottom = c.GridBottom;
        if (Tab.RollClip is not { } clip || Tab.SelectedTrack is not { } track)
        {
            Text(Tab.SelectedTrack?.Kind == SongTrackKind.Audio
                    ? "Audio clips have no notes · select a MIDI clip"
                    : "Select a MIDI clip in the arrangement · double-click an empty lane to create one",
                16, h * 0.5f, 12, RulerText);
            return;
        }

        var color = TrackColor(track, Song.Tracks.IndexOf(track));

        // Rows.
        var topPitch = Math.Min(127, c.PitchAt(ruler));
        for (var p = topPitch; p >= 0; p--)
        {
            var y = c.PitchY(p);
            if (y > gridBottom)
                break;
            Box(key, y, w - key, row, SongGrid.IsBlackKey(p) ? RowBlack : RowWhite);
            if (p % 12 == 0)
                Line(key, y + row - 0.5f, w, y + row - 0.5f, GridBar);
        }

        DrawTimeGrid(c, clip, w, ruler, gridBottom);

        // Outside the clip.
        var cx0 = c.RollX(0);
        var cx1 = c.RollX(clip.Length);
        if (cx0 > key)
            Box(key, ruler, cx0 - key, gridBottom - ruler, Outside);
        if (cx1 < w)
            Box(MathF.Max(key, cx1), ruler, w - MathF.Max(key, cx1), gridBottom - ruler, Outside);

        // Notes.
        foreach (var n in clip.Notes)
        {
            var x0 = c.RollX(n.Start);
            var x1 = c.RollX(n.Start + n.Length);
            var y = c.PitchY(n.Pitch);
            if (x1 < key || x0 > w || y + row < ruler || y > gridBottom)
                continue;
            var selected = Tab.SelectedNotes.Contains(n);
            var left = MathF.Max(x0, key);
            Box(left, y + 1, MathF.Max(2, x1 - left - 1), row - 2, WithAlpha(color, 0.45f + 0.55f * n.Velocity / 127f));
            if (selected)
                Outline(left, y + 1, MathF.Max(2, x1 - left - 1), row - 2, White, 1.25f);
        }

        if (c.Gesture == SongGesture.RubberBand)
        {
            var (from, to) = c.RubberBand;
            var bx = MathF.Min(from.X, to.X);
            var by = MathF.Min(from.Y, to.Y);
            Box(bx, by, MathF.Abs(to.X - from.X), MathF.Abs(to.Y - from.Y), Band);
            Outline(bx, by, MathF.Abs(to.X - from.X), MathF.Abs(to.Y - from.Y), BandEdge);
        }

        // Velocity lane.
        Box(key, gridBottom, w - key, h - gridBottom, VelocityBack);
        Line(0, gridBottom + 0.5f, w, gridBottom + 0.5f, GridBar);
        var vTop = gridBottom + 6;
        var vHeight = SongCanvasController.VelocityHeight - 10;
        foreach (var n in clip.Notes)
        {
            var x = c.RollX(n.Start);
            if (x < key || x > w)
                continue;
            var top = vTop + vHeight * (1 - n.Velocity / 127f);
            var tint = Tab.SelectedNotes.Contains(n) ? White : color;
            Line(x + 1, vTop + vHeight, x + 1, top, tint, 1.5f);
            Box(x - 1.5f, top - 1.5f, 5, 3, tint);
        }

        DrawKeyboard(c, ruler, gridBottom);
        Box(0, gridBottom, key, h - gridBottom, Ruler);
        Text("Velocity", 6, gridBottom + 16, 9.5f, RulerText);
        DrawRuler(c, clip, w);

        var px = c.RollX(PlayheadTick - clip.Start);
        if (px >= key && px <= w)
            Line(px, 0, px, gridBottom, Playhead, 1.5f);
    }

    private void DrawTimeGrid(SongCanvasController c, MidiClip clip, float w, float top, float bottom)
    {
        var song = Song;
        var bar = song.TicksPerBar;
        var beat = SongGrid.TicksPerBeat(song);
        var step = SongGrid.Step(Tab.Snap, song.Ppq);
        var fine = step > 1 && step * Tab.RollPixelsPerTick >= 8 ? step : beat;
        var firstAbs = clip.Start + (long)Math.Floor(Tab.RollScrollTick);
        var t = SongGrid.Floor(firstAbs, fine);
        for (var guard = 0; guard < 4000; guard++, t += fine)
        {
            var x = c.RollX(t - clip.Start);
            if (x > w)
                break;
            if (x < SongCanvasController.KeyboardWidth)
                continue;
            Line(x, top, x, bottom, t % bar == 0 ? GridBar : t % beat == 0 ? GridBeat : GridFine);
        }
    }

    private void DrawKeyboard(SongCanvasController c, float top, float bottom)
    {
        const float key = SongCanvasController.KeyboardWidth;
        const float row = SongCanvasController.RowHeight;
        Box(0, top, key, bottom - top, KeyWhite);
        var topPitch = Math.Min(127, c.PitchAt(top));
        for (var p = topPitch; p >= 0; p--)
        {
            var y = c.PitchY(p);
            if (y > bottom)
                break;
            if (SongGrid.IsBlackKey(p))
                Box(0, y, key * 0.62f, row, KeyBlack);
            else
                Line(0, y + row - 0.5f, key, y + row - 0.5f, Rgb(0xc4, 0xc8, 0xd0));
            if (p % 12 == 0)
                Text(SongGrid.NoteName(p), key - 22, y + row - 2, 9, KeyText);
        }

        Line(key - 0.5f, top, key - 0.5f, bottom, GridBar);
    }

    private void DrawRuler(SongCanvasController c, MidiClip clip, float w)
    {
        const float ruler = SongCanvasController.RollRulerHeight;
        Box(0, 0, w, ruler, Ruler);
        Line(0, ruler - 0.5f, w, ruler - 0.5f, GridBar);
        var bar = Song.TicksPerBar;
        var first = SongGrid.Floor(clip.Start + (long)Math.Floor(Tab.RollScrollTick), bar);
        for (var t = first; ; t += bar)
        {
            var x = c.RollX(t - clip.Start);
            if (x > w)
                break;
            if (x < SongCanvasController.KeyboardWidth)
                continue;
            Line(x, ruler - 7, x, ruler, GridBar);
            Text(BarLabel(t / bar + 1), x + 3, ruler - 4, 10, RulerText);
        }
    }
}

/// <summary>Min/max peaks of audio files for the arrangement's audio clips, computed in the background and cached.</summary>
public static class SongWaveforms
{
    public const int MaxBins = 4096;

    private static readonly Dictionary<string, Waveform?> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Pending = new(StringComparer.Ordinal);
    private static readonly object Gate = new();

    /// <summary>A file's peaks: <see cref="Bins"/> columns over <see cref="Seconds"/>, values −1…1.</summary>
    public sealed record Waveform(float[] Min, float[] Max, int Bins, double Seconds);

    /// <summary>The cached peaks, or null while they load (or when the file cannot be read).</summary>
    public static Waveform? Get(string file)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(file, out var cached))
                return cached;
            if (!Pending.Add(file))
                return null;
        }

        _ = Task.Run(() =>
        {
            var result = Load(file);
            lock (Gate)
            {
                Cache[file] = result;
                Pending.Remove(file);
            }
        });
        return null;
    }

    /// <summary>The peaks now (decodes on this thread when not cached): drops and picked files need the length.</summary>
    public static Waveform? GetNow(string file)
    {
        lock (Gate)
            if (Cache.TryGetValue(file, out var cached))
                return cached;
        var result = Load(file);
        lock (Gate)
            Cache[file] = result;
        return result;
    }

    /// <summary>Forgets every file (the project changed).</summary>
    public static void Clear()
    {
        lock (Gate)
            Cache.Clear();
    }

    private static Waveform? Load(string file)
    {
        try
        {
            if (SongSnapshotBuilder.LoadClip(file) is not { } stream || !stream.Preload())
                return null;
            var samples = stream.DecodedSamples.Span;
            var channels = Math.Max(1, stream.Channels);
            var frames = samples.Length / channels;
            if (frames == 0 || stream.SampleRate <= 0)
                return null;
            var bins = Math.Min(MaxBins, frames);
            var min = new float[bins];
            var max = new float[bins];
            for (var b = 0; b < bins; b++)
            {
                var from = (long)b * frames / bins;
                var to = Math.Max(from + 1, (long)(b + 1) * frames / bins);
                float lo = 0, hi = 0;
                for (var f = from; f < to; f++)
                    for (var ch = 0; ch < channels; ch++)
                    {
                        var s = samples[(int)(f * channels + ch)];
                        lo = MathF.Min(lo, s);
                        hi = MathF.Max(hi, s);
                    }

                min[b] = Math.Clamp(lo, -1, 1);
                max[b] = Math.Clamp(hi, -1, 1);
            }

            return new Waveform(min, max, bins, (double)frames / stream.SampleRate);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            Log.Warning($"[Music] No waveform for '{file}': {e.Message}");
            return null;
        }
    }
}
