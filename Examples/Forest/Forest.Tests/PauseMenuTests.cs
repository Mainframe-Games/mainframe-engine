using System.Numerics;
using MainframeEngine;
using Silk.NET.Input;

namespace Forest.Tests;

/// <summary>A plain <see cref="IForestDisplay"/>: the renderer switches without a GPU.</summary>
internal sealed class FakeDisplay : IForestDisplay
{
    public AntiAliasing AntiAliasing { get; set; } = AntiAliasing.Taa;

    public Scaling3DMode Scaling3DMode { get; set; } = Scaling3DMode.Taau;

    public float Scaling3DScale { get; set; } = 0.75f;

    public bool VSync { get; set; } = true;
}

/// <summary>The parts of the Forest scene the menu acts on, with the Forest's look (the profile and lens files' values).</summary>
internal static class MenuScene
{
    public static Node3D Build(Node3D scene)
    {
        scene.AddChild(new DirectionalLight3D
        {
            Name = "Sun",
            RotationDegrees = new Vector3(-ValleyLayout.SunElevationDegrees, 180f - ValleyLayout.SunAzimuthDegrees, 0f),
            ShadowResolution = 1024,
            ShadowMaxDistance = 140f,
            ShadowCascades = 4,
            LightAngularDistance = 0.5f,
            ContactShadows = true,
        });
        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            FogEnabled = true,
            FogDensity = 0.0015f,
            VolumetricFogEnabled = true,
            VolumetricFogDensity = 0.004f,
            WindStrength = 0.35f,
            PostProcess = new PostProcessProfile
            {
                Tonemapper = Tonemapper.Agx,
                AutoExposureEnabled = true,
                GlowEnabled = true,
                SsaoEnabled = true,
                SsaoIntensity = 1.1f,
                AdjustmentEnabled = true,
            },
            CameraAttributes = new CameraAttributesPractical { VignetteIntensity = 0.2f, FilmGrainIntensity = 0.015f },
        });
        return scene;
    }

    /// <summary>A world over <paramref name="scene"/> with every target the options need (a terrain and readout off-tree).</summary>
    public static ForestWorld World(Node scene, ForestSettings? settings = null)
    {
        var world = ForestWorld.Capture(scene, new FakeDisplay(), settings ?? new ForestSettings());
        world.Terrain ??= new Terrain3D();
        world.Fps ??= new FpsHud();
        world.CaptureBaselines();
        return world;
    }
}

/// <summary>RmlUi is process-global: the tests that start a <see cref="UiServer"/> run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MenuCollection
{
    public const string Name = "Menu";
}

/// <summary>The pause menu's settings model (ADR 0180): the option table, defaults, clamping, persistence and mapping.</summary>
public sealed class ForestOptionsTests
{
    [Fact]
    public void EveryOptionHasAUniqueKeyAPageAndAValidRange()
    {
        Assert.Equal(ForestOptions.All.Length, ForestOptions.All.Select(o => o.Key).Distinct().Count());
        foreach (var option in ForestOptions.All)
        {
            Assert.Contains(option.Page, PauseMenu.Pages);
            Assert.Matches("^[a-z_0-9]+$", option.Key);
            Assert.Same(option, ForestOptions.Find(option.Key));
            if (option.Kind == ForestOptionKind.Slider)
                Assert.True(option.Max > option.Min, option.Key);
            if (option.Kind == ForestOptionKind.Choice)
                Assert.True(option.Choices.Length >= 2, option.Key);
        }

        Assert.Null(ForestOptions.Find("nope"));
        // What Brogan asked for is there.
        foreach (var key in new[]
                 {
                     "upscaling", "render_scale", "aa", "shadow_res", "shadow_distance", "pcss", "contact_shadows", "ssao",
                     "ssao_intensity", "volumetric_fog", "volumetric_density", "haze", "light_shafts", "glow", "tonemapper",
                     "exposure", "auto_exposure", "grade", "dof", "grain", "vignette", "aberration", "fov", "vsync", "fps",
                     "grass_density", "grass_distance", "wind", "sun_elevation", "sun_azimuth", "master", "ambience", "water",
                     "footsteps", "mute",
                 })
            Assert.NotNull(ForestOptions.Find(key));
    }

    [Fact]
    public void ClampSnapsSlidersToTheirStepsAndRangeAndSwitchesAndChoicesToWholeValues()
    {
        var scale = ForestOptions.Find("render_scale")!;
        Assert.Equal(0.75f, scale.Clamp(0.76f));
        Assert.Equal(0.5f, scale.Clamp(0.1f));
        Assert.Equal(1f, scale.Clamp(7f));
        Assert.Equal(0.5f, scale.Clamp(float.NaN));
        var exposure = ForestOptions.Find("exposure")!;
        Assert.Equal(-0.3f, exposure.Clamp(-0.27f), 5);
        var ssao = ForestOptions.Find("ssao")!;
        Assert.Equal(1f, ssao.Clamp(0.7f));
        Assert.Equal(0f, ssao.Clamp(0.2f));
        var aa = ForestOptions.Find("aa")!;
        Assert.Equal(2f, aa.Clamp(9f));
        Assert.Equal(0f, aa.Clamp(-1f));
        Assert.Equal(1f, aa.Clamp(1.4f));
    }

    [Fact]
    public void ReadoutsDescribeTheValue()
    {
        Assert.Equal("75 %", ForestOptions.Find("render_scale")!.Describe(0.75f));
        Assert.Equal("140 m", ForestOptions.Find("shadow_distance")!.Describe(140f));
        Assert.Equal("+0.5 EV", ForestOptions.Find("exposure")!.Describe(0.5f));
        Assert.Equal("-1.0 EV", ForestOptions.Find("exposure")!.Describe(-1f));
        Assert.Equal("0.0 EV", ForestOptions.Find("exposure")!.Describe(0.01f));
        Assert.Equal("On", ForestOptions.Find("ssao")!.Describe(1f));
        Assert.Equal("FXAA", ForestOptions.Find("aa")!.Describe(1f));
        Assert.Equal("25°", ForestOptions.Find("sun_elevation")!.Describe(25f));
    }

    [Fact]
    public void DefaultsAreTheScenesOwnValuesAndAFreshSettingsFile()
    {
        var world = MenuScene.World(MenuScene.Build(new Node3D()));
        Assert.Equal(0f, world.DefaultOf(ForestOptions.Find("upscaling")!)); // TAAU
        Assert.Equal(0.75f, world.DefaultOf(ForestOptions.Find("render_scale")!));
        Assert.Equal(1f, world.DefaultOf(ForestOptions.Find("shadow_res")!)); // 1024
        Assert.Equal(140f, world.DefaultOf(ForestOptions.Find("shadow_distance")!));
        Assert.Equal(1f, world.DefaultOf(ForestOptions.Find("haze")!)); // 100 % of the look
        Assert.Equal(1f, world.DefaultOf(ForestOptions.Find("wind")!));
        Assert.Equal(1.1f, world.DefaultOf(ForestOptions.Find("ssao_intensity")!), 4);
        Assert.Equal(0f, world.DefaultOf(ForestOptions.Find("exposure")!));
        Assert.Equal(0.15f, world.DefaultOf(ForestOptions.Find("grain")!), 4);
        Assert.Equal(25f, world.DefaultOf(ForestOptions.Find("sun_elevation")!), 3);
        Assert.Equal(25f, world.DefaultOf(ForestOptions.Find("sun_azimuth")!), 3);
        Assert.Equal(90f, world.DefaultOf(ForestOptions.Find("fov")!));
        Assert.Equal(1f, world.DefaultOf(ForestOptions.Find("master")!));
        Assert.Equal(0f, world.DefaultOf(ForestOptions.Find("mute")!));

        // A changed settings file does not move the defaults of what it stores.
        world.Settings.HorizontalFov = 70f;
        Assert.Equal(90f, world.DefaultOf(ForestOptions.Find("fov")!));
    }

    [Fact]
    public void EveryOptionReadsBackWhatWasApplied()
    {
        var world = MenuScene.World(MenuScene.Build(new Node3D()));
        foreach (var option in ForestOptions.All)
        {
            float[] values = option.Kind switch
            {
                ForestOptionKind.Toggle => [0f, 1f, 0f],
                ForestOptionKind.Choice => Enumerable.Range(0, option.Choices.Length).Select(i => (float)i).Reverse().ToArray(),
                _ => [option.Min, option.Max, option.Min + 0.4f * (option.Max - option.Min)],
            };
            foreach (var value in values)
            {
                var applied = world.Apply(option, value);
                Assert.Equal(option.Clamp(value), applied);
                Assert.True(MathF.Abs(applied - world.Value(option)) <= MathF.Max(option.Step, 1e-3f) * 0.51f,
                    $"{option.Key}: applied {applied}, read back {world.Value(option)}");
            }
        }
    }

    [Fact]
    public void OptionsSetTheEnginesRuntimeProperties()
    {
        var scene = MenuScene.Build(new Node3D());
        var world = MenuScene.World(scene);
        var display = (FakeDisplay)world.Display!;
        var sun = scene.GetNode<DirectionalLight3D>("Sun");
        var env = scene.GetNode<WorldEnvironment>("Environment");
        var profile = env.PostProcess!;
        var lens = env.CameraAttributes!;

        void Set(string key, float value) => world.Apply(ForestOptions.Find(key)!, value);

        Set("upscaling", 1);
        Set("render_scale", 0.6f);
        Assert.Equal((Scaling3DMode.Fsr, 0.6f), (display.Scaling3DMode, display.Scaling3DScale));
        Set("upscaling", 2);
        Assert.Equal(1f, display.Scaling3DScale);
        Assert.Equal(0.6f, world.Value(ForestOptions.Find("render_scale")!)); // kept for when an upscaler comes back
        Set("aa", 1);
        Assert.Equal(AntiAliasing.Fxaa, display.AntiAliasing);
        Set("vsync", 0);
        Assert.False(display.VSync);

        Set("shadow_res", 3);
        Assert.Equal(4096, sun.ShadowResolution);
        Set("shadow_distance", 200f);
        Assert.Equal(200f, sun.ShadowMaxDistance);
        Set("pcss", 0);
        Assert.Equal(0f, sun.LightAngularDistance);
        Set("pcss", 1);
        Assert.Equal(0.5f, sun.LightAngularDistance);
        Set("contact_shadows", 0);
        Assert.False(sun.ContactShadows);

        Set("haze", 2f);
        Assert.Equal(0.003f, env.FogDensity, 6);
        Set("volumetric_density", 0.5f);
        Assert.Equal(0.002f, env.VolumetricFogDensity, 6);
        Set("wind", 3f);
        Assert.Equal(1.05f, env.WindStrength, 5);

        Set("ssao", 0);
        Assert.False(profile.SsaoEnabled);
        Set("tonemapper", 1);
        Assert.Equal(Tonemapper.GodotAces, profile.Tonemapper);
        Set("exposure", 1f);
        Assert.Equal(2f, profile.TonemapExposure, 4);
        Set("glow", 0f);
        Assert.False(profile.GlowEnabled);
        Set("glow", 2f);
        Assert.True(profile.GlowEnabled);
        Assert.Equal(0.6f, profile.GlowIntensity, 4);
        Set("grade", 0);
        Assert.False(profile.AdjustmentEnabled);
        Set("light_shafts", 1);
        Assert.True(profile.LightShaftsEnabled);

        Set("dof", 1);
        Set("dof_distance", 20f);
        Assert.True(lens.DofBlurFarEnabled);
        Assert.Equal(20f, lens.DofBlurFarDistance);
        Set("aberration", 0.5f);
        Assert.Equal(2f, lens.ChromaticAberrationIntensity, 4);

        Set("grass_density", 0.5f);
        Assert.Equal(0.5f, world.Terrain!.FoliageDensityScale);

        var probes = world.Probes = new LightProbeVolume { BakeWhenStale = true };
        Set("sun_elevation", 25f); // the baked direction: a stale bake may still re-bake
        Assert.True(probes.BakeWhenStale);
        Set("sun_elevation", 60f);
        Assert.False(probes.BakeWhenStale); // moved on purpose: no background re-bake
        Set("sun_azimuth", 180f);
        var towards = Vector3.Transform(Vector3.UnitZ, sun.Rotation);
        Assert.Equal(MathF.Sin(float.DegreesToRadians(60f)), towards.Y, 3);
        Assert.Equal(MathF.Cos(float.DegreesToRadians(60f)), towards.Z, 3); // due south: +Z

        Set("fps", 0);
        Assert.False(world.Fps!.Visible);
        Set("fov", 100f);
        Assert.Equal(100f, world.Settings.HorizontalFov);
        Set("mute", 1);
        Assert.Equal(0f, world.Settings.Audio.Volume(AudioBusLayout.MasterBus));
    }

    [Fact]
    public void OnlyChangedSceneOptionsAreStoredAndResetRestoresThePage()
    {
        var scene = MenuScene.Build(new Node3D());
        var world = MenuScene.World(scene);
        world.Apply(ForestOptions.Find("ssao")!, 0f);
        world.Apply(ForestOptions.Find("haze")!, 1f); // its default: nothing to store
        world.Apply(ForestOptions.Find("master")!, 0.5f);
        Assert.Equal(["ssao"], world.Settings.Graphics.Keys);
        Assert.Equal(0.5f, world.Settings.Audio.MasterVolume);

        world.Apply(ForestOptions.Find("ssao")!, 1f);
        Assert.Empty(world.Settings.Graphics);

        world.Apply(ForestOptions.Find("shadow_res")!, 3f);
        world.Apply(ForestOptions.Find("exposure")!, -1f);
        world.Reset(ForestOptions.Graphics);
        Assert.Empty(world.Settings.Graphics);
        Assert.Equal(1024, scene.GetNode<DirectionalLight3D>("Sun").ShadowResolution);
        Assert.Equal(1f, scene.GetNode<WorldEnvironment>("Environment").PostProcess!.TonemapExposure, 4);
        Assert.Equal(0.5f, world.Settings.Audio.MasterVolume); // another page

        world.Reset(ForestOptions.Audio);
        Assert.Equal(1f, world.Settings.Audio.MasterVolume);
    }

    [Fact]
    public void SettingsRoundTripThroughTheJsonFileAndApplyAtStart()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("forest-menu").FullName, "settings.json");
        var world = MenuScene.World(MenuScene.Build(new Node3D()));
        world.Apply(ForestOptions.Find("shadow_res")!, 2f);
        world.Apply(ForestOptions.Find("tonemapper")!, 1f);
        world.Apply(ForestOptions.Find("haze")!, 0.5f);
        world.Apply(ForestOptions.Find("mute")!, 1f);
        world.Apply(ForestOptions.Find("water")!, 0.25f);
        world.Settings.Graphics["removed_option"] = 3f; // from an older build
        world.Settings.Save(path);

        var loaded = ForestSettings.LoadOrDefault(path);
        Assert.Equal(world.Settings.Graphics, loaded.Graphics);
        Assert.True(loaded.Audio.Muted);
        Assert.Equal(0.25f, loaded.Audio.WaterVolume);

        // A fresh scene with the loaded file: the saved values apply, the unknown key is dropped.
        var scene = MenuScene.Build(new Node3D());
        var fresh = MenuScene.World(scene, loaded);
        fresh.ApplySaved();
        Assert.Equal(2048, scene.GetNode<DirectionalLight3D>("Sun").ShadowResolution);
        Assert.Equal(Tonemapper.GodotAces, scene.GetNode<WorldEnvironment>("Environment").PostProcess!.Tonemapper);
        Assert.Equal(0.00075f, scene.GetNode<WorldEnvironment>("Environment").FogDensity, 6);
        Assert.DoesNotContain("removed_option", fresh.Settings.Graphics.Keys);
        Assert.Equal(1f, fresh.DefaultOf(ForestOptions.Find("haze")!)); // the defaults are still the scene's

        // Out-of-range values in a hand-edited file are clamped.
        loaded.Graphics["render_scale"] = 9f;
        var clamped = MenuScene.World(MenuScene.Build(new Node3D()), loaded);
        clamped.ApplySaved();
        Assert.Equal(1f, clamped.RenderScale);
    }

    [Fact]
    public void AnOldSettingsFileWithoutGraphicsLoads()
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("forest-menu").FullName, "settings.json");
        File.WriteAllText(path, """{ "HorizontalFov": 80, "Audio": { "MasterVolume": 0.5 } }""");
        var loaded = ForestSettings.LoadOrDefault(path);
        Assert.Equal(80f, loaded.HorizontalFov);
        Assert.Empty(loaded.Graphics);
        Assert.False(loaded.Audio.Muted);
    }

    // ADR 0181's reference-resolution scaling scales dp with the window: a px length (or a px media query) would not follow.
    [Fact]
    public void TheStyleSheetIsAuthoredInDpAndEveryShotHasAThumbnail()
    {
        var content = Path.Combine(AppContext.BaseDirectory, "Content", "UI");
        var rcss = File.ReadAllText(Path.Combine(content, "menu.rcss"));
        Assert.DoesNotMatch(@"\d+(\.\d+)?px\b", rcss);
        Assert.Contains("@media (max-width: 1599dp)", rcss, StringComparison.Ordinal);
        foreach (var shot in ValleyLayout.Shots)
            Assert.True(File.Exists(Path.Combine(content, "shots", shot.Name + ".jpg")), shot.Name);
        var rml = PauseMenu.BuildRml();
        Assert.DoesNotContain("data-visible", rml, StringComparison.Ordinal); // RmlUi's keeps the space: data-class-hidden
    }

    [Theory]
    [InlineData(new string[0], true)]
    [InlineData(new[] { "--no-fps" }, true)]
    [InlineData(new[] { "--shot", "1" }, false)]
    [InlineData(new[] { "--benchmark" }, false)]
    [InlineData(new[] { "--autowalk" }, false)]
    [InlineData(new[] { "--menu", "graphics" }, false)]
    [InlineData(new[] { "--camera", "flyover" }, false)]
    public void SavedSettingsApplyInPlayButNotInCapturesOrMeasurements(string[] args, bool applies) =>
        Assert.Equal(applies, ForestDev.UsesPlayerSettings(args));
}

/// <summary>The Cameras page's views (<see cref="ForestCameras"/>) and the menu's control of the player.</summary>
public sealed class ForestCamerasTests
{
    private static (ControllerHarness Harness, FirstPersonController Player, ForestCameras Cameras) Setup()
    {
        var h = new ControllerHarness();
        var player = h.AddPlayer(new Vector3(64, 0, 200), captureMouse: true);
        var cameras = new ForestCameras { Name = "Cameras", Player = player, GroundHeight = static (_, _) => 0f };
        h.Scene.AddChild(cameras);
        h.Run(2);
        return (h, player, cameras);
    }

    [Fact]
    public void CamerasSwitchBetweenThePlayerTheShotsTheFlyOverAndTheFreeCamera()
    {
        var (h, player, cameras) = Setup();
        using var _ = h;
        Assert.Equal(ForestCameraMode.Player, cameras.Mode);
        var viewport = h.Tree.Root;

        cameras.ShowShot(2);
        Assert.Equal(ForestCameraMode.Shot, cameras.Mode);
        Assert.Same(cameras.CinematicCamera, viewport.ActiveCamera3D);
        Assert.Equal(ProcessMode.Disabled, player.ProcessMode);
        Assert.Equal(MouseMode.Hidden, h.Input.MouseMode);
        var (eye, _) = ForestDev.ShotPose(ValleyLayout.Shots[2], static (_, _) => 0f);
        Assert.Equal(eye, cameras.CinematicCamera!.GlobalPosition);
        Assert.Equal(ValleyLayout.Shots[2].Fov, cameras.CinematicCamera.Fov);

        // Left and right step through the shots (and wrap).
        h.Hold("move_left");
        h.Run(1);
        h.Release("move_left");
        h.Run(1);
        Assert.Equal(1, cameras.ShotIndex);
        cameras.ShowShot(-1);
        Assert.Equal(ValleyLayout.Shots.Length - 1, cameras.ShotIndex);
        Assert.Null(cameras.CinematicCamera.Attributes); // R7 keeps the world's lens…
        cameras.ShowShot(4);
        Assert.True(cameras.CinematicCamera.Attributes!.DofBlurNearEnabled); // …R5 is a photo shot

        cameras.StartFreeFly();
        Assert.Equal(ForestCameraMode.FreeFly, cameras.Mode);
        Assert.Same(cameras.FreeCamera, viewport.ActiveCamera3D);
        Assert.Equal(MouseMode.Captured, h.Input.MouseMode);
        var start = cameras.FreeCamera!.GlobalPosition;
        h.Hold("move_forward");
        h.Run(30);
        h.Release("move_forward");
        Assert.True(Vector3.Distance(start, cameras.FreeCamera.GlobalPosition) > 2.5f);

        cameras.ReturnToPlayer();
        Assert.Same(player.Camera, viewport.ActiveCamera3D);
        Assert.Equal(ProcessMode.Inherit, player.ProcessMode);
        Assert.Equal(ProcessMode.Disabled, cameras.FreeCamera.ProcessMode);
        Assert.Equal(MouseMode.Captured, h.Input.MouseMode);
    }

    [Fact]
    public void TheFlyOverFliesAtASteadySpeedAndLoopsOrHandsBackThePlayer()
    {
        var (h, _, cameras) = Setup();
        using var _ = h;
        cameras.StartFlyOver();
        Assert.Equal(ForestCameraMode.FlyOver, cameras.Mode);
        Assert.InRange(cameras.FlyOverLength, 350f, 700f);
        var camera = cameras.CinematicCamera!;
        var previous = camera.GlobalPosition;
        for (var i = 0; i < 120; i++)
        {
            h.Run(1);
            var step = Vector3.Distance(previous, camera.GlobalPosition);
            Assert.InRange(step, ForestCameras.FlyOverSpeed / 60f * 0.8f, ForestCameras.FlyOverSpeed / 60f * 1.2f);
            previous = camera.GlobalPosition;
        }

        // Loops past its end…
        var loop = (int)(cameras.FlyOverLength / ForestCameras.FlyOverSpeed * 60f);
        h.Run(loop);
        Assert.Equal(ForestCameraMode.FlyOver, cameras.Mode);
        Assert.InRange(cameras.FlyOverDistance, 0f, 30f);

        // …or hands the view back.
        cameras.Loop = false;
        h.Run(loop);
        Assert.Equal(ForestCameraMode.Player, cameras.Mode);
    }

    [Fact]
    public void TheMenuStopsThePlayerAndGivesControlBackToTheView()
    {
        var (h, player, cameras) = Setup();
        using var _ = h;
        cameras.MenuOpen = true;
        Assert.Equal(ProcessMode.Disabled, player.ProcessMode);
        Assert.Equal(MouseMode.Visible, h.Input.MouseMode);
        var feet = player.GlobalPosition;
        h.Hold("move_forward");
        h.Run(30);
        Assert.Equal(feet, player.GlobalPosition);
        cameras.MenuOpen = false;
        Assert.Equal(MouseMode.Captured, h.Input.MouseMode);
        h.Run(30);
        Assert.NotEqual(feet, player.GlobalPosition);
        h.Release("move_forward");

        cameras.StartFlyOver();
        cameras.MenuOpen = true;
        cameras.MenuOpen = false;
        Assert.Equal(MouseMode.Hidden, h.Input.MouseMode); // a cinematic hides the cursor
        Assert.Equal(ProcessMode.Disabled, player.ProcessMode);
    }

    [Fact]
    public void FlyingAndShotsAllocateNothingPerFrame()
    {
        var (h, _, cameras) = Setup();
        using var _ = h;
        cameras.StartFlyOver();
        h.Run(60);
        var before = GC.GetAllocatedBytesForCurrentThread();
        h.Run(300);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}

/// <summary>The menu document over a headless UI server: Escape, the pages, clicks and sliders reach the game.</summary>
[Collection(MenuCollection.Name)]
public sealed class PauseMenuTests
{
    private sealed class MenuHarness : IDisposable
    {
        public MenuHarness(Vector2 viewport)
        {
            H = new ControllerHarness(uiViewport: viewport);
            MenuScene.Build(H.Scene);
            Player = H.AddPlayer(new Vector3(64, 0, 200), captureMouse: true);
            Fps = FpsHud.Attach(H.Scene);
            World = ForestWorld.Capture(H.Scene, new FakeDisplay(), new ForestSettings());
            World.Terrain = new Terrain3D();
            World.CaptureBaselines();
            Cameras = new ForestCameras { Name = "Cameras", Player = Player, GroundHeight = static (_, _) => 0f };
            H.Scene.AddChild(Cameras);
            SettingsPath = Path.Combine(Directory.CreateTempSubdirectory("forest-menu").FullName, "settings.json");
            Menu = PauseMenu.Attach(H.Scene, World, Cameras, SettingsPath);
            H.Run(2);
        }

        public ControllerHarness H { get; }
        public FirstPersonController Player { get; }
        public FpsHud Fps { get; }
        public ForestWorld World { get; }
        public ForestCameras Cameras { get; }
        public PauseMenu Menu { get; }
        public string SettingsPath { get; }

        public void Key(Key key, bool pressed) => H.Tree.PushInput(new InputEventKey { Key = key, Pressed = pressed });

        public void Tap(Key key)
        {
            Key(key, true);
            H.Run(1);
            Key(key, false);
            H.Run(1);
        }

        public void Dispose() => H.Dispose();
    }

    [Fact]
    public void EscapeOpensTheMenuStopsThePlayerAndClosesItAgain()
    {
        using var m = new MenuHarness(new Vector2(1920, 1080));
        Assert.False(m.Menu.IsOpen);
        Assert.False(m.Menu.Layer!.Visible);
        m.H.Input.MouseMode = MouseMode.Captured;

        m.Tap(Key.Escape);
        Assert.True(m.Menu.IsOpen);
        Assert.True(m.Menu.Layer!.Visible);
        Assert.Equal(MouseMode.Visible, m.H.Input.MouseMode);
        Assert.Equal(ProcessMode.Disabled, m.Player.ProcessMode);
        Assert.True(m.Menu.IsLoaded);

        // A held key repeats: still one toggle.
        m.Key(Key.Escape, true);
        m.Key(Key.Escape, true);
        m.H.Run(1);
        m.Key(Key.Escape, false);
        m.H.Run(1);
        Assert.False(m.Menu.IsOpen);
        Assert.Equal(MouseMode.Captured, m.H.Input.MouseMode); // the player's pause did not release it again
        Assert.Equal(ProcessMode.Inherit, m.Player.ProcessMode);

        // Gamepad: Start opens, B closes.
        m.H.Tree.PushInput(new InputEventGamepadButton { Button = ButtonName.Start, Pressed = true });
        m.H.Tree.PushInput(new InputEventGamepadButton { Button = ButtonName.Start, Pressed = false });
        m.H.Run(1);
        Assert.True(m.Menu.IsOpen);
        m.H.Tree.PushInput(new InputEventGamepadButton { Button = ButtonName.B, Pressed = true });
        m.H.Run(1);
        Assert.False(m.Menu.IsOpen);
    }

    [Fact]
    public void ThePagesShowTheirRowsAndClicksApplyOptions()
    {
        using var m = new MenuHarness(new Vector2(1920, 1080));
        m.Menu.Open(ForestOptions.Graphics);
        m.H.Run(2);
        foreach (var option in ForestOptions.All)
            Assert.True(m.Menu.GetElementById("opt-" + option.Key) is { IsValid: true }, option.Key);

        // A segmented choice: ACES (the second button of the tonemapper row).
        m.Menu.QuerySelector("#opt-tonemapper .seg button:nth-child(2)")!.PerformClick();
        m.H.Run(2);
        Assert.Equal(Tonemapper.GodotAces, m.World.Profile!.Tonemapper);
        Assert.True(m.Menu.QuerySelector("#opt-tonemapper .seg button:nth-child(2)")!.IsClassSet("on"));

        // A switch, and the dependent row greys out.
        m.Menu.QuerySelector("#opt-ssao .seg button:nth-child(1)")!.PerformClick();
        m.H.Run(2);
        Assert.False(m.World.Profile.SsaoEnabled);
        Assert.True(m.Menu.GetElementById("opt-ssao_intensity")!.IsClassSet("dim"));

        // A slider: its value reaches the scene and the readout.
        var slider = m.Menu.QuerySelector("#opt-shadow_distance input")!;
        slider.Value = "200";
        m.H.Run(2);
        Assert.Equal(200f, m.World.Sun!.ShadowMaxDistance);
        Assert.Contains("200 m", m.Menu.QuerySelector("#opt-shadow_distance .opt-val")!.InnerRml, StringComparison.Ordinal);

        // The pages switch; Reset restores the page.
        m.Menu.GetElementById("nav-audio")!.PerformClick();
        m.H.Run(2);
        Assert.Equal(ForestOptions.Audio, m.Menu.Page);
        m.Menu.ShowPage(ForestOptions.Graphics);
        m.Menu.GetElementById("reset")!.PerformClick();
        m.H.Run(2);
        Assert.Equal(Tonemapper.Agx, m.World.Profile.Tonemapper);
        Assert.Equal(140f, m.World.Sun.ShadowMaxDistance);

        // Closing saves (once).
        m.Menu.GetElementById("resume")!.PerformClick();
        m.H.Run(2);
        Assert.False(m.Menu.IsOpen);
        Assert.Equal(1, m.Menu.Saves);
        Assert.True(File.Exists(m.SettingsPath));
    }

    [Fact]
    public void TheCamerasPageStartsAViewAndClosesTheMenu()
    {
        using var m = new MenuHarness(new Vector2(1920, 1080));
        m.Menu.Open(PauseMenu.CamerasPage);
        m.H.Run(2);
        m.Menu.GetElementById("shot-4")!.PerformClick();
        m.H.Run(2);
        Assert.False(m.Menu.IsOpen);
        Assert.Equal(ForestCameraMode.Shot, m.Cameras.Mode);
        Assert.Equal(3, m.Cameras.ShotIndex);

        // Escape in a shot comes back to the menu, on the Cameras page.
        m.Tap(Key.Escape);
        Assert.True(m.Menu.IsOpen);
        Assert.Equal(PauseMenu.CamerasPage, m.Menu.Page);
        Assert.Equal("R4 · Vista", PauseMenu.StateOf(m.Cameras));
        m.Menu.GetElementById("cam-flyover")!.PerformClick();
        m.H.Run(2);
        Assert.Equal(ForestCameraMode.FlyOver, m.Cameras.Mode);
        m.Tap(Key.Escape);
        m.Menu.GetElementById("cam-player")!.PerformClick();
        m.H.Run(2);
        Assert.Equal(ForestCameraMode.Player, m.Cameras.Mode);
        Assert.Same(m.Player.Camera, m.H.Tree.Root.ActiveCamera3D);
    }

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1280, 720)]
    public void TheLayoutFitsTheWindow(int width, int height)
    {
        using var m = new MenuHarness(new Vector2(width, height));
        m.Menu.Open(ForestOptions.Graphics);
        m.H.Run(3);
        var panel = m.Menu.GetElementById("panel")!.Bounds;
        Assert.True(panel.X >= 0 && panel.Y >= 0 && panel.X + panel.Width <= width && panel.Y + panel.Height <= height,
            $"panel {panel.X},{panel.Y} {panel.Width}x{panel.Height} in {width}x{height}");
        var quit = m.Menu.GetElementById("quit")!.Bounds;
        Assert.True(quit.Y + quit.Height <= height, $"quit at {quit.Y}");
        // Every row's control stays inside the panel.
        foreach (var option in ForestOptions.OnPage(ForestOptions.Graphics))
        {
            var row = m.Menu.GetElementById("opt-" + option.Key)!.Bounds;
            Assert.True(row.X >= panel.X && row.X + row.Width <= panel.X + panel.Width + 1, $"{option.Key}: {row.X} + {row.Width}");
        }
    }

    [Fact]
    public void AClosedMenuAllocatesNothingPerFrame()
    {
        using var m = new MenuHarness(new Vector2(1280, 720));
        m.Menu.Open();
        m.H.Run(3);
        m.Menu.Close();
        var motion = new InputEventMouseMotion { Relative = new Vector2(1, 0) };
        for (var i = 0; i < 60; i++) // the input path's first events grow its working lists
        {
            m.H.Tree.PushInput(motion);
            m.H.Run(1);
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            m.H.Tree.PushInput(motion);
            m.H.Run(1);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
