using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// CPU mirrors of the post tonemap pass's curves (<c>Post/TonemapPost.vk.frag</c>, ADR 0124 and ADR 0168), for tests and
/// tools: linear HDR (already × exposure) in, linear display values out, before the sRGB encode (which clamps to 0–1).
/// </summary>
public static class TonemapCurves
{
    // Linear sRGB → linear Rec.2020 with Blender's AgX inset, and the inverse outset with Rec.2020 → sRGB, as columns
    // (GLSL order): result = c.X · column0 + c.Y · column1 + c.Z · column2.
    private static readonly Vector3[] AgxInset =
    [
        new(0.54490813676363087053f, 0.14044005884001287035f, 0.088827411851915368603f),
        new(0.37377945959812267119f, 0.75410959864013760045f, 0.17887712465043811023f),
        new(0.081384976686407536266f, 0.10543358536857773485f, 0.73224999956948382528f),
    ];

    private static readonly Vector3[] AgxOutset =
    [
        new(1.9645509602733325934f, -0.29932243390911083839f, -0.16436833806080403409f),
        new(-0.85585845117807513559f, 1.3264510741502356555f, -0.23822464068860595117f),
        new(-0.10886710826831608324f, -0.027084020983874825605f, 1.402665347143271889f),
    ];

    /// <summary>The curve <paramref name="settings"/> selects applied to <paramref name="color"/> (already × exposure).</summary>
    public static Vector3 Apply(in PostProcessSettings settings, Vector3 color) => settings.Tonemapper switch
    {
        Tonemapper.GodotAces => GodotAces(color, settings.GodotAcesWhiteTonemapped),
        Tonemapper.Linear => Vector3.Max(color, Vector3.Zero),
        Tonemapper.Reinhard => Reinhard(color, settings.GlowWhite),
        Tonemapper.Filmic => Filmic(Vector3.Max(color, Vector3.Zero)) / settings.FilmicWhiteTonemapped,
        Tonemapper.Agx => Agx(color),
        _ => ColorSpace.AcesFitted(color),
    };

    /// <summary>Godot 4.7's ACES: the engine's fit at input × 1.8, divided by the curve at 1.8 · white (unclamped).</summary>
    public static Vector3 GodotAces(Vector3 color, float whiteTonemapped)
    {
        color = Vector3.Max(color, Vector3.Zero) * 1.8f;
        var input = new Vector3(
            0.59719f * color.X + 0.35458f * color.Y + 0.04823f * color.Z,
            0.07600f * color.X + 0.90834f * color.Y + 0.01566f * color.Z,
            0.02840f * color.X + 0.13383f * color.Y + 0.83777f * color.Z);
        var a = input * (input + new Vector3(0.0245786f)) - new Vector3(0.000090537f);
        var b = input * (0.983729f * input + new Vector3(0.4329510f)) + new Vector3(0.238081f);
        var v = a / b;
        var output = new Vector3(
            1.60475f * v.X - 0.53108f * v.Y - 0.07367f * v.Z,
            -0.10208f * v.X + 1.10813f * v.Y - 0.00605f * v.Z,
            -0.00327f * v.X - 0.07276f * v.Y + 1.07602f * v.Z);
        return output / whiteTonemapped;
    }

    /// <summary>Godot 4.4's extended Reinhard: <c>c (1 + c / white²) / (1 + c)</c>, 1 at <paramref name="white"/>.</summary>
    public static Vector3 Reinhard(Vector3 color, float white)
    {
        color = Vector3.Max(color, Vector3.Zero);
        var w2 = white * white;
        var w2c = w2 * color;
        return (w2c + color * color) / (w2c + new Vector3(w2));
    }

    /// <summary>Godot 4.4's filmic curve (Hable's, 2× exposure bias), before the division by its value at white.</summary>
    public static Vector3 Filmic(Vector3 color)
    {
        const float a = 0.22f * 4f, b = 0.30f * 2f, c = 0.10f, d = 0.20f, e = 0.01f, f = 0.30f;
        return (color * (a * color + new Vector3(c * b)) + new Vector3(d * e)) / (color * (a * color + new Vector3(b)) + new Vector3(d * f)) - new Vector3(e / f);
    }

    /// <summary>Godot 4.4's AgX (Blender's look): may return components outside 0–1, which the encode clamps.</summary>
    public static Vector3 Agx(Vector3 color)
    {
        const float minEv = -12.4739311883324f, maxEv = 4.02606881166759f;
        color = Vector3.Max(color, new Vector3(2e-10f));
        color = color.X * AgxInset[0] + color.Y * AgxInset[1] + color.Z * AgxInset[2];
        color = new Vector3(MathF.Log2(color.X), MathF.Log2(color.Y), MathF.Log2(color.Z));
        color = (Vector3.Clamp(color, new Vector3(minEv), new Vector3(maxEv)) - new Vector3(minEv)) / (maxEv - minEv);
        color = Contrast(color);
        color = new Vector3(Pow(color.X), Pow(color.Y), Pow(color.Z));
        return color.X * AgxOutset[0] + color.Y * AgxOutset[1] + color.Z * AgxOutset[2];

        static float Pow(float x) => MathF.Pow(MathF.Max(x, 0f), 2.4f);

        static Vector3 Contrast(Vector3 x)
        {
            var x2 = x * x;
            var x4 = x2 * x2;
            return 0.021f * x + 4.0111f * x2 - 25.682f * x2 * x + 70.359f * x4 - 74.778f * x4 * x + 27.069f * x4 * x2;
        }
    }
}
