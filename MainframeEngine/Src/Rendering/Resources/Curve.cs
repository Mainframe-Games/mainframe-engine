namespace MainframeEngine;

/// <summary>
/// Values at offsets in 0..1, linearly interpolated (Godot's <c>Curve</c> with linear tangents; ADR 0187). Clamped outside
/// the points. Offsets are expected in ascending order. Used by <see cref="ParticleProcessMaterial.ScaleCurve"/>.
/// </summary>
[EditorIcon("math-function")]
public sealed class Curve : Resource
{
    [Export] public float[] Offsets { get; set; } = [0f, 1f];

    [Export] public float[] Values { get; set; } = [1f, 1f];

    /// <summary>The value at <paramref name="offset"/> (no allocation).</summary>
    public float Sample(float offset)
    {
        var count = Math.Min(Offsets.Length, Values.Length);
        if (count == 0)
            return 1f;
        if (offset <= Offsets[0])
            return Values[0];
        for (var i = 1; i < count; i++)
        {
            if (offset > Offsets[i])
                continue;
            var span = Offsets[i] - Offsets[i - 1];
            var t = span > 0f ? (offset - Offsets[i - 1]) / span : 1f;
            return Values[i - 1] + (Values[i] - Values[i - 1]) * t;
        }

        return Values[count - 1];
    }
}
