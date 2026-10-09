using System.Numerics;
using System.Text;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0154: the physical sky's settings and atmosphere model, auto exposure and the anti-aliasing setting.</summary>
public sealed class SkyAndPostSettingsTests
{
    private static readonly AtmosphereParameters Earth = new PhysicalSkySettings().ToAtmosphere();

    [Fact]
    public void PhysicalSkyDefaultsAreGodotsAndEarths()
    {
        var s = new PhysicalSkySettings();
        Assert.Equal(2f, s.RayleighCoefficient);
        Assert.Equal(new Vector3(0.3f, 0.405f, 0.6f), s.RayleighColor);
        Assert.Equal(0.005f, s.MieCoefficient);
        Assert.Equal(0.8f, s.MieEccentricity);
        Assert.Equal(new Vector3(0.69f, 0.729f, 0.812f), s.MieColor);
        Assert.Equal(10f, s.Turbidity);
        Assert.Equal(1f, s.SunDiskScale);
        Assert.Equal(1f, s.EnergyMultiplier);
        Assert.Equal(300f, s.AltitudeMeters);

        // The defaults are Hillaire's Earth.
        Assert.Equal(AtmosphereParameters.EarthRayleighScattering, Earth.RayleighScattering);
        Assert.Equal(AtmosphereParameters.EarthMieScattering, Earth.MieScattering.X, 1e-9f);
        Assert.Equal(AtmosphereParameters.EarthMieExtinction, Earth.MieExtinction.Y, 1e-9f);
        Assert.Equal(0.8f, Earth.MieG);
        Assert.Equal((6360f, 6460f), (Earth.BottomRadius, Earth.TopRadius));
    }

    [Fact]
    public void GodotParametersScaleTheAtmosphere()
    {
        var hazy = new PhysicalSkySettings { Turbidity = 20f, MieCoefficient = 0.01f }.ToAtmosphere();
        Assert.Equal(Earth.MieScattering.X * 4f, hazy.MieScattering.X, 1e-8f);
        var thin = new PhysicalSkySettings { RayleighCoefficient = 1f }.ToAtmosphere();
        Assert.Equal(Earth.RayleighScattering / 2f, thin.RayleighScattering);
        var red = new PhysicalSkySettings { RayleighColor = new Vector3(0.6f, 0.405f, 0.6f) }.ToAtmosphere();
        Assert.Equal(Earth.RayleighScattering.X * 2f, red.RayleighScattering.X, 1e-8f);
        Assert.Equal(Earth.RayleighScattering.Z, red.RayleighScattering.Z, 1e-8f);
        Assert.Equal(0.999f, new PhysicalSkySettings { MieEccentricity = 2f }.ToAtmosphere().MieG);
    }

    [Fact]
    public void TransmittanceMatchesEarthsOpticalDepth()
    {
        // Straight up from sea level the optical depth is ≈ σ·H for each layer: Rayleigh 8 km, Mie 1.2 km, ozone 15 km.
        var up = AtmosphereModel.Transmittance(Earth, Earth.BottomRadius, 1f, 256);
        Vector3 Depth(Vector3 rayleigh, float mie, Vector3 ozone) => rayleigh * 8f * (1f - MathF.Exp(-100f / 8f)) + new Vector3(mie * 1.2f) + ozone * 15f;
        var expected = Depth(Earth.RayleighScattering, AtmosphereParameters.EarthMieExtinction, Earth.OzoneAbsorption);
        Assert.Equal(MathF.Exp(-expected.X), up.X, 2e-3f);
        Assert.Equal(MathF.Exp(-expected.Y), up.Y, 2e-3f);
        Assert.Equal(MathF.Exp(-expected.Z), up.Z, 2e-3f);
        Assert.True(up.Z < up.Y && up.Y < up.X, "blue is scattered most");

        // At sunset the light crosses far more air: dimmer and red.
        var low = AtmosphereModel.SunTransmittance(Earth, 300f, Vector3.Normalize(new Vector3(1f, 0.03f, 0f)));
        Assert.True(low.X > 3f * low.Z && low.X < up.X, $"sunset transmittance {low}");
        Assert.Equal(Vector3.Zero, AtmosphereModel.Transmittance(Earth, Earth.BottomRadius + 0.3f, -0.5f)); // into the ground
    }

    [Fact]
    public void SingleScatteringMakesABlueSkyAndAnOrangeSunset()
    {
        var sun = Vector3.Normalize(new Vector3(0.5f, 0.85f, 0f));
        var zenith = AtmosphereModel.SingleScattering(Earth, 0.3f, Vector3.UnitY, sun);
        Assert.True(zenith.Z > zenith.Y && zenith.Y > zenith.X, $"noon zenith {zenith}");
        var horizon = AtmosphereModel.SingleScattering(Earth, 0.3f, Vector3.Normalize(new Vector3(-0.5f, 0.02f, 0.86f)), sun);
        Assert.True(horizon.X + horizon.Y + horizon.Z > zenith.X + zenith.Y + zenith.Z, "the horizon is brighter than the zenith");

        var setting = Vector3.Normalize(new Vector3(1f, 0.02f, 0f));
        var towardsSun = AtmosphereModel.SingleScattering(Earth, 0.3f, Vector3.Normalize(new Vector3(1f, 0.05f, 0.05f)), setting);
        Assert.True(towardsSun.X > towardsSun.Z, $"sunset glow {towardsSun}");
    }

    [Fact]
    public void PhaseFunctionsIntegrateToOne()
    {
        static float Integrate(Func<float, float> phase)
        {
            const int n = 20000;
            var sum = 0.0;
            for (var i = 0; i < n; i++)
            {
                var mu = -1f + (i + 0.5f) * 2f / n;
                sum += phase(mu) * 2.0 * Math.PI * (2.0 / n);
            }

            return (float)sum;
        }

        Assert.Equal(1f, Integrate(AtmosphereModel.RayleighPhase), 1e-3f);
        Assert.Equal(1f, Integrate(mu => AtmosphereModel.MiePhase(0.8f, mu)), 2e-2f);
        Assert.True(AtmosphereModel.MiePhase(0.8f, 1f) > 50f * AtmosphereModel.MiePhase(0.8f, -1f), "Mie scatters forwards");
    }

    [Fact]
    public void SkyResourcePhysicalExportsRoundTripAndFeedTheSettings()
    {
        var root = new Node3D { Name = "Root" };
        var env = new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky { Mode = SkyEnvironmentType.Physical, Turbidity = 3f, MieEccentricity = 0.7f, AltitudeMeters = 1200f, EnergyMultiplier = 2f },
        };
        root.AddChild(env);
        env.Owner = root;

        var copy = (WorldEnvironment)PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetChild(0);
        var sky = copy.Sky!;
        Assert.Equal(SkyEnvironmentType.Physical, sky.Mode);
        var settings = sky.PhysicalSettings;
        Assert.Equal((3f, 0.7f, 1200f, 2f), (settings.Turbidity, settings.MieEccentricity, settings.AltitudeMeters, settings.EnergyMultiplier));
        Assert.Equal(sky.GroundColor, settings.GroundColor);
        Assert.Equal(new Sky().PhysicalSettings, new PhysicalSkySettings()); // the resource's defaults are the settings'
    }

    [Fact]
    public void AutoExposureDefaultsOffWithGodotsScaleAndSpeed()
    {
        var s = PostProcessSettings.Default;
        Assert.False(s.AutoExposureEnabled);
        Assert.Equal(0.4f, s.AutoExposureScale);
        Assert.Equal(0.5f, s.AutoExposureSpeed);
        Assert.True(s.AutoExposureMinLuminance > 0f && s.AutoExposureMinLuminance < s.AutoExposureMaxLuminance);
        Assert.NotEqual(PostProcessSettings.Default, s with { AutoExposureEnabled = true }); // turns the post pass on
    }

    [Fact]
    public void AutoExposureAdaptsExponentiallyAndClampsTheLuminance()
    {
        var s = PostProcessSettings.Default with { AutoExposureEnabled = true, AutoExposureSpeed = 2f };
        Assert.Equal(0f, s.AutoExposureBlend(0f));
        Assert.Equal(1f - MathF.Exp(-2f / 60f), s.AutoExposureBlend(1f / 60f), 1e-6f);
        Assert.Equal(1f, s.AutoExposureBlend(100f), 1e-6f);
        Assert.Equal(0f, s.AutoExposureBlend(-1f));

        // 60 frames at 1/60 s close 1 − e^(−2) of the gap, as the GPU's per-frame blend does.
        var adapted = 1f;
        for (var i = 0; i < 60; i++)
            adapted += (0.1f - adapted) * s.AutoExposureBlend(1f / 60f);
        Assert.Equal(1f + (0.1f - 1f) * (1f - MathF.Exp(-2f)), adapted, 1e-4f);

        Assert.Equal(0.4f / 0.2f, s.AutoExposureFor(0.2f), 1e-6f);
        Assert.Equal(0.4f / s.AutoExposureMinLuminance, s.AutoExposureFor(1e-4f), 1e-4f);
        Assert.Equal(0.4f / s.AutoExposureMaxLuminance, s.AutoExposureFor(1000f), 1e-6f);
    }

    [Fact]
    public void WorldEnvironmentAutoExposureExportsRoundTrip()
    {
        var root = new Node3D { Name = "Root" };
        var env = new WorldEnvironment
        {
            Name = "Environment",
            PostProcess = new PostProcessProfile
            {
                AutoExposureEnabled = true,
                AutoExposureScale = 0.3f,
                AutoExposureSpeed = 1.5f,
                AutoExposureMinLuminance = 0.01f,
                AutoExposureMaxLuminance = 4f,
            },
        };
        root.AddChild(env);
        env.Owner = root;

        var copy = (WorldEnvironment)PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetChild(0);
        Assert.Equal(env.PostProcessSettings, copy.PostProcessSettings);
        Assert.True(copy.PostProcessSettings.AutoExposureEnabled);
        Assert.Equal(4f, copy.PostProcess!.AutoExposureMaxLuminance);
    }

    [Fact]
    public void AntiAliasingDefaultsToNoneEverywhere()
    {
        Assert.Equal(AntiAliasing.None, new EngineOptions { GameName = "t" }.AntiAliasing);
        Assert.Equal(AntiAliasing.None, new VulkanRendererOptions().AntiAliasing);
        Assert.Equal(AntiAliasing.None, new ProjectSettings().Rendering.AntiAliasing);
        Assert.Equal(["None", "Fxaa", "Taa"], Enum.GetNames<AntiAliasing>());
    }

    [Fact]
    public void TheAntiAliasingProjectSettingRoundTrips()
    {
        var settings = new ProjectSettings();
        settings.Rendering.AntiAliasing = AntiAliasing.Fxaa;
        var json = Encoding.UTF8.GetString(settings.ToJson());
        Assert.Contains("\"antiAliasing\": \"Fxaa\"", json, StringComparison.Ordinal);
        Assert.Equal(AntiAliasing.Fxaa, Parse(json).Rendering.AntiAliasing);
        Assert.Equal(AntiAliasing.Fxaa, Parse("""{ "format": 1, "rendering": { "antiAliasing": "fxaa" } }""").Rendering.AntiAliasing);
        Assert.DoesNotContain("antiAliasing", Encoding.UTF8.GetString(new ProjectSettings().ToJson()), StringComparison.Ordinal);

        var error = Assert.ThrowsAny<Exception>(() => Parse("""{ "format": 1, "rendering": { "antiAliasing": "Msaa" } }"""));
        Assert.Contains("rendering.antiAliasing", error.Message, StringComparison.Ordinal);
        Assert.Contains("'Msaa' is not one of None, Fxaa, Taa", error.Message, StringComparison.Ordinal);
    }

    private static ProjectSettings Parse(string json) => ProjectSettings.Parse(Encoding.UTF8.GetBytes(json), "test.mfproj");
}
