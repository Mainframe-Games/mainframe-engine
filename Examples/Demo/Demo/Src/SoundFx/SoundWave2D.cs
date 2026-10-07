using System.Numerics;
using MainframeEngine;

namespace Demo;

/// <summary>The current ZzFX sound's waveform (min/max per column over the sound's length); flashes when it plays.</summary>
public sealed class SoundWave2D : Node2D
{
    public const int Columns = 240;
    public const float Width = 780f;
    public const float Height = 190f;
    public static readonly Vector2 Center = new(170f, 30f);

    private readonly float[] _min = new float[Columns];
    private readonly float[] _max = new float[Columns];
    private float _flash;

    /// <summary>Seconds of the sound shown.</summary>
    public float Seconds { get; private set; }

    /// <summary>Synthesises <paramref name="parameters"/> once (allocates; call on a click, not every frame) and redraws.</summary>
    public void Show(in ZzfxParameters parameters)
    {
        var samples = Zzfx.Generate(parameters with { Randomness = 0 });
        Seconds = samples.Length / (float)Zzfx.SampleRate;
        var peak = 1e-4f;
        foreach (var s in samples)
            peak = MathF.Max(peak, MathF.Abs(s));
        for (var c = 0; c < Columns; c++)
        {
            var from = (int)((long)samples.Length * c / Columns);
            var to = Math.Max(from + 1, (int)((long)samples.Length * (c + 1) / Columns));
            float lo = 0f, hi = 0f;
            for (var i = from; i < to && i < samples.Length; i++)
            {
                lo = MathF.Min(lo, samples[i]);
                hi = MathF.Max(hi, samples[i]);
            }

            _min[c] = lo / peak;
            _max[c] = hi / peak;
        }

        QueueRedraw();
    }

    /// <summary>Brightens the waveform for a moment (the sound just played).</summary>
    public void Flash()
    {
        _flash = 1f;
        QueueRedraw();
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_flash <= 0f)
            return;
        _flash = MathF.Max(0f, _flash - gameTime.DeltaTime * 2.5f);
        QueueRedraw();
    }

    protected override void OnDraw()
    {
        var left = Center.X - Width / 2;
        DrawLine(new Vector2(left, Center.Y), new Vector2(left + Width, Center.Y), new Vector4(0.45f, 0.62f, 1f, 0.25f), 1f);
        var color = Vector4.Lerp(new Vector4(0.98f, 0.75f, 0.2f, 0.9f), new Vector4(1f, 0.97f, 0.85f, 1f), _flash);
        var step = Width / Columns;
        for (var c = 0; c < Columns; c++)
        {
            var x = left + (c + 0.5f) * step;
            var top = Center.Y - MathF.Max(_max[c], 0.005f) * Height;
            var bottom = Center.Y - MathF.Min(_min[c], -0.005f) * Height;
            DrawLine(new Vector2(x, top), new Vector2(x, bottom), color, step * 0.6f);
        }
    }
}
