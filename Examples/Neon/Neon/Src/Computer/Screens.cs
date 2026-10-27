using System.Numerics;
using MainframeEngine;

namespace Neon;

/// <summary>
/// What the computer's monitors show, drawn into one 2D sub-viewport (<see cref="ScreenAtlas"/>, 3 × 2 cells of
/// <see cref="CellWidth"/> × <see cref="CellHeight"/>): each monitor's material shows one cell through its UV scale
/// and offset (ADR 0216). Panels redraw every frame from preallocated buffers and constant strings: nothing
/// allocates per frame.
/// </summary>
public static class Screens
{
    public const int CellWidth = 640;
    public const int CellHeight = 360;

    private static Font? _mono;

    /// <summary>The engine's Roboto Mono, loaded once.</summary>
    public static Font Mono => _mono ??= Font.FromData(File.ReadAllBytes(ContentPaths.Resolve("Assets/UI/fonts/RobotoMono-Regular.ttf")));

    public static readonly Vector4 Background = new(0.02f, 0.03f, 0.06f, 1f);
    public static readonly Vector4 Cyan = new(0.25f, 0.95f, 1f, 1f);
    public static readonly Vector4 Pink = new(1f, 0.25f, 0.7f, 1f);
    public static readonly Vector4 Amber = new(1f, 0.72f, 0.25f, 1f);
    public static readonly Vector4 Dim = new(0.35f, 0.45f, 0.6f, 1f);
    public static readonly Vector4 Green = new(0.35f, 1f, 0.55f, 1f);

    /// <summary>A cell's frame: background, border and a title bar.</summary>
    public static void Frame(CanvasItem canvas, string title, Vector4 accent)
    {
        canvas.DrawRect(new Rect2(0, 0, CellWidth, CellHeight), Background);
        canvas.DrawRect(new Rect2(0, 0, CellWidth, 26), accent * new Vector4(0.25f, 0.25f, 0.25f, 1f));
        canvas.DrawRect(new Rect2(1, 1, CellWidth - 2, CellHeight - 2), accent, filled: false, width: 2f);
        canvas.DrawString(Mono, new Vector2(10, 19), title, fontSize: 15, modulate: accent);
    }

    /// <summary>A small deterministic hash in [0, 1).</summary>
    public static float Hash(int n)
    {
        var x = (uint)n * 747796405u + 2891336453u;
        x = ((x >> (int)((x >> 28) + 4)) ^ x) * 277803737u;
        return ((x >> 22) ^ x) / 4294967296f;
    }
}

/// <summary>The terminal's cell: the scrollback and the input line with a blinking cursor.</summary>
public sealed class TerminalPanel : Node2D
{
    private float _time;

    public Terminal? Terminal { get; set; }

    /// <summary>Whether the keyboard is the terminal's (the player is seated): the cursor blinks.</summary>
    public bool Active { get; set; }

    protected override void OnProcess(in GameTime gameTime)
    {
        _time += gameTime.DeltaTime;
        QueueRedraw();
    }

    protected override void OnDraw()
    {
        Screens.Frame(this, "tty1 - neon//os", Screens.Green);
        if (Terminal is not { } terminal)
            return;
        var font = Screens.Mono;
        var y = 50f;
        foreach (var line in terminal.Lines)
        {
            DrawString(font, new Vector2(12, y), line, fontSize: 16, modulate: Screens.Green);
            y += 22f;
        }

        DrawString(font, new Vector2(12, y), terminal.InputLine, fontSize: 16, modulate: Screens.Green);
        if (!Active || (int)(_time * 2f) % 2 == 0)
        {
            var width = font.GetStringSize(terminal.InputLine, fontSize: 16).X;
            DrawRect(new Rect2(14 + width, y - 14, 9, 18), Active ? Screens.Green : Screens.Dim);
        }
    }
}

/// <summary>Three scrolling graphs (CPU, net, ice) with labels.</summary>
public sealed class GraphPanel : Node2D
{
    private const int Samples = 96;
    private readonly float[][] _history = [new float[Samples], new float[Samples], new float[Samples]];
    private readonly Vector2[] _points = new Vector2[Samples];
    private float _clock;
    private int _tick;

    private static readonly string[] Labels = ["cpu cluster", "net in/out", "ice pressure"];
    private static readonly Vector4[] Colors = [Screens.Cyan, Screens.Pink, Screens.Amber];

    protected override void OnProcess(in GameTime gameTime)
    {
        _clock += gameTime.DeltaTime;
        while (_clock > 0.08f)
        {
            _clock -= 0.08f;
            _tick++;
            for (var g = 0; g < 3; g++)
            {
                var h = _history[g];
                Array.Copy(h, 1, h, 0, Samples - 1);
                var wave = 0.5f + 0.3f * MathF.Sin(_tick * (0.07f + g * 0.05f) + g);
                h[Samples - 1] = Math.Clamp(wave + (Screens.Hash(_tick * 3 + g) - 0.5f) * 0.35f, 0.02f, 0.98f);
            }
        }

        QueueRedraw();
    }

    protected override void OnDraw()
    {
        Screens.Frame(this, "sys://monitor", Screens.Cyan);
        for (var g = 0; g < 3; g++)
        {
            var top = 40f + g * 106f;
            DrawRect(new Rect2(12, top, Screens.CellWidth - 24, 92), new Vector4(0.05f, 0.07f, 0.12f, 1f));
            for (var i = 0; i < Samples; i++)
                _points[i] = new Vector2(16 + i * (Screens.CellWidth - 32f) / (Samples - 1), top + 88 - _history[g][i] * 80f);
            DrawPolyline(_points, Colors[g], 2f);
            DrawString(Screens.Mono, new Vector2(20, top + 18), Labels[g], fontSize: 14, modulate: Colors[g]);
        }
    }
}

/// <summary>A scrolling dump of made-up hex with a highlighted line sweeping through it.</summary>
public sealed class HexPanel : Node2D
{
    private static readonly string[] Rows = MakeRows();
    private float _scroll;

    private static string[] MakeRows()
    {
        var rows = new string[48];
        const string hex = "0123456789ABCDEF";
        var chars = new char[47];
        for (var r = 0; r < rows.Length; r++)
        {
            for (var c = 0; c < chars.Length; c++)
                chars[c] = c % 3 == 2 ? ' ' : hex[(int)(Screens.Hash(r * 97 + c) * 16f)];
            rows[r] = $"{r * 16:X4}  " + new string(chars);
        }

        return rows;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        _scroll += gameTime.DeltaTime * 18f;
        QueueRedraw();
    }

    protected override void OnDraw()
    {
        Screens.Frame(this, "ice//breaker  [tracing]", Screens.Pink);
        const float lineHeight = 19f;
        var first = (int)(_scroll / lineHeight);
        var offset = _scroll % lineHeight;
        for (var i = 0; i < 17; i++)
        {
            var row = Rows[(first + i) % Rows.Length];
            var y = 48 + i * lineHeight - offset;
            if (y < 40)
                continue;
            var hot = (first + i) % 11 == 0;
            if (hot)
                DrawRect(new Rect2(8, y - 14, Screens.CellWidth - 16, 18), new Vector4(0.4f, 0.05f, 0.25f, 1f));
            DrawString(Screens.Mono, new Vector2(12, y), row, fontSize: 14, modulate: hot ? Screens.Pink : Screens.Dim);
        }
    }
}

/// <summary>
/// The city map: the street grid and moving traffic dots. <see cref="Positions"/> gives the dots (0..1 across the map)
/// when the traffic is running; until then the panel moves its own dots along the grid.
/// </summary>
public sealed class MapPanel : Node2D
{
    private const int Fallback = 40;
    private float _time;

    /// <summary>Fills the span with the traffic's map positions (0..1); returns how many it wrote.</summary>
    public MapPositions? Positions { get; set; }

    private readonly Vector2[] _dots = new Vector2[256];

    protected override void OnProcess(in GameTime gameTime)
    {
        _time += gameTime.DeltaTime;
        QueueRedraw();
    }

    protected override void OnDraw()
    {
        Screens.Frame(this, "grid//sector 9  traffic", Screens.Amber);
        var area = new Rect2(14, 36, Screens.CellWidth - 28, Screens.CellHeight - 50);
        for (var i = 0; i <= 8; i++)
        {
            var x = area.Position.X + area.Size.X * i / 8f;
            var y = area.Position.Y + area.Size.Y * i / 8f;
            DrawLine(new Vector2(x, area.Position.Y), new Vector2(x, area.End.Y), new Vector4(0.15f, 0.2f, 0.3f, 1f), 1f);
            DrawLine(new Vector2(area.Position.X, y), new Vector2(area.End.X, y), new Vector4(0.15f, 0.2f, 0.3f, 1f), 1f);
        }

        var count = Positions?.Invoke(_dots) ?? 0;
        if (count == 0)
        {
            count = Fallback;
            for (var i = 0; i < Fallback; i++)
            {
                var lane = (int)(Screens.Hash(i) * 9f) / 8f;
                var t = (Screens.Hash(i + 100) + _time * (0.02f + Screens.Hash(i + 200) * 0.05f)) % 1f;
                _dots[i] = i % 2 == 0 ? new Vector2(t, lane) : new Vector2(lane, t);
            }
        }

        for (var i = 0; i < count; i++)
        {
            var p = area.Position + _dots[i] * area.Size;
            DrawRect(new Rect2(p.X - 2, p.Y - 2, 4, 4), i % 5 == 0 ? Screens.Pink : Screens.Amber);
        }

        // You are here (the apartment's tower, the centre of the map), pulsing.
        var centre = area.Position + area.Size * 0.5f;
        DrawCircle(centre, 6f + 3f * MathF.Sin(_time * 4f), Screens.Cyan, filled: false, width: 2f);
        DrawString(Screens.Mono, centre + new Vector2(12, -8), "you", fontSize: 13, modulate: Screens.Cyan);
    }
}

/// <summary>Writes map positions (0..1 across the map) into <paramref name="into"/>; returns how many.</summary>
public delegate int MapPositions(Span<Vector2> into);

/// <summary>The clock, the weather and a music visualiser that dances while the stereo plays.</summary>
public sealed class StatusPanel : Node2D
{
    private const int Bars = 32;
    private static readonly string[] TwoDigits = MakeTwoDigits();
    private readonly float[] _levels = new float[Bars];
    private float _time;

    /// <summary>Minutes past midnight when the scene starts.</summary>
    public float StartMinutes { get; set; } = 23 * 60 + 41;

    public Stereo? Stereo { get; set; }

    private static string[] MakeTwoDigits()
    {
        var digits = new string[60];
        for (var i = 0; i < digits.Length; i++)
            digits[i] = i.ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        return digits;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        _time += gameTime.DeltaTime;
        var playing = Stereo is { IsOn: true };
        for (var i = 0; i < Bars; i++)
        {
            var target = playing
                ? 0.15f + 0.8f * MathF.Abs(MathF.Sin(_time * (2.1f + Screens.Hash(i) * 3f) + i * 0.7f)) * (1f - i / (float)Bars * 0.5f)
                : 0.03f;
            _levels[i] += (target - _levels[i]) * MathF.Min(1f, gameTime.DeltaTime * 12f);
        }

        QueueRedraw();
    }

    protected override void OnDraw()
    {
        Screens.Frame(this, "status", Screens.Pink);
        var minutes = (int)(StartMinutes + _time / 60f) % (24 * 60);
        var font = Screens.Mono;
        DrawString(font, new Vector2(24, 100), TwoDigits[minutes / 60 % 24], fontSize: 64, modulate: Screens.Cyan);
        DrawString(font, new Vector2(104, 98), (int)(_time * 2f) % 2 == 0 ? ":" : " ", fontSize: 64, modulate: Screens.Cyan);
        DrawString(font, new Vector2(140, 100), TwoDigits[minutes % 60], fontSize: 64, modulate: Screens.Cyan);
        DrawString(font, new Vector2(320, 60), "ACID RAIN  94%", fontSize: 18, modulate: Screens.Amber);
        DrawString(font, new Vector2(320, 86), "17 C  WIND SW 22", fontSize: 18, modulate: Screens.Amber);
        DrawString(font, new Vector2(320, 112), "CURFEW  ACTIVE", fontSize: 18, modulate: Screens.Pink);
        var baseY = Screens.CellHeight - 24f;
        var w = (Screens.CellWidth - 48f) / Bars;
        for (var i = 0; i < Bars; i++)
        {
            var h = _levels[i] * 170f;
            var t = i / (float)(Bars - 1);
            DrawRect(new Rect2(24 + i * w, baseY - h, w - 3, h), Vector4.Lerp(Screens.Cyan, Screens.Pink, t));
        }

        DrawString(font, new Vector2(24, baseY + 18), Stereo is { IsOn: true } ? "now playing" : "stereo idle", fontSize: 13, modulate: Screens.Dim);
    }
}
