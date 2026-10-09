using System.Text;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0169: the post-processing profile resource, the environment's reference to it and the scene migration.</summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class PostProcessProfileTests
{
    [Fact]
    public void ANewProfileIsTheEngineDefault()
    {
        var profile = new PostProcessProfile();
        Assert.Equal(PostProcessSettings.Default, profile.Settings);
        Assert.Equal(PostProcessSettings.Default, new WorldEnvironment().PostProcessSettings);
        Assert.Equal(PostProcessSettings.Default, new WorldEnvironment { PostProcess = profile }.PostProcessSettings);
    }

    [Fact]
    public void TheStructIsBuiltFromTheProfileAndTheLens()
    {
        var lut = CubeLut.FromFunction(2, static c => c, "id").ToTexture3D();
        var profile = new PostProcessProfile
        {
            Tonemapper = Tonemapper.Agx,
            TonemapExposure = 1.3f,
            AutoExposureEnabled = true,
            AutoExposureSpeed = 2f,
            GlowEnabled = true,
            GlowQuality = GlowQuality.High,
            GlowLevel5 = 0.25f,
            LightShaftsEnabled = true,
            LightShaftsSamples = 24,
            SsaoEnabled = true,
            SsaoRadius = 0.6f,
            AdjustmentEnabled = true,
            AdjustmentColorCorrection = lut,
            AdjustmentColorCorrectionStrength = 0.5f,
        };
        var expected = PostProcessSettings.Default with
        {
            Tonemapper = Tonemapper.Agx,
            TonemapExposure = 1.3f,
            AutoExposureEnabled = true,
            AutoExposureSpeed = 2f,
            GlowEnabled = true,
            GlowQuality = GlowQuality.High,
            GlowLevel5 = 0.25f,
            LightShaftsEnabled = true,
            LightShaftsSamples = 24,
            SsaoEnabled = true,
            SsaoRadius = 0.6f,
            AdjustmentEnabled = true,
            AdjustmentColorCorrection = lut,
            AdjustmentColorCorrectionStrength = 0.5f,
        };
        Assert.Equal(expected, profile.Settings);

        var lens = new CameraAttributesPractical { VignetteIntensity = 0.2f, DofBlurFarEnabled = true };
        var environment = new WorldEnvironment { PostProcess = profile, CameraAttributes = lens };
        Assert.Equal(lens.ApplyTo(expected), environment.PostProcessSettings);
        Assert.Equal(0.2f, environment.PostProcessSettings.VignetteIntensity);

        // FromSettings keeps everything but the lens.
        var copy = PostProcessProfile.FromSettings(environment.PostProcessSettings);
        Assert.Equal(expected, copy.Settings);
    }

    [Fact]
    public void EveryExportedMemberIsAMovedEnvironmentProperty()
    {
        // The migration moves exactly the profile's properties (ResourceName is the base Resource's).
        var info = TypeRegistry.Get(typeof(PostProcessProfile))!;
        var exported = info.Properties.Select(p => p.Name).Where(n => n != nameof(Resource.ResourceName)).Order(StringComparer.Ordinal);
        Assert.Equal(exported, PostProcessProfile.MovedPropertyNames.Order(StringComparer.Ordinal));
        Assert.Equal(PostProcessProfile.MovedPropertyNames.Length, PostProcessProfile.MovedPropertyNames.Distinct().Count());
        Assert.Equal(2, TypeRegistry.Get(typeof(WorldEnvironment))!.Version);
        foreach (var group in info.Properties.Where(p => p.Name != nameof(Resource.ResourceName)).Select(p => p.Group).Distinct())
            Assert.Contains(group, (string?[])["Tonemap", "Auto Exposure", "Glow", "Light Shafts", "SSAO", "Adjustments"]);
    }

    [Fact]
    public void ChangingAValueRaisesChangedOnce()
    {
        var profile = new PostProcessProfile();
        var environment = new WorldEnvironment { PostProcess = profile };
        var changes = 0;
        profile.Changed += () => changes++;
        profile.SsaoEnabled = true;
        Assert.Equal(1, changes);
        Assert.True(environment.PostProcessSettings.SsaoEnabled); // applies at once: the next frame reads it
        profile.SsaoEnabled = true; // unchanged: no event
        Assert.Equal(1, changes);
        profile.GlowIntensity = 0.5f;
        Assert.Equal(2, changes);
        Assert.Equal(0.5f, environment.PostProcessSettings.GlowIntensity);
    }

    [Fact]
    public void AnInlineProfileRoundTripsThroughAScene()
    {
        var root = new Node3D { Name = "Root" };
        var environment = new WorldEnvironment
        {
            Name = "Environment",
            PostProcess = new PostProcessProfile { Tonemapper = Tonemapper.Filmic, GlowEnabled = true, SsaoEnabled = true, SsaoPower = 1.1f },
        };
        root.AddChild(environment);
        environment.Owner = root;

        var json = Encoding.UTF8.GetString(SceneSaver.ToJson(root));
        Assert.Contains("\"type\": \"PostProcessProfile\"", json, StringComparison.Ordinal);
        Assert.Contains("\"v\": 2", json, StringComparison.Ordinal);
        var copy = (WorldEnvironment)PackedScene.Parse(Encoding.UTF8.GetBytes(json)).Instantiate().GetChild(0);
        Assert.Equal(environment.PostProcessSettings, copy.PostProcessSettings);
        Assert.Equal(1.1f, copy.PostProcess!.SsaoPower);
    }

    [Fact]
    public void AProfileFileRoundTripsAndScenesReferenceIt()
    {
        var project = Path.Combine(Path.GetTempPath(), "mf-post-" + Guid.NewGuid().ToString("N"));
        var previous = AssetDatabase.Current;
        Directory.CreateDirectory(Path.Combine(project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(project);
        ResourceLoader.ClearCache();
        try
        {
            var profile = new PostProcessProfile
            {
                AutoExposureEnabled = true,
                AutoExposureScale = 0.2f,
                LightShaftsEnabled = true,
                GlowHdrThreshold = 3f,
                // ADR 0177
                AutoExposureMode = AutoExposureMode.Histogram,
                AutoExposureMetering = AutoExposureMetering.Uniform,
                AutoExposureHighPercent = 95f,
                AutoExposureHighlightProtection = true,
                AutoExposureHighlightWhite = 0.8f,
            };
            var uid = ResourceSaver.Save(profile, "Content/PostProcess/look.mres");
            var root = new Node3D { Name = "Root" };
            var environment = new WorldEnvironment { Name = "Environment", PostProcess = profile };
            root.AddChild(environment);
            environment.Owner = root;
            var json = Encoding.UTF8.GetString(SceneSaver.ToJson(root));
            Assert.Contains(uid, json, StringComparison.Ordinal);
            Assert.DoesNotContain("AutoExposureScale", json, StringComparison.Ordinal); // in the file, not the scene

            ResourceLoader.ClearCache();
            var loaded = ResourceLoader.Load<PostProcessProfile>(uid);
            Assert.Equal(profile.Settings, loaded.Settings);
            var copy = (WorldEnvironment)PackedScene.Parse(Encoding.UTF8.GetBytes(json)).Instantiate().GetChild(0);
            Assert.Same(loaded, copy.PostProcess); // one shared, cached profile
            loaded.Release();
        }
        finally
        {
            ResourceLoader.ClearCache();
            AssetDatabase.Current = previous;
            Directory.Delete(project, recursive: true);
        }
    }

    // A scene written before ADR 0169 (the Forest's environment at 1edf326f): post-processing on the environment itself,
    // with a LUT reference and the lens.
    private const string VersionOneScene = """
        {
          "format": 2,
          "uid": "scn_5030bd74b9d6",
          "resources": {
            "CubeLut_a": { "type": "Texture3D", "props": { } },
            "CameraAttributesPractical_m6y5e": {
              "type": "CameraAttributesPractical",
              "props": { "VignetteIntensity": 0.2, "FilmGrainIntensity": 0.015 }
            }
          },
          "nodes": [
            { "name": "forest", "type": "Node3D" },
            {
              "name": "Environment",
              "parent": ".",
              "type": "WorldEnvironment",
              "props": {
                "AmbientEnergy": 1.6,
                "FogEnabled": true,
                "FogSunScatter": 0.3,
                "Tonemapper": "Agx",
                "GlowEnabled": true,
                "GlowHdrThreshold": 4,
                "GlowHdrLuminanceCap": 3,
                "GlowQuality": "High",
                "AdjustmentEnabled": true,
                "AdjustmentColorCorrection": { "res": "CubeLut_a" },
                "CameraAttributes": { "res": "CameraAttributesPractical_m6y5e" },
                "AutoExposureEnabled": true,
                "AutoExposureScale": 0.16,
                "LightShaftsEnabled": true,
                "LightShaftsIntensity": 2.3,
                "SsaoEnabled": true,
                "SsaoRadius": 0.8,
                "SsaoDetail": 0.4
              }
            }
          ]
        }
        """;

    [Fact]
    public void OldScenesLoadTheirPostSettingsIntoAnInlineProfile()
    {
        var root = PackedScene.Parse(Encoding.UTF8.GetBytes(VersionOneScene)).Instantiate();
        var environment = (WorldEnvironment)root.GetChild(0);
        Assert.Equal(1.6f, environment.AmbientEnergy); // environment properties stay where they were
        Assert.True(environment.FogEnabled);
        var profile = environment.PostProcess;
        Assert.NotNull(profile);
        Assert.False(profile.IsExternal);
        Assert.Equal(Tonemapper.Agx, profile.Tonemapper);
        Assert.True(profile.GlowEnabled);
        Assert.Equal(4f, profile.GlowHdrThreshold);
        Assert.Equal(GlowQuality.High, profile.GlowQuality);
        Assert.True(profile.AdjustmentEnabled);
        Assert.NotNull(profile.AdjustmentColorCorrection); // the reference resolves against the scene's table
        Assert.Equal(0.16f, profile.AutoExposureScale);
        Assert.Equal(2.3f, profile.LightShaftsIntensity);
        Assert.Equal(0.8f, profile.SsaoRadius);
        Assert.Equal(0.2f, environment.CameraAttributes!.VignetteIntensity);
        Assert.Equal(0.2f, environment.PostProcessSettings.VignetteIntensity);

        // The next save writes version 2: the profile as a sub-resource, nothing post-related on the node.
        var saved = Encoding.UTF8.GetString(SceneSaver.ToJson(root, "scn_5030bd74b9d6"));
        using var document = System.Text.Json.JsonDocument.Parse(saved);
        var node = document.RootElement.GetProperty("nodes")[1];
        Assert.Equal(2, node.GetProperty("v").GetInt32());
        var props = node.GetProperty("props");
        Assert.True(props.TryGetProperty("PostProcess", out _));
        foreach (var name in PostProcessProfile.MovedPropertyNames)
            Assert.False(props.TryGetProperty(name, out _), name);
        var reloaded = (WorldEnvironment)PackedScene.Parse(Encoding.UTF8.GetBytes(saved)).Instantiate().GetChild(0);
        Assert.Equal(environment.PostProcessSettings with { AdjustmentColorCorrection = null },
            reloaded.PostProcessSettings with { AdjustmentColorCorrection = null });
        Assert.NotNull(reloaded.PostProcess!.AdjustmentColorCorrection);
    }

    [Fact]
    public void OldScenesWithoutPostSettingsGetNoProfile()
    {
        const string scene = """
            { "format": 2, "uid": "scn_000000000001", "nodes": [
              { "name": "Root", "type": "Node3D" },
              { "name": "Environment", "parent": ".", "type": "WorldEnvironment", "props": { "FogEnabled": true } } ] }
            """;
        var environment = (WorldEnvironment)PackedScene.Parse(Encoding.UTF8.GetBytes(scene)).Instantiate().GetChild(0);
        Assert.Null(environment.PostProcess);
        Assert.True(environment.FogEnabled);
        Assert.Equal(PostProcessSettings.Default, environment.PostProcessSettings);
    }

    [Fact]
    public void TheMigrationBuildsAnInlineResourceValue()
    {
        var bag = new PropertyBag();
        bag.SetBoolean("SsaoEnabled", true);
        bag.SetNumber("SsaoRadius", 0.5);
        bag.SetNumber("FogDensity", 0.01);
        WorldEnvironment.MovePostProcessIntoProfile(bag);
        Assert.False(bag.Contains("SsaoEnabled"));
        Assert.True(bag.Contains("FogDensity"));
        Assert.True(bag.TryGet("PostProcess", out var value));
        Assert.Equal("PostProcessProfile", value.GetProperty("type").GetString());
        Assert.Equal(0.5, value.GetProperty("props").GetProperty("SsaoRadius").GetDouble());
    }
}
