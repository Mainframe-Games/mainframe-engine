using System.Numerics;
using MainframeEngine;

namespace Forest;

/// <summary>
/// The Forest's colour grade (ADR 0168): a warm, soft forest-morning look, generated here (own work, CC0) and committed as
/// <see cref="LutPath"/> (a 33³ <c>.cube</c>, written by <c>--write-scenes</c>), plus the film lens (vignette, grain) and
/// the photo shots' depth of field. The grade works on display values, after the tonemap:
/// <list type="number">
/// <item>a warm white balance (the low morning sun) that spares the sky's blue;</item>
/// <item>a soft tone curve: blacks lifted, the shade's mid-tones a little brighter and steeper, a gentle shoulder, so
/// nothing crushes or clips hard;</item>
/// <item>split toning: faintly cool shade, golden light (again not in the blue sky);</item>
/// <item>foliage greens pulled a little towards olive and yellow (sunlit leaves read warm, not minty);</item>
/// <item>saturation up in the mid-tones, down in the highlights and the deepest shade (film-like).</item>
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

    // The tone curve's control points (display in → display out): blacks lifted to 0.012, mid-tones a little brighter and
    // steeper (slope ≈ 1.1 between 0.15 and 0.5, where the forest's shade sits), a soft shoulder ending at 0.975.
    private static readonly float[] CurveX = [0f, 0.05f, 0.15f, 0.3f, 0.5f, 0.75f, 0.9f, 1f];
    private static readonly float[] CurveY = [0.012f, 0.052f, 0.148f, 0.31f, 0.535f, 0.78f, 0.91f, 0.975f];

    /// <summary>The grade of one display colour (0–1, sRGB-encoded).</summary>
    public static Vector3 Grade(Vector3 c)
    {
        // 1. Warm white balance (the low morning sun), sparing the sky's blue.
        var blueness = Math.Clamp((c.Z - MathF.Max(c.X, c.Y)) * 6f, 0f, 1f);
        c *= Vector3.Lerp(new Vector3(1.025f, 1f, 0.96f), Vector3.One, blueness);

        // 2. The tone curve, per channel (a film-like saturation lift in the mid-tones comes with it).
        c = new Vector3(Curve(c.X), Curve(c.Y), Curve(c.Z));

        // 3. Split toning by luminance: faintly cool shade, golden light.
        var l = Vector3.Dot(c, Luma);
        var shadows = 1f - SmoothStep(0f, 0.35f, l);
        var highlights = SmoothStep(0.35f, 0.95f, l);
        c += shadows * new Vector3(-0.006f, 0.002f, 0.010f);
        c += highlights * (1f - blueness) * new Vector3(0.028f, 0.012f, -0.026f);

        // 4. Foliage: where green leads, a little red in and blue out (towards olive and yellow).
        var greenness = Math.Clamp((c.Y - MathF.Max(c.X, c.Z)) * 4f, 0f, 1f);
        c += greenness * new Vector3(0.03f, 0f, -0.02f);

        // 5. Saturation: +12 % in the mid-tones, less towards white (−8 %) and in the deepest shade (−4 %).
        l = Vector3.Dot(c, Luma);
        var saturation = 1.12f - 0.2f * SmoothStep(0.65f, 1f, l) - 0.08f * (1f - SmoothStep(0f, 0.15f, l));
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
