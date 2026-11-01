using System.Numerics;
using MainframeEngine;

namespace Neon.Tests;

/// <summary>A plain <see cref="INeonDisplay"/>: the renderer switches without a GPU.</summary>
internal sealed class FakeDisplay : INeonDisplay
{
    public AntiAliasing AntiAliasing { get; set; } = AntiAliasing.Taa;

    public Scaling3DMode Scaling3DMode { get; set; } = Scaling3DMode.Taau;

    public float Scaling3DScale { get; set; } = 1f;

    public float TaaSharpness { get; set; } = 0.25f;
}

/// <summary>The parts of Neon's scene the menu acts on, with Neon's look.</summary>
internal static class MenuScene
{
    public static Node3D Build()
    {
        var scene = new Node3D { Name = "Scene" };
        scene.AddChild(new DirectionalLight3D { Name = "Moon", ShadowMaxDistance = 40f });
        var environment = NeonScene.CreateEnvironment();
        environment.PostProcess = NeonLook.CreateProfile(); // its own: the loaded files are shared between tests
        environment.CameraAttributes = NeonLook.CreateLens();
        scene.AddChild(environment);
        scene.AddChild(new LightProbeVolume { Name = "Lighting", Energy = 1.5f });
        return scene;
    }

    public static NeonWorld World(Node scene, UserSettings? user = null)
    {
        var world = NeonWorld.Capture(scene, new FakeDisplay(), user ?? new UserSettings(new ConfigFile()));
        world.Fps ??= new NeonFps();
        world.Player ??= new FirstPersonController();
        world.CaptureBaselines();
        return world;
    }
}

/// <summary>The pause menu's settings model: the option table, defaults, clamping, presets and persistence.</summary>
public sealed class NeonOptionsTests
{
    [Fact]
    public void EveryOptionHasAUniqueKeyAPageAndAValidRange()
    {
        var world = MenuScene.World(MenuScene.Build());
        Assert.Equal(NeonOptions.All.Length, NeonOptions.All.Select(o => o.Key).Distinct().Count());
        foreach (var option in NeonOptions.All)
        {
            Assert.Contains(option.Page, NeonMenu.Pages);
            Assert.Matches("^[a-z_0-9]+$", option.Key);
            Assert.Same(option, NeonOptions.Find(option.Key));
            if (option.Kind == NeonOptionKind.Slider)
                Assert.True(option.Max > option.Min, option.Key);
            if (option.Kind is NeonOptionKind.Choice or NeonOptionKind.Stepper)
                Assert.True(option.ChoicesIn(world).Length >= 2, option.Key);
        }

        Assert.Null(NeonOptions.Find("nope"));
        foreach (var page in NeonMenu.Pages)
            Assert.NotEmpty(NeonOptions.OnPage(page));
        Assert.True(NeonOptions.All.Length >= 45, $"{NeonOptions.All.Length} options");
    }

    [Fact]
    public void ThePresetsSetOnlyRealOptionsAndReadBack()
    {
        var world = MenuScene.World(MenuScene.Build());
        var preset = NeonOptions.Find("preset")!;
        Assert.Equal(NeonOptions.Presets.Length, preset.Choices.Length - 1); // and Custom
        for (var i = 0; i < NeonOptions.Presets.Length; i++)
        {
            foreach (var (key, _) in NeonOptions.Presets[i].Values)
                Assert.NotNull(NeonOptions.Find(key));
            Assert.Equal(preset.Choices[i], NeonOptions.Presets[i].Name);
        }

        // Off-tree the city and apartment are missing, so what they own cannot match; the display options still apply.
        world.ApplyPreset(0);
        Assert.Equal(AntiAliasing.Fxaa, world.Display!.AntiAliasing);
        Assert.Equal(0.6f, world.RenderScale);
        Assert.Equal(Scaling3DMode.Fsr, world.Display.Scaling3DMode);
        Assert.False(world.Profile!.SsaoEnabled);
        world.ApplyPreset(99); // "Custom" changes nothing
        Assert.Equal(AntiAliasing.Fxaa, world.Display.AntiAliasing);
    }

    [Fact]
    public void ClampSnapsSlidersAndKeepsChoicesInRange()
    {
        var world = MenuScene.World(MenuScene.Build());
        var scale = NeonOptions.Find("render_scale")!;
        Assert.Equal(0.75f, scale.Clamp(world, 0.76f));
        Assert.Equal(0.5f, scale.Clamp(world, 0.1f));
        Assert.Equal(0.5f, scale.Clamp(world, float.NaN));
        Assert.Equal(1f, NeonOptions.Find("ssao")!.Clamp(world, 0.7f));
        Assert.Equal(2f, NeonOptions.Find("aa")!.Clamp(world, 9f));
        var fps = NeonOptions.Find("max_fps")!;
        Assert.Equal(7f, fps.Clamp(world, 40f));
        Assert.Equal(0f, fps.Clamp(world, -3f));
    }

    [Fact]
    public void ReadoutsDescribeTheValue()
    {
        var world = MenuScene.World(MenuScene.Build());
        Assert.Equal("75 %", NeonOptions.Find("render_scale")!.Describe(world, 0.75f));
        Assert.Equal("+0.5 EV", NeonOptions.Find("exposure")!.Describe(world, 0.5f));
        Assert.Equal("0.0 EV", NeonOptions.Find("exposure")!.Describe(world, 0.01f));
        Assert.Equal("On", NeonOptions.Find("ssao")!.Describe(world, 1f));
        Assert.Equal("FXAA", NeonOptions.Find("aa")!.Describe(world, 1f));
        Assert.Equal("144", NeonOptions.Find("max_fps")!.Describe(world, 5f));
        Assert.Equal("1920 × 1080", NeonOptions.Find("resolution")!.Describe(world, world.WindowSizeIndex));
    }

    [Fact]
    public void TheGrainIsGoneFromNeonsLookAndTheRainIsLight()
    {
        Assert.Equal(0f, NeonLook.CreateLens().FilmGrainIntensity);
        var lens = Path.Combine(AppContext.BaseDirectory, NeonLook.LensPath);
        Assert.DoesNotContain("FilmGrain", File.ReadAllText(lens), StringComparison.Ordinal);
        var world = MenuScene.World(MenuScene.Build());
        Assert.Equal(0f, world.DefaultOf(NeonOptions.Find("grain")!));
        Assert.Equal(NeonRain.DefaultDensity, NeonOptions.Find("rain")!.Get(world));
        Assert.True(NeonRain.DefaultDensity < 0.5f);
    }

    [Fact]
    public void DefaultsAreTheScenesOwnValues()
    {
        var world = MenuScene.World(MenuScene.Build());
        Assert.Equal(2f, world.DefaultOf(NeonOptions.Find("upscaling")!)); // native
        Assert.Equal(1f, world.DefaultOf(NeonOptions.Find("haze")!)); // 100 % of the look
        Assert.Equal(1f, world.DefaultOf(NeonOptions.Find("glow")!));
        Assert.Equal(0f, world.DefaultOf(NeonOptions.Find("exposure")!));
        Assert.Equal(40f, world.DefaultOf(NeonOptions.Find("moon_distance")!));
        Assert.Equal(90f, world.DefaultOf(NeonOptions.Find("fov")!));
    }

    [Fact]
    public void EveryOptionReadsBackWhatWasApplied()
    {
        var world = MenuScene.World(MenuScene.Build());
        foreach (var option in NeonOptions.All)
        {
            if (option.Store == NeonOptionStore.Derived || option.Page == NeonOptions.Graphics && option.Group is "City" or "Weather" && option.Key is not ("haze" or "street_haze" or "volumetric_fog" or "volumetric_density"))
                continue; // the city's own options need a built city (CityMenuTests)
            float[] values = option.Kind switch
            {
                NeonOptionKind.Toggle => [0f, 1f, 0f],
                NeonOptionKind.Choice or NeonOptionKind.Stepper => Enumerable.Range(0, option.ChoicesIn(world).Length).Select(i => (float)i).Reverse().ToArray(),
                _ => [option.Min, option.Max, option.Min + 0.4f * (option.Max - option.Min)],
            };
            foreach (var value in values)
            {
                var applied = world.Apply(option, value);
                Assert.Equal(option.Clamp(world, value), applied);
                Assert.True(MathF.Abs(applied - world.Value(option)) <= MathF.Max(option.Step, 1e-3f) * 0.51f,
                    $"{option.Key}: applied {applied}, read back {world.Value(option)}");
            }
        }
    }

    [Fact]
    public void OptionsSetTheEnginesRuntimeProperties()
    {
        var scene = MenuScene.Build();
        var world = MenuScene.World(scene);
        var display = (FakeDisplay)world.Display!;
        var env = scene.GetNode<WorldEnvironment>("Environment");
        var profile = env.PostProcess!;
        var lens = env.CameraAttributes!;

        void Set(string key, float value) => world.Apply(NeonOptions.Find(key)!, value);

        Set("upscaling", 1);
        Set("render_scale", 0.6f);
        Assert.Equal((Scaling3DMode.Fsr, 0.6f), (display.Scaling3DMode, display.Scaling3DScale));
        Set("upscaling", 2);
        Assert.Equal(1f, display.Scaling3DScale);
        Set("aa", 1);
        Assert.Equal(AntiAliasing.Fxaa, display.AntiAliasing);
        Set("sharpen", 0.5f);
        Assert.Equal(0.5f, display.TaaSharpness);

        Set("haze", 2f);
        Assert.Equal(0.0052f, env.FogDensity, 6);
        Set("street_haze", 0f);
        Assert.Equal(0f, env.FogHeightDensity);
        Set("volumetric_fog", 1);
        Assert.True(env.VolumetricFogEnabled);

        Set("ssao", 0);
        Assert.False(profile.SsaoEnabled);
        Set("tonemapper", 1);
        Assert.Equal(Tonemapper.GodotAces, profile.Tonemapper);
        Set("exposure", 1f);
        Assert.Equal(2.2f, profile.TonemapExposure, 4);
        Set("glow", 0f);
        Assert.False(profile.GlowEnabled);
        Set("glow", 2f);
        Assert.True(profile.GlowEnabled);
        Assert.Equal(1.1f, profile.GlowIntensity, 4);
        Set("glow_threshold", 3f);
        Assert.Equal(3f, profile.GlowHdrThreshold);

        Set("dof", 1);
        Set("dof_distance", 20f);
        Assert.True(lens.DofBlurFarEnabled);
        Assert.Equal(20f, lens.DofBlurFarDistance);
        Set("aberration", 0.5f);
        Assert.Equal(2f, lens.ChromaticAberrationIntensity, 4);
        Set("grain", 0.5f);
        Assert.Equal(0.05f, lens.FilmGrainIntensity, 4);

        Set("fps", 0);
        Assert.False(world.Fps!.Visible);
        Set("fov", 100f);
        Assert.Equal(100f, world.Player!.HorizontalFov);
        Set("sensitivity", 0.3f);
        Assert.Equal(0.3f, world.Player.MouseSensitivity);
        Set("head_bob", 0);
        Assert.False(world.Player.HeadBob);
    }

    [Fact]
    public void OnlyChangedSceneOptionsAreStoredAndResetRestoresThePage()
    {
        var scene = MenuScene.Build();
        var world = MenuScene.World(scene);
        var config = world.User!.Config;
        world.Apply(NeonOptions.Find("ssao")!, 0f);
        world.Apply(NeonOptions.Find("haze")!, 1f); // its default: nothing to store
        Assert.Equal(["ssao"], config.GetSectionKeys(NeonWorld.Section));

        world.Apply(NeonOptions.Find("ssao")!, 1f);
        Assert.Empty(config.GetSectionKeys(NeonWorld.Section));

        world.Apply(NeonOptions.Find("exposure")!, -1f);
        world.Apply(NeonOptions.Find("bounce")!, 3f);
        world.Reset(NeonOptions.Camera);
        Assert.Equal(1.1f, scene.GetNode<WorldEnvironment>("Environment").PostProcess!.TonemapExposure, 4);
        Assert.Equal(3f, scene.GetNode<LightProbeVolume>("Lighting").Energy); // another page
    }

    [Fact]
    public void SavedOptionsApplyAtStartAndUnknownKeysAreDropped()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("neon-menu").FullName, "settings.json");
        var user = new UserSettings(new ConfigFile());
        var world = MenuScene.World(MenuScene.Build(), user);
        world.Apply(NeonOptions.Find("tonemapper")!, 1f);
        world.Apply(NeonOptions.Find("haze")!, 0.5f);
        user.Config.SetValue(NeonWorld.Section, "removed_option", 3.0); // from an older build
        user.Config.SetValue(NeonWorld.Section, "render_scale", 9.0);   // hand-edited
        user.Config.Save(path);

        var scene = MenuScene.Build();
        var fresh = MenuScene.World(scene, UserSettings.Load(path));
        fresh.ApplySaved();
        Assert.Equal(Tonemapper.GodotAces, scene.GetNode<WorldEnvironment>("Environment").PostProcess!.Tonemapper);
        Assert.Equal(0.0013f, scene.GetNode<WorldEnvironment>("Environment").FogDensity, 6);
        Assert.Equal(1f, fresh.RenderScale);
        Assert.DoesNotContain("removed_option", fresh.User!.Config.GetSectionKeys(NeonWorld.Section));
        Assert.Equal(1f, fresh.DefaultOf(NeonOptions.Find("haze")!)); // the defaults are still the scene's
    }

    // Reference-resolution scaling scales dp with the window: a px length (or a px media query) would not follow.
    [Fact]
    public void TheStyleSheetsAreAuthoredInDpAndTheDocumentUsesDataClassHidden()
    {
        var ui = Path.Combine(AppContext.BaseDirectory, "Assets", "UI");
        foreach (var sheet in (ReadOnlySpan<string>)["menu.rcss", "keys.rcss"])
            Assert.DoesNotMatch(@"\d+(\.\d+)?px\b", File.ReadAllText(Path.Combine(ui, sheet)));
        var rml = NeonMenu.BuildRml();
        Assert.DoesNotContain("data-visible", rml, StringComparison.Ordinal); // RmlUi's keeps the space
        foreach (var option in NeonOptions.All)
            Assert.Contains("id=\"opt-" + option.Key + "\"", rml, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, NeonMenu.KeysSource)));
    }
}

/// <summary>The city's options, on a built city.</summary>
public sealed class CityMenuTests
{
    [Fact]
    public void TheCitysOptionsReachItsShadersAndNodes()
    {
        var bin = Path.Combine(AppContext.BaseDirectory, CityKit.TowersBufferPath);
        if (!File.Exists(bin) || File.ReadAllText(bin).StartsWith("version https://git-lfs", StringComparison.Ordinal))
            Assert.Skip("Neon's LFS art is not checked out (git lfs pull).");
        var scene = MenuScene.Build();
        var city = new NeonCity { Name = "City", Rain = false, Traffic = false };
        scene.AddChild(city);
        city.Build();
        try
        {
            var world = MenuScene.World(scene);
            void Set(string key, float value) => world.Apply(NeonOptions.Find(key)!, value);

            Set("city_detail", 0f);
            Set("city_lights", 0.5f);
            Set("neon", 1.5f);
            Set("reflections", 0.25f);
            Set("crowd", 0f);
            Set("holograms", 0f);
            Set("puddles", 0f);
            Assert.False(city.Detail);
            Assert.Equal(0.5f, city.LitWindows);
            Assert.Equal(1.5f, city.NeonBrightness);
            Assert.Equal(0.25f, city.StreetReflections);
            Assert.False(city.GetNode<MultiMeshInstance3D>("Crowd").Visible);
            Assert.False(city.GetNode<MeshInstance3D>("HologramKoi").Visible);
            Assert.False(city.PuddleReflections);
            foreach (var material in city.FacadeMaterials)
                Assert.Equal(0.5f, (float)material.GetShaderParameter("lit_scale")!);
        }
        finally
        {
            scene.Free();
        }
    }
}

/// <summary>Opening and closing the menu.</summary>
public sealed class NeonMenuTests
{
    [Fact]
    public void WhileOpenThePlayerIsStoppedAndClosingGivesThemBack()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer(Vector3.Zero);
        var menu = new NeonMenu { World = new NeonWorld { Player = player } };
        menu.Open(NeonOptions.Audio);
        Assert.True(menu.IsOpen);
        Assert.Equal(NeonOptions.Audio, menu.Page);
        Assert.Equal(ProcessMode.Disabled, player.ProcessMode);
        menu.ShowPage("nope");
        Assert.Equal(NeonOptions.Audio, menu.Page);
        var closed = 0;
        menu.Closed += () => closed++;
        menu.Close();
        menu.Close();
        Assert.False(menu.IsOpen);
        Assert.Equal(1, closed);
        Assert.Equal(ProcessMode.Inherit, player.ProcessMode);
        menu.Free();
    }

    [Fact]
    public void NothingIsWrittenInScriptedRuns()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("neon-menu").FullName, "settings.json");
        var user = UserSettings.Load(path);
        var world = MenuScene.World(MenuScene.Build(), user);
        var menu = new NeonMenu { World = world };
        menu.Open();
        menu.Set(NeonOptions.Find("ssao")!, 0f);
        menu.Close();
        Assert.Equal(0, menu.Saves);
        Assert.False(File.Exists(path));

        menu.Persist = true;
        menu.Open();
        menu.Set(NeonOptions.Find("ssao")!, 1f);
        menu.Set(NeonOptions.Find("tonemapper")!, 2f);
        menu.Close();
        Assert.Equal(1, menu.Saves);
        Assert.True(File.Exists(path));
        menu.Free();
    }
}
