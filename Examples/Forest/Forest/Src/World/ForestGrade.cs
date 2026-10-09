using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The Forest's colour grade (ADR 0168; refitted in G8e.7, ADR 0175): a soft forest-morning look, generated here (own work,
/// CC0) and committed as <see cref="LutPath"/> (a 33³ <c>.cube</c>, written by <c>--write-scenes</c>), plus the film lens
/// (vignette, grain) and the photo shots' depth of field. The grade works on display values, after the engine's ACES
/// tonemap (AgX was compared in G8e.7 and read flat and washed in the shade), and its numbers are fitted to ungraded
/// renders of the seven reference shots by <c>Tools/grade_from_shots.py</c> (<see cref="ForestGradeFit"/>):
/// <list type="number">
/// <item>a faintly warm white balance (the low morning sun) that spares the sky's blue;</item>
/// <item>a tone curve through the shots' 5th, 50th and 95th luminance percentiles to the fit's targets: shade a little
/// deeper, mid-tones a little brighter, a gentle shoulder, lifted blacks;</item>
/// <item>split toning that moves the shade's cast towards a faintly cool green and the light's towards a warm low sun
/// (halfway, by the measured casts);</item>
/// <item>foliage greens a little less saturated and pushed towards olive (natural, not a game's emerald);</item>
/// <item>saturation down a little in the highlights and the deepest shade (film-like).</item>
/// </list>
/// </summary>
public static class ForestGrade
{
    /// <summary>The LUT, relative to the project folder.</summary>
    public const string LutPath = "Content/Grading/forest-morning.cube";

    public const int LutSize = 33;

    /// <summary>How much of the LUT the environment applies.</summary>
    public const float Strength = 1f;

    /// <summary>The film lens every Forest camera sees: a subtle vignette and a touch of grain, no aberration.</summary>
    public const float VignetteIntensity = 0.2f;
    public const float FilmGrainIntensity = 0.015f;

    private static readonly Vector3 Luma = new(0.2126f, 0.7152f, 0.0722f);

    /// <summary>The Forest's lens (on <see cref="WorldEnvironment.CameraAttributes"/>).</summary>
    public static CameraAttributesPractical CreateLens() => new()
    {
        VignetteIntensity = VignetteIntensity,
        VignetteRoundness = 1f,
        FilmGrainIntensity = FilmGrainIntensity,
        FilmGrainSize = 1.5f,
    };

    // The tone curve's control points (display in → display out), fitted to the shots (ForestGradeFit).
    private static readonly float[] CurveX = ForestGradeFit.CurveX;
    private static readonly float[] CurveY = ForestGradeFit.CurveY;

    /// <summary>The grade of one display colour (0–1, sRGB-encoded).</summary>
    public static Vector3 Grade(Vector3 c)
    {
        // 1. A faintly warm white balance (the low morning sun), sparing the sky's blue.
        var blueness = Math.Clamp((c.Z - MathF.Max(c.X, c.Y)) * 6f, 0f, 1f);
        c *= Vector3.Lerp(new Vector3(1.015f, 1f, 0.975f), Vector3.One, blueness);

        // 2. The fitted tone curve, per channel.
        c = new Vector3(Curve(c.X), Curve(c.Y), Curve(c.Z));

        // 3. Split toning by luminance: the shade's and the light's measured casts moved halfway to their targets.
        var l = Vector3.Dot(c, Luma);
        var shadows = 1f - SmoothStep(0f, 0.35f, l);
        var highlights = SmoothStep(0.4f, 0.95f, l);
        c += shadows * ForestGradeFit.ShadeTint;
        c += highlights * (1f - blueness) * ForestGradeFit.LightTint;

        // 4. Foliage: where green leads, a little less saturated and towards olive.
        l = Vector3.Dot(c, Luma);
        var greenness = Math.Clamp((c.Y - MathF.Max(c.X, c.Z)) * 5f, 0f, 1f);
        c += greenness * ForestGradeFit.FoliageWarmth * new Vector3(0.035f, 0f, -0.02f);
        c = new Vector3(l) + (c - new Vector3(l)) * float.Lerp(1f, ForestGradeFit.FoliageSaturation, greenness);

        // 5. Saturation: a touch up in the mid-tones, down towards white (−8 %) and in the deepest shade (−6 %).
        l = Vector3.Dot(c, Luma);
        var saturation = 1.03f - 0.11f * SmoothStep(0.65f, 1f, l) - 0.09f * (1f - SmoothStep(0f, 0.15f, l));
        c = new Vector3(l) + (c - new Vector3(l)) * saturation;
        return Vector3.Clamp(c, Vector3.Zero, Vector3.One);
    }

    /// <summary>The tone curve: a monotone cubic (Fritsch–Carlson) through <see cref="CurveX"/>/<see cref="CurveY"/>.</summary>
    public static float Curve(float x)
    {
        x = Math.Clamp(x, 0f, 1f);
        var n = CurveX.Length;
        var i = 0;
        while (i < n - 2 && x > CurveX[i + 1])
            i++;
        var h = CurveX[i + 1] - CurveX[i];
        var t = (x - CurveX[i]) / h;
        var m0 = Tangent(i) * h;
        var m1 = Tangent(i + 1) * h;
        var t2 = t * t;
        var t3 = t2 * t;
        return (2f * t3 - 3f * t2 + 1f) * CurveY[i] + (t3 - 2f * t2 + t) * m0 + (-2f * t3 + 3f * t2) * CurveY[i + 1] + (t3 - t2) * m1;

        static float Slope(int k) => (CurveY[k + 1] - CurveY[k]) / (CurveX[k + 1] - CurveX[k]);

        static float Tangent(int k)
        {
            if (k == 0)
                return Slope(0);
            if (k == CurveX.Length - 1)
                return Slope(k - 1);
            var a = Slope(k - 1);
            var b = Slope(k);
            return a * b <= 0f ? 0f : 2f / (1f / a + 1f / b); // harmonic mean: monotone
        }
    }

    /// <summary>The grade as a 33³ table.</summary>
    public static CubeLut CreateLut() => CubeLut.FromFunction(LutSize, Grade, "Forest morning (Mainframe Engine, CC0)");

    /// <summary>Writes <see cref="LutPath"/> under <paramref name="projectDirectory"/>.</summary>
    public static string WriteLut(string projectDirectory)
    {
        var path = Path.Combine(projectDirectory, LutPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CreateLut().Save(path);
        return path;
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }
}
