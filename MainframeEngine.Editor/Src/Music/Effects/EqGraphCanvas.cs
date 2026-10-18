using System.Numerics;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The Channel EQ's curve in its plugin window: a log-frequency grid (20 Hz–20 kHz, ±24 dB), each enabled band's own
/// response faintly in its colour, the summed response (<see cref="EqEffect.ResponseDb"/>, the same coefficients the DSP
/// plays) and a handle per band. Drawn into a sub-viewport only when the parameters or the size change.
/// </summary>
public sealed class EqGraphCanvas : EditorCanvas
{
    public const float MinFrequency = 20;
    public const float MaxFrequency = 20000;
    public const float RangeDb = 24;
    public const float HandleRadius = 6;
    private const int Points = 256;
    private const double DisplayRate = 48000;

    /// <summary>The band colours (also the window's section swatches), in <see cref="EqEffect.Bands"/> order.</summary>
    public static readonly string[] BandColors = ["#94a3b8", "#f97316", "#facc15", "#4ade80", "#22d3ee", "#818cf8", "#e879f9", "#94a3b8"];

    private static readonly Vector4[] BandVectors = [.. BandColors.Select(ParseColor)];
    private readonly float[] _curve = new float[Points];

    /// <summary>The insert's parameters (null: defaults).</summary>
    public IReadOnlyDictionary<string, float>? Values { get; set; }

    /// <summary>The band whose handle is being dragged or was last touched (−1: none).</summary>
    public int SelectedBand { get; set; } = -1;

    /// <summary>The x (dp) of <paramref name="frequency"/>.</summary>
    public float X(float frequency) =>
        ViewSize.X * MathF.Log(Math.Clamp(frequency, MinFrequency, MaxFrequency) / MinFrequency) / MathF.Log(MaxFrequency / MinFrequency);

    public float Frequency(float x) => MinFrequency * MathF.Pow(MaxFrequency / MinFrequency, Math.Clamp(x / Math.Max(1, ViewSize.X), 0, 1));

    public float Y(float db) => ViewSize.Y * 0.5f * (1 - Math.Clamp(db, -RangeDb - 6, RangeDb + 6) / RangeDb);

    public float Gain(float y) => Math.Clamp((1 - y / Math.Max(1, ViewSize.Y * 0.5f)) * RangeDb, -RangeDb, RangeDb);

    /// <summary>Where band <paramref name="index"/>'s handle is (dp): cuts sit on the 0 dB line.</summary>
    public Vector2 Handle(int index)
    {
        var band = EqEffect.Bands[index];
        var info = EqEffect.Definition;
        var gain = band.GainId is null ? 0 : info.Value(Values, band.GainId);
        return new Vector2(X(info.Value(Values, band.FreqId)), Y(gain));
    }

    /// <summary>The band whose handle is within reach of <paramref name="point"/> (dp), nearest first, or −1.</summary>
    public int HitBand(Vector2 point, float reach = 12)
    {
        var best = -1;
        var bestDistance = reach;
        for (var i = 0; i < EqEffect.Bands.Length; i++)
        {
            var d = Vector2.Distance(Handle(i), point);
            if (d <= bestDistance)
            {
                best = i;
                bestDistance = d;
            }
        }

        return best;
    }

    protected override void OnDraw()
    {
        var w = ViewSize.X;
        var h = ViewSize.Y;
        Box(0, 0, w, h, Rgb(21, 23, 28));

        // Grid: decades and the in-between octaves, every 6 dB.
        var minor = Rgb(38, 41, 49);
        var major = Rgb(52, 56, 66);
        var label = Rgb(107, 116, 136);
        foreach (var f in GridFrequencies)
            Line(X(f), 0, X(f), h, f is 100 or 1000 or 10000 ? major : minor);
        for (var db = -18; db <= 18; db += 6)
            Line(0, Y(db), w, Y(db), db == 0 ? major : minor);
        Text("100", X(100) + 3, h - 4, 10, label);
        Text("1k", X(1000) + 3, h - 4, 10, label);
        Text("10k", X(10000) + 3, h - 4, 10, label);
        Text("+12", 3, Y(12) - 3, 10, label);
        Text("-12", 3, Y(-12) - 3, 10, label);

        // Each enabled band's own curve, faint.
        var info = EqEffect.Definition;
        for (var b = 0; b < EqEffect.Bands.Length; b++)
        {
            var band = EqEffect.Bands[b];
            if (info.Value(Values, band.OnId) < 0.5f)
                continue;
            var color = WithAlpha(BandVectors[b], b == SelectedBand ? 0.55f : 0.25f);
            var previous = 0f;
            for (var i = 0; i < Points; i++)
            {
                var x = w * i / (Points - 1);
                var y = Y((float)EqEffect.BandResponseDb(band, Values, Frequency(x), DisplayRate));
                if (i > 0)
                    Line(w * (i - 1) / (Points - 1), previous, x, y, color, 1);
                previous = y;
            }
        }

        // The sum.
        for (var i = 0; i < Points; i++)
            _curve[i] = Y((float)EqEffect.ResponseDb(Values, Frequency(w * i / (Points - 1)), DisplayRate));
        var curve = Rgb(232, 235, 242);
        for (var i = 1; i < Points; i++)
            Line(w * (i - 1) / (Points - 1), _curve[i - 1], w * i / (Points - 1), _curve[i], curve, 2);

        // Handles: filled when the band is on, a ring when it is off; the selected one larger.
        for (var b = 0; b < EqEffect.Bands.Length; b++)
        {
            var p = Handle(b);
            var on = info.Value(Values, EqEffect.Bands[b].OnId) >= 0.5f;
            var r = (b == SelectedBand ? HandleRadius + 2 : HandleRadius) * PixelScale;
            var at = p * PixelScale;
            DrawCircle(at, r, on ? BandVectors[b] : WithAlpha(BandVectors[b], 0.35f));
            DrawCircle(at, r * 0.45f, Rgb(21, 23, 28));
        }
    }

    private static readonly float[] GridFrequencies = [30, 50, 100, 200, 300, 500, 1000, 2000, 3000, 5000, 10000];

    private static Vector4 ParseColor(string hex) =>
        Rgb(Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));
}
