using System.Numerics;

namespace MainframeEngine.Tests.Audio;

/// <summary>Decibels, attenuation curves, listener-space projection, panning and doppler.</summary>
public sealed class AudioMathTests
{
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(-6.0206f, 0.5f)]
    [InlineData(-20f, 0.1f)]
    [InlineData(6.0206f, 2f)]
    [InlineData(-80f, 0f)]
    [InlineData(-200f, 0f)]
    public void DecibelsConvertToLinearAndBack(float db, float linear)
    {
        Assert.Equal(linear, AudioMath.DbToLinear(db), 3);
        if (linear > 0)
            Assert.Equal(db, AudioMath.LinearToDb(linear), 2);
        else
            Assert.Equal(AudioMath.SilenceDb, AudioMath.LinearToDb(linear));
    }

    [Theory]
    [InlineData(AttenuationModel.Disabled, 15f, 1f)]
    [InlineData(AttenuationModel.Inverse, 2f, 1f)] // within the unit size
    [InlineData(AttenuationModel.Inverse, 4f, 0.5f)] // 2U: −6 dB
    [InlineData(AttenuationModel.Inverse, 8f, 0.25f)]
    [InlineData(AttenuationModel.InverseSquare, 4f, 0.25f)]
    [InlineData(AttenuationModel.InverseSquare, 8f, 0.0625f)]
    [InlineData(AttenuationModel.Exponential, 4f, 0.5f)] // (d/U)^-1
    [InlineData(AttenuationModel.Linear, 2f, 1f)]
    [InlineData(AttenuationModel.Linear, 11f, 0.5f)] // halfway between U = 2 and M = 20
    [InlineData(AttenuationModel.Linear, 19.99f, 0.001f)]
    [InlineData(AttenuationModel.Logarithmic, 2f, 1f)]
    [InlineData(AttenuationModel.Logarithmic, 6.3246f, 0.5f)] // √(U·M): halfway in log space
    public void AttenuationCurvesMatchTheirFormulas(AttenuationModel model, float distance, float expected)
    {
        Assert.Equal(expected, AudioMath.Attenuation(model, distance, unitSize: 2f, maxDistance: 20f), 3);
    }

    [Theory]
    [InlineData(AttenuationModel.Inverse)]
    [InlineData(AttenuationModel.InverseSquare)]
    [InlineData(AttenuationModel.Linear)]
    [InlineData(AttenuationModel.Exponential)]
    [InlineData(AttenuationModel.Logarithmic)]
    [InlineData(AttenuationModel.Disabled)]
    public void EveryModelIsSilentBeyondTheMaxDistanceAndMonotonic(AttenuationModel model)
    {
        Assert.Equal(0f, AudioMath.Attenuation(model, 20.01f, 2f, 20f));
        var previous = 1f;
        for (var d = 0f; d <= 20f; d += 0.25f)
        {
            var gain = AudioMath.Attenuation(model, d, 2f, 20f);
            Assert.InRange(gain, 0f, 1f);
            Assert.True(gain <= previous + 1e-6f, $"{model} rises at {d}");
            previous = gain;
        }
    }

    [Fact]
    public void UnlimitedDistanceNeverCutsAndRangeModelsUseOneHundredUnits()
    {
        Assert.True(AudioMath.Attenuation(AttenuationModel.Inverse, 1e6f, 1f, 0f) > 0f);
        Assert.Equal(0.5f, AudioMath.Attenuation(AttenuationModel.Linear, 50.5f, 1f, 0f), 3); // range = 100 U
    }

    [Fact]
    public void RolloffSteepensTheCurves()
    {
        var gentle = AudioMath.Attenuation(AttenuationModel.Inverse, 10f, 1f, 0f, rolloff: 0.5f);
        var steep = AudioMath.Attenuation(AttenuationModel.Inverse, 10f, 1f, 0f, rolloff: 2f);
        Assert.Equal(1f / 5.5f, gentle, 4);
        Assert.Equal(1f / 19f, steep, 4);
        Assert.Equal(0.01f, AudioMath.Attenuation(AttenuationModel.Exponential, 10f, 1f, 0f, rolloff: 2f), 4);
    }

    [Fact]
    public void CustomCurveInterpolatesOverTheRange()
    {
        float[] curve = [1f, 0.5f, 0f];
        Assert.Equal(1f, AudioMath.Attenuation(AttenuationModel.Custom, 0f, 1f, 10f, customCurve: curve));
        Assert.Equal(0.75f, AudioMath.Attenuation(AttenuationModel.Custom, 2.5f, 1f, 10f, customCurve: curve), 4);
        Assert.Equal(0.5f, AudioMath.Attenuation(AttenuationModel.Custom, 5f, 1f, 10f, customCurve: curve), 4);
        Assert.Equal(0f, AudioMath.Attenuation(AttenuationModel.Custom, 10f, 1f, 10f, customCurve: curve), 4);
        Assert.Equal(1f, AudioMath.Attenuation(AttenuationModel.Custom, 5f, 1f, 10f)); // no curve: no attenuation
    }

    [Fact]
    public void BadInputsNeverProduceNaN()
    {
        Assert.Equal(0f, AudioMath.Attenuation(AttenuationModel.Inverse, float.NaN, 1f, 10f));
        Assert.Equal(0f, AudioMath.Attenuation(AttenuationModel.Linear, float.PositiveInfinity, 1f, 10f));
        Assert.Equal(1f, AudioMath.Attenuation(AttenuationModel.Inverse, -5f, 0f, 0f));
        AudioMath.PanGains(float.NaN, out var l, out var r);
        Assert.False(float.IsNaN(l) || float.IsNaN(r));
    }

    [Fact]
    public void PanLawIsUnityAtTheCentreAndHardPansOneChannel()
    {
        AudioMath.PanGains(0f, out var l, out var r);
        Assert.Equal(1f, l, 4);
        Assert.Equal(1f, r, 4);
        AudioMath.PanGains(1f, out l, out r);
        Assert.Equal(0f, l, 4);
        Assert.Equal(1f, r, 4);
        AudioMath.PanGains(-1f, out l, out r);
        Assert.Equal(1f, l, 4);
        Assert.Equal(0f, r, 4);

        // Monotonic in between, never boosted.
        var previousLeft = 1f;
        for (var p = -1f; p <= 1f; p += 0.05f)
        {
            AudioMath.PanGains(p, out l, out r);
            Assert.InRange(l, 0f, 1f);
            Assert.InRange(r, 0f, 1f);
            Assert.True(l <= previousLeft + 1e-6f);
            previousLeft = l;
        }
    }

    [Fact]
    public void EmittersAreProjectedIntoListenerSpace()
    {
        var identity = Transform3D.Identity; // at the origin looking down −Z
        AudioMath.ProjectToListener(identity, new Vector3(0, 0, -5), 1f, out var distance, out var pan);
        Assert.Equal(5f, distance, 4);
        Assert.Equal(0f, pan, 4); // straight ahead

        AudioMath.ProjectToListener(identity, new Vector3(3, 0, 0), 1f, out _, out pan);
        Assert.Equal(1f, pan, 4); // right
        AudioMath.ProjectToListener(identity, new Vector3(-3, 0, 0), 1f, out _, out pan);
        Assert.Equal(-1f, pan, 4); // left
        AudioMath.ProjectToListener(identity, new Vector3(0, 0, 4), 1f, out _, out pan);
        Assert.Equal(0f, pan, 4); // behind: centred (stereo cannot tell front from back)
        AudioMath.ProjectToListener(identity, new Vector3(2, 0, -2), 1f, out _, out pan);
        Assert.Equal(MathF.Sqrt(0.5f), pan, 4); // 45° right

        // Elevation counts toward distance but does not pan.
        AudioMath.ProjectToListener(identity, new Vector3(0, 10, 0), 1f, out distance, out pan);
        Assert.Equal(10f, distance, 4);
        Assert.Equal(0f, pan, 4);

        // Panning strength scales (and clamps) the pan.
        AudioMath.ProjectToListener(identity, new Vector3(2, 0, -2), 0.5f, out _, out pan);
        Assert.Equal(MathF.Sqrt(0.5f) * 0.5f, pan, 4);
    }

    [Fact]
    public void ListenerOrientationAndPositionAreRespected()
    {
        // Listener at (10, 0, 0) turned 90° left (yaw +90°): it now looks down world −X.
        var listener = new Transform3D(Basis.FromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, MathF.PI / 2)), new Vector3(10, 0, 0));
        AudioMath.ProjectToListener(listener, new Vector3(5, 0, 0), 1f, out var distance, out var pan);
        Assert.Equal(5f, distance, 4);
        Assert.Equal(0f, pan, 3); // in front

        AudioMath.ProjectToListener(listener, new Vector3(10, 0, -5), 1f, out _, out pan);
        Assert.Equal(1f, pan, 3); // world −Z is now on the listener's right

        // Scale on the listener is ignored.
        var scaled = new Transform3D(new Basis(Vector3.UnitX * 3, Vector3.UnitY * 3, Vector3.UnitZ * 3), Vector3.Zero);
        AudioMath.ProjectToListener(scaled, new Vector3(2, 0, -2), 1f, out distance, out pan);
        Assert.Equal(MathF.Sqrt(8f), distance, 4);
        Assert.Equal(MathF.Sqrt(0.5f), pan, 4);
    }

    [Fact]
    public void DopplerRaisesPitchWhenApproachingAndLowersWhenReceding()
    {
        const float c = 343f;
        Assert.Equal(1f, AudioMath.DopplerFactor(Vector3.Zero, Vector3.Zero, new Vector3(0, 0, -10), Vector3.Zero, c));
        // Emitter approaching at 34.3 m/s: c / (c − v) ≈ 1.111.
        Assert.Equal(c / (c - 34.3f), AudioMath.DopplerFactor(Vector3.Zero, Vector3.Zero, new Vector3(0, 0, -10), new Vector3(0, 0, 34.3f), c), 4);
        // Emitter receding.
        Assert.Equal(c / (c + 34.3f), AudioMath.DopplerFactor(Vector3.Zero, Vector3.Zero, new Vector3(0, 0, -10), new Vector3(0, 0, -34.3f), c), 4);
        // Listener moving towards the emitter: (c + v) / c.
        Assert.Equal((c + 34.3f) / c, AudioMath.DopplerFactor(Vector3.Zero, new Vector3(0, 0, -34.3f), new Vector3(0, 0, -10), Vector3.Zero, c), 4);
        // Sideways motion: no shift. Scale 0: off. Supersonic: clamped.
        Assert.Equal(1f, AudioMath.DopplerFactor(Vector3.Zero, Vector3.Zero, new Vector3(0, 0, -10), new Vector3(50, 0, 0), c), 4);
        Assert.Equal(1f, AudioMath.DopplerFactor(Vector3.Zero, Vector3.Zero, new Vector3(0, 0, -10), new Vector3(0, 0, 34.3f), c, scale: 0f));
        Assert.Equal(2f, AudioMath.DopplerFactor(Vector3.Zero, Vector3.Zero, new Vector3(0, 0, -10), new Vector3(0, 0, 1000f), c));
    }

    [Fact]
    public void TwoDimensionalAttenuationAndPanFollowGodot()
    {
        // A 1920×1080 view whose camera looks at (100, 50): the listener is the view centre.
        var screen = new Vector2(1920, 1080);
        var canvas = Transform2D.FromTrs(screen * 0.5f - new Vector2(100, 50), 0, Vector2.One);
        var listener = canvas.AffineInverse().TransformPoint(screen * 0.5f);
        Assert.Equal(new Vector2(100, 50), listener);

        AudioPlayer2D.Spatialize(listener, listener, canvas, screen, 2000f, 1f, 1f, 0.5f, out var gain, out var left, out var right);
        Assert.Equal(1f, gain);
        Assert.Equal(0.5f, left); // linear pan: half each in the centre
        Assert.Equal(0.5f, right);

        // 960 px right = half the width: pan 0.5 × 1 × 0.5 × 0.5 + 0.5 = 0.625.
        AudioPlayer2D.Spatialize(listener + new Vector2(960, 0), listener, canvas, screen, 2000f, 1f, 1f, 0.5f, out gain, out left, out right);
        Assert.Equal(1f - 960f / 2000f, gain, 4);
        Assert.Equal(0.375f, left, 4);
        Assert.Equal(0.625f, right, 4);

        // Far left, attenuation 2: the pan clamps at −1 before the strengths.
        AudioPlayer2D.Spatialize(listener - new Vector2(1980, 0), listener, canvas, screen, 3000f, 2f, 1f, 0.5f, out gain, out left, out right);
        Assert.Equal(MathF.Pow(1f - 1980f / 3000f, 2f), gain, 4);
        Assert.Equal(0.75f, left, 4);
        Assert.Equal(0.25f, right, 4);

        // Zoomed in ×2: the same world offset is twice as far across the screen.
        var zoomed = Transform2D.FromTrs(screen * 0.5f - 2f * new Vector2(100, 50), 0, new Vector2(2));
        AudioPlayer2D.Spatialize(listener + new Vector2(480, 0), listener, zoomed, screen, 2000f, 1f, 1f, 0.5f, out _, out left, out right);
        Assert.Equal(0.625f, right, 4);

        AudioPlayer2D.Spatialize(listener + new Vector2(0, 2500), listener, canvas, screen, 2000f, 1f, 1f, 0.5f, out gain, out left, out right);
        Assert.Equal(0f, gain);
        Assert.Equal(0f, left + right);
    }
}
