using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0168: the <c>.cube</c> parser, <see cref="Texture3D"/>, the tonemap curves and the cinematic settings.</summary>
public sealed class CinematicPostTests
{
    // ── .cube parsing ────────────────────────────────────────────────────────

    [Fact]
    public void ACubeFileParsesWithTitleCommentsDomainAndRedFastest()
    {
        const string text = """
            # A comment
            TITLE "Two"
            LUT_3D_SIZE 2

            DOMAIN_MIN 0 0 0
            DOMAIN_MAX 1 1 1
            0 0 0
            1 0 0
            0 1 0
            1 1 0
            0 0 1
            1 0 1
            0 1 1
            1 1 1
            """;
        var lut = CubeLut.Parse(text);
        Assert.Equal("Two", lut.Title);
        Assert.Equal(2, lut.Size);
        Assert.False(lut.Is1D);
        Assert.True(lut.HasUnitDomain);
        Assert.Equal(new Vector3(1, 0, 0), lut[1, 0, 0]); // red changes fastest
        Assert.Equal(new Vector3(0, 1, 0), lut[0, 1, 0]);
        Assert.Equal(new Vector3(0, 0, 1), lut[0, 0, 1]);
        Assert.Equal(new Vector3(0.25f, 0.5f, 0.75f), lut.Sample(new Vector3(0.25f, 0.5f, 0.75f))); // an identity is exact
    }

    [Fact]
    public void ResolveStyleFilesWithCrlfTabsAndVendorKeywordsParse()
    {
        var text = "LUT_3D_SIZE 2\r\nLUT_IN_VIDEO_RANGE\r\nLUT_3D_INPUT_RANGE 0.0 1.0\r\n" +
                   string.Concat(Enumerable.Range(0, 8).Select(i => $"{i & 1}\t{(i >> 1) & 1}  {(i >> 2) & 1}\r\n"));
        var lut = CubeLut.Parse(text);
        Assert.Equal(8, lut.Entries.Length);
        Assert.Equal(Vector3.One, lut[1, 1, 1]);
    }

    [Theory]
    [InlineData("0 0 0\n", "before LUT_3D_SIZE")]
    [InlineData("LUT_3D_SIZE 1\n", "outside 2")]
    [InlineData("LUT_3D_SIZE 300\n", "outside 2")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\n", "expected 8 table rows")]
    [InlineData("LUT_3D_SIZE 2\n0 0\n", "expected three numbers")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0 0\n", "more than three numbers")]
    [InlineData("LUT_3D_SIZE 2\n0 zero 0\n", "is not a number")]
    [InlineData("LUT_3D_SIZE 2\n0 NaN 0\n", "is not a number")]
    [InlineData("LUT_3D_SIZE 2\nLUT_1D_SIZE 4\n0 0 0\n", "both")]
    [InlineData("LUT_3D_SIZE 2\n0 0 0\nTITLE \"late\"\n", "keyword after the table data")]
    [InlineData("TITLE \"empty\"\n", "no LUT_3D_SIZE")]
    [InlineData("LUT_3D_SIZE 2\nDOMAIN_MIN 1 1 1\nDOMAIN_MAX 0 0 0\n0 0 0\n1 0 0\n0 1 0\n1 1 0\n0 0 1\n1 0 1\n0 1 1\n1 1 1\n", "DOMAIN_MAX")]
    public void InvalidCubeFilesAreRejectedWithTheReason(string text, string reason)
    {
        var e = Assert.Throws<InvalidDataException>(() => CubeLut.Parse(new StringReader(text), "bad.cube"));
        Assert.Contains(reason, e.Message, StringComparison.Ordinal);
        Assert.StartsWith("bad.cube", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WritingAndParsingRoundTrips()
    {
        var lut = CubeLut.FromFunction(5, static c => new Vector3(c.Z, c.X * 0.5f, MathF.Sqrt(c.Y)), "round trip");
        var writer = new StringWriter();
        lut.Write(writer);
        Assert.DoesNotContain('\r', writer.ToString());
        var copy = CubeLut.Parse(writer.ToString());
        Assert.Equal("round trip", copy.Title);
        Assert.Equal(lut.Size, copy.Size);
        for (var i = 0; i < lut.Entries.Length; i++)
            Assert.True(Vector3.Distance(lut.Entries[i], copy.Entries[i]) < 1e-5f);
    }

    [Fact]
    public void SamplingIsTrilinearAndClampsOutsideTheDomain()
    {
        var lut = CubeLut.FromFunction(3, static c => c * c);
        // Between grid points the table interpolates linearly: halfway between 0.5 (0.25) and 1 (1) is 0.625.
        Assert.Equal(0.625f, lut.Sample(new Vector3(0.75f, 0f, 0f)).X, 1e-6f);
        Assert.Equal(Vector3.One, lut.Sample(new Vector3(2f)));
        Assert.Equal(Vector3.Zero, lut.Sample(new Vector3(-1f)));
    }

    [Fact]
    public void OneDimensionalTablesAndOtherDomainsAreResampledInto3D()
    {
        var oneD = CubeLut.Parse("LUT_1D_SIZE 3\n0 0 0\n0.25 0.5 1\n1 1 1\n");
        Assert.True(oneD.Is1D);
        Assert.Equal(new Vector3(0.125f, 0.25f, 0.5f), oneD.Sample(new Vector3(0.25f)));
        var volume = oneD.ToTexture3D(5);
        Assert.Equal((5, 5, 5), (volume.Width, volume.Height, volume.Depth));
        var mid = volume.GetTexel(2, 2, 2); // input 0.5 on every channel
        Assert.Equal(0.25f, mid.X, 3e-3f);
        Assert.Equal(0.5f, mid.Y, 3e-3f);
        Assert.Equal(1f, mid.Z, 3e-3f);

        var wide = new CubeLut(2, false, [Vector3.Zero, new(1, 0, 0), new(0, 1, 0), new(1, 1, 0), new(0, 0, 1), new(1, 0, 1), new(0, 1, 1), Vector3.One],
            Vector3.Zero, new Vector3(2f));
        var resampled = wide.ToTexture3D(3).GetTexel(2, 2, 2); // texture coordinate 1 = input 2 (the domain's top)
        Assert.Equal(1f, resampled.X, 1e-3f);
        Assert.Equal(0.5f, wide.ToTexture3D(3).GetTexel(1, 0, 0).X, 1e-3f); // texture 0.5 = input 1 = half the domain
    }

    [Fact]
    public void AUnitDomainTableBecomesAHalfFloatVolumeEntryForEntry()
    {
        var lut = CubeLut.FromFunction(4, static c => new Vector3(c.Z, c.X, c.Y));
        var volume = lut.ToTexture3D();
        Assert.Equal(Texture3DFormat.Rgba16F, volume.Format);
        Assert.Equal(4 * 4 * 4 * 8, volume.Texels.Length);
        Assert.Equal(new Vector4(1f / 3f, 1f, 2f / 3f, 1f), volume.GetTexel(3, 2, 1), new Vector4Comparer(1e-3f));
    }

    [Fact]
    public void TheCubeImporterIsRegistered()
    {
        Assert.IsType<CubeLutImporter>(AssetImporters.Find("grade.cube"));
        var path = Path.Combine(Path.GetTempPath(), $"mf-cube-{Guid.NewGuid():N}.cube");
        try
        {
            CubeLut.Identity(3).Save(path);
            var volume = Assert.IsType<Texture3D>(new CubeLutImporter().Import(path, "Content/grade.cube", null));
            Assert.Equal(3, volume.Depth);
        }
        finally
        {
            File.Delete(path);
        }
    }

    // ── Texture3D ────────────────────────────────────────────────────────────

    [Fact]
    public void ATexture3DChecksItsSizeAndBumpsItsVersion()
    {
        Assert.Throws<ArgumentException>(() => new Texture3D(2, 2, 2, Texture3DFormat.Rgba8, new byte[31]));
        var volume = new Texture3D(2, 2, 2, Texture3DFormat.Rgba8, new byte[32]);
        var version = volume.Version;
        volume.SetTexels(Enumerable.Repeat((byte)255, 32).ToArray());
        Assert.NotEqual(version, volume.Version);
        Assert.Equal(Vector4.One, volume.GetTexel(1, 1, 1));
        version = volume.Version;
        volume.Filter = false;
        Assert.NotEqual(version, volume.Version);
        Assert.True(new Texture3D().IsEmpty);
    }

    // ── Tonemap curves ───────────────────────────────────────────────────────

    [Fact]
    public void NewTonemappersKeepTheOldOrdinalsAndFollowGodots()
    {
        Assert.Equal([0, 1, 2, 3, 4, 5], Enum.GetValues<Tonemapper>().Select(t => (int)t));
        Assert.Equal(Tonemapper.Agx, Enum.GetValues<Tonemapper>()[^1]);
    }

    [Theory]
    [InlineData(Tonemapper.Engine)]
    [InlineData(Tonemapper.GodotAces)]
    [InlineData(Tonemapper.Linear)]
    [InlineData(Tonemapper.Reinhard)]
    [InlineData(Tonemapper.Filmic)]
    [InlineData(Tonemapper.Agx)]
    public void EveryCurveKeepsGreyNeutralBlackBlackAndIsMonotonic(Tonemapper tonemapper)
    {
        var settings = PostProcessSettings.Default with { Tonemapper = tonemapper };
        Assert.True(TonemapCurves.Apply(settings, Vector3.Zero).Length() < 2e-3f, "black is not black");
        var last = -1f;
        for (var ev = -8f; ev <= 6f; ev += 0.25f)
        {
            var grey = TonemapCurves.Apply(settings, new Vector3(0.18f * MathF.Pow(2f, ev)));
            Assert.True(MathF.Abs(grey.X - grey.Y) < 2e-3f && MathF.Abs(grey.Y - grey.Z) < 2e-3f, $"grey is tinted at {ev} EV: {grey}");
            Assert.True(grey.Y >= last - 1e-5f, $"not monotonic at {ev} EV");
            last = grey.Y;
        }
    }

    [Fact]
    public void ReinhardAndFilmicReachOneAtWhite()
    {
        foreach (var white in new[] { 1f, 4f, 11f })
        {
            var reinhard = PostProcessSettings.Default with { Tonemapper = Tonemapper.Reinhard, TonemapWhite = white };
            var filmic = PostProcessSettings.Default with { Tonemapper = Tonemapper.Filmic, TonemapWhite = white };
            Assert.Equal(1f, TonemapCurves.Apply(reinhard, new Vector3(white)).X, 1e-5f);
            Assert.Equal(1f, TonemapCurves.Apply(filmic, new Vector3(white)).X, 1e-5f);
        }
    }

    [Fact]
    public void AgxDesaturatesBrightSaturatedLightTowardsWhite()
    {
        // A saturated yellow (sun through leaves) brightened stop by stop: AgX keeps its hue in the mid-tones, then
        // narrows the channels' spread and ends at white.
        var agx = PostProcessSettings.Default with { Tonemapper = Tonemapper.Agx };
        var spreads = new List<float>();
        for (var ev = 0; ev <= 7; ev++)
        {
            var c = Vector3.Clamp(TonemapCurves.Apply(agx, new Vector3(1f, 0.6f, 0.1f) * MathF.Pow(2f, ev)), Vector3.Zero, Vector3.One);
            Assert.True(c.X >= c.Y && c.Y >= c.Z, $"the hue flipped at {ev} EV: {c}");
            spreads.Add(Spread(c));
        }

        Assert.True(spreads[0] > 0.3f, $"the mid-tone is not saturated ({spreads[0]})");
        for (var i = 2; i < spreads.Count; i++)
            Assert.True(spreads[i] <= spreads[i - 1] + 1e-4f, $"the spread grew at {i} EV");
        Assert.True(spreads[^1] < 0.01f, $"bright light did not reach white ({spreads[^1]})");
        // Middle grey lands near the middle of the display range (Blender's AgX base: 0.18 → about 0.18 linear display).
        Assert.InRange(TonemapCurves.Apply(agx, new Vector3(0.18f)).X, 0.12f, 0.3f);

        static float Spread(Vector3 c) => MathF.Max(c.X, MathF.Max(c.Y, c.Z)) - MathF.Min(c.X, MathF.Min(c.Y, c.Z));
    }

    [Fact]
    public void ExposureAndWhiteFollowTheTonemapper()
    {
        var s = PostProcessSettings.Default with { TonemapExposure = 2f, TonemapWhite = 6f };
        Assert.Equal(1.3f, s.ExposureFor(1.3f)); // the engine curve keeps the project exposure
        Assert.Equal(2f, (s with { Tonemapper = Tonemapper.Agx }).ExposureFor(1.3f));
        Assert.Equal(6f, (s with { Tonemapper = Tonemapper.Reinhard }).GlowWhite);
        Assert.Equal(6f, (s with { Tonemapper = Tonemapper.Filmic }).GlowWhite);
        Assert.Equal(1f, (s with { Tonemapper = Tonemapper.Linear }).GlowWhite);
        Assert.Equal(1f, (s with { Tonemapper = Tonemapper.Agx }).GlowWhite);
    }

    // ── Settings, enable rules, serialization ────────────────────────────────

    [Fact]
    public void EverythingIsOffByDefaultWithGodotsDefaults()
    {
        var s = PostProcessSettings.Default;
        Assert.Equal(GlowQuality.Standard, s.GlowQuality);
        Assert.False(s.AdjustmentEnabled);
        Assert.Equal((1f, 1f, 1f), (s.AdjustmentBrightness, s.AdjustmentContrast, s.AdjustmentSaturation));
        Assert.Null(s.AdjustmentColorCorrection);
        Assert.Equal(1f, s.AdjustmentColorCorrectionStrength);
        Assert.False(s.DofBlurFarEnabled || s.DofBlurNearEnabled);
        Assert.Equal((10f, 5f, 2f, 1f, 0.1f), (s.DofBlurFarDistance, s.DofBlurFarTransition, s.DofBlurNearDistance, s.DofBlurNearTransition, s.DofBlurAmount));
        Assert.Equal((0f, 1f, 0f, 1.5f, 0f), (s.VignetteIntensity, s.VignetteRoundness, s.FilmGrainIntensity, s.FilmGrainSize, s.ChromaticAberrationIntensity));
        Assert.False(s.DofEnabled);
        Assert.False(s.ColorGradeEnabled);
        Assert.Equal(s, new CameraAttributesPractical().ApplyTo(s)); // a default lens changes nothing: the goldens hold
    }

    [Fact]
    public void TheCircleOfConfusionRampsOverEachTransition()
    {
        var s = PostProcessSettings.Default with { DofBlurFarEnabled = true, DofBlurNearEnabled = true };
        Assert.Equal(0f, s.DofBlur(5f)); // in focus between 2 and 10 m
        Assert.Equal(0.5f, s.DofBlur(12.5f), 1e-6f);
        Assert.Equal(1f, s.DofBlur(100f));
        Assert.Equal(0.5f, s.DofBlur(1.5f), 1e-6f);
        Assert.Equal(1f, s.DofBlur(0.5f));
        Assert.Equal(1f, (s with { DofBlurFarTransition = 0f }).DofBlur(10.01f)); // a hard edge
        Assert.Equal(6.4f, s.DofMaxRadius(1080f), 1e-4f);
        Assert.Equal(12.8f, s.DofMaxRadius(2160f), 1e-4f);
        Assert.Equal(16, DepthOfFieldEffect.Taps(DepthOfFieldQuality.Standard));
        Assert.Equal(32, DepthOfFieldEffect.Taps(DepthOfFieldQuality.High));
    }

    [Fact]
    public void TheNewEffectsRunInTheirStagesWhenTheirSettingsAskForThem()
    {
        var dof = new DepthOfFieldEffect();
        var grade = new ColorGradeEffect();
        var fxaa = new FxaaEffect();
        var stack = new PostProcessStack();
        foreach (PostEffect effect in new PostEffect[] { grade, fxaa, dof })
            stack.Add(effect);
        Assert.Equal(new PostEffect[] { dof, fxaa, grade }, stack.Effects); // DoF before tonemap; the grade after FXAA
        Assert.Equal(PostStage.BeforeTonemap, dof.Stage);
        Assert.Equal(PostStage.AfterTonemap, grade.Stage);
        Assert.Equal(PostEffectNeeds.None, dof.Needs); // the scene pass's own depth is enough

        Settings(s => s with { DofBlurFarEnabled = true }, dof, true);
        Settings(s => s with { DofBlurFarEnabled = true, DofBlurAmount = 0f }, dof, false);
        Settings(s => s with { AdjustmentEnabled = true }, grade, false); // nothing to do
        Settings(s => s with { AdjustmentEnabled = true, AdjustmentContrast = 1.1f }, grade, true);
        Settings(s => s with { AdjustmentContrast = 1.1f }, grade, false); // Godot: adjustments only when enabled
        Settings(s => s with { AdjustmentEnabled = true, AdjustmentColorCorrection = CubeLut.Identity(2).ToTexture3D() }, grade, true);
        Settings(s => s with { AdjustmentEnabled = true, AdjustmentColorCorrection = new Texture3D() }, grade, false); // empty
        Settings(s => s with { VignetteIntensity = 0.2f }, grade, true);
        Settings(s => s with { FilmGrainIntensity = 0.01f }, grade, true);
        Settings(s => s with { ChromaticAberrationIntensity = 1f }, grade, true);

        static void Settings(Func<PostProcessSettings, PostProcessSettings> change, PostEffect effect, bool enabled)
        {
            var settings = new PostEffectSettings(change(PostProcessSettings.Default), AntiAliasing.None, RenderDebugView.None);
            Assert.Equal(enabled, effect.IsEnabled(settings));
        }
    }

    [Fact]
    public void HighQualityGlowCompositesLevelZeroAlone()
    {
        Span<float> w = stackalloc float[GlowEffect.LevelCount];
        w.Fill(0.5f);
        GlowEffect.CombinedWeights(w);
        Assert.Equal([1f, 0f, 0f, 0f, 0f, 0f, 0f], w.ToArray());
    }

    [Fact]
    public void TheCamerasLensReplacesTheEnvironments()
    {
        var environment = new WorldEnvironment { CameraAttributes = new CameraAttributesPractical { VignetteIntensity = 0.1f } };
        Assert.Equal(0.1f, environment.PostProcess.VignetteIntensity);
        var lens = new CameraAttributesPractical { DofBlurFarEnabled = true, FilmGrainIntensity = 0.02f };
        Assert.True(lens.ApplyTo(environment.PostProcess).DofEnabled);
        Assert.Equal(0f, lens.ApplyTo(environment.PostProcess).VignetteIntensity); // the whole lens, not a merge
        Assert.Equal(0.02f, lens.ApplyTo(environment.PostProcess).FilmGrainIntensity);
    }

    [Fact]
    public void CinematicExportsRoundTripThroughScenes()
    {
        var root = new Node3D { Name = "Root" };
        var environment = new WorldEnvironment
        {
            Name = "Env",
            Tonemapper = Tonemapper.Agx,
            GlowQuality = GlowQuality.High,
            AdjustmentEnabled = true,
            AdjustmentBrightness = 1.05f,
            AdjustmentContrast = 1.1f,
            AdjustmentSaturation = 0.9f,
            AdjustmentColorCorrectionStrength = 0.7f,
            CameraAttributes = new CameraAttributesPractical { DofBlurFarEnabled = true, DofBlurFarDistance = 30f, DofQuality = DepthOfFieldQuality.High, VignetteIntensity = 0.2f, FilmGrainIntensity = 0.015f },
        };
        root.AddChild(environment);
        environment.Owner = root;
        var camera = new Camera3D { Name = "Camera", Attributes = new CameraAttributesPractical { ChromaticAberrationIntensity = 1.5f } };
        root.AddChild(camera);
        camera.Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate();
        var env = (WorldEnvironment)copy.GetChild(0);
        Assert.Equal(environment.PostProcess, env.PostProcess);
        Assert.Equal(30f, env.CameraAttributes!.DofBlurFarDistance);
        Assert.Equal(1.5f, ((Camera3D)copy.GetChild(1)).Attributes!.ChromaticAberrationIntensity);
    }

    private sealed class Vector4Comparer(float tolerance) : IEqualityComparer<Vector4>
    {
        public bool Equals(Vector4 a, Vector4 b) => Vector4.Distance(a, b) <= tolerance;
        public int GetHashCode(Vector4 v) => 0;
    }
}
