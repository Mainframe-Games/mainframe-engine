using System.Text;
using MainframeEngine;

namespace Forest.Tests;

public sealed class ProjectTests
{
    private static string ForestRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static ProjectSettings Load() => ProjectSettings.Load(Path.Combine(AppContext.BaseDirectory, "project.mfproj"));

    [Fact]
    public void ProjectFileNamesTheForestScene()
    {
        var settings = Load();
        Assert.Equal("Forest Demo", settings.Name);
        Assert.Equal(ForestScene.Path, settings.MainScene);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, settings.MainScene!)));
        Assert.Equal((1920, 1080), (settings.Window.Width, settings.Window.Height));
    }

    /// <summary>
    /// ADR 0182: the player-facing name is the project name (window title, macOS bundle "Forest Demo.app", Windows file
    /// description), and the icons exist where project.mfproj and Forest.Desktop.csproj point.
    /// </summary>
    [Fact]
    public void AppIsForestDemoWithItsIcons()
    {
        var settings = Load();
        Assert.Null(settings.Window.Title); // the title falls back to the name
        Assert.Equal("Forest Demo", settings.ToEngineOptions().GameName);
        Assert.Equal("Content/Brand/icon.png", settings.Window.Icon);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, settings.Window.Icon!)));

        var desktop = File.ReadAllText(Path.Combine(ForestRoot, "Forest.Desktop", "Forest.Desktop.csproj"));
        var icns = Property(desktop, "MacAppIcon");
        var ico = Property(desktop, "ApplicationIcon");
        Assert.Equal("../Brand/forest.icns", icns);
        Assert.Equal("../Brand/forest.ico", ico);
        var icnsBytes = File.ReadAllBytes(Path.Combine(ForestRoot, "Forest.Desktop", icns));
        Assert.Equal("icns"u8.ToArray(), icnsBytes[..4]);
        Assert.True(File.Exists(Path.Combine(ForestRoot, "Forest.Desktop", ico)));
        foreach (var svg in new[] { "icon.svg", "icon-small.svg", "icon-macos.svg" })
            Assert.True(File.Exists(Path.Combine(ForestRoot, "Brand", svg)), svg);

        static string Property(string csproj, string name)
        {
            var start = csproj.IndexOf($"<{name}>", StringComparison.Ordinal);
            Assert.True(start >= 0, $"{name} is not set in Forest.Desktop.csproj");
            start += name.Length + 2;
            return csproj[start..csproj.IndexOf($"</{name}>", start, StringComparison.Ordinal)];
        }
    }

    [Theory]
    [InlineData("move_forward")]
    [InlineData("move_back")]
    [InlineData("move_left")]
    [InlineData("move_right")]
    [InlineData("jump")]
    [InlineData("sprint")]
    [InlineData("crouch")]
    [InlineData("pause")]
    public void KeyboardActionsHaveAKey(string action) =>
        Assert.Contains(Load().Input.GetAction(action)!.Bindings, b => b.Kind == InputBindingKind.Key);

    [Theory]
    [InlineData("move_forward")]
    [InlineData("move_left")]
    [InlineData("look_left")]
    [InlineData("look_up")]
    [InlineData("jump")]
    [InlineData("sprint_toggle")]
    [InlineData("crouch_toggle")]
    [InlineData("pause")]
    public void GamepadActionsHaveAPadBinding(string action) =>
        Assert.Contains(Load().Input.GetAction(action)!.Bindings, b => b.Kind is InputBindingKind.GamepadButton or InputBindingKind.GamepadAxis);

    [Fact]
    public void CommittedSceneMatchesTheBuilder()
    {
        var root = ForestScene.Build();
        try
        {
            var committed = File.ReadAllText(Path.Combine(ForestRoot, ForestScene.Path)).ReplaceLineEndings();
            var uid = System.Text.Json.JsonDocument.Parse(committed).RootElement.GetProperty("uid").GetString()!;
            Assert.Equal(committed, Encoding.UTF8.GetString(SceneSaver.ToJson(root, uid)).ReplaceLineEndings());
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void SceneHasTheSunTheLookTheValleyThePlayerAndAudio()
    {
        var root = ForestScene.Build();
        try
        {
            var player = root.GetNode<FirstPersonController>("Player");
            Assert.NotNull(player.GetNode<Camera3D>("Head/Camera"));
            var sun = root.GetNode<DirectionalLight3D>("Sun");
            Assert.True(sun.CastsShadows);
            Assert.Same(sun, root.Children[0]); // the first directional light is the sky's sun
            var environment = root.GetNode<WorldEnvironment>("Environment");
            Assert.Equal(SkyEnvironmentType.Physical, environment.Sky!.Mode);
            Assert.Equal(AmbientSource.Sky, environment.AmbientSource);
            Assert.Equal(ReflectedLightSource.Sky, environment.ReflectedLightSource);
            var post = environment.PostProcess!;
            Assert.True(environment.FogEnabled && post.AutoExposureEnabled && post.GlowEnabled && post.SsaoEnabled);
            Assert.True(environment.VolumetricFogEnabled && !post.LightShaftsEnabled); // ADR 0171: real shafts replace the screen-space ones
            // ADR 0177: shaded views keep their sunlit slopes in range (Brogan's 2026-10-09 playtest).
            Assert.Equal(AutoExposureMode.Histogram, post.AutoExposureMode);
            Assert.True(post.AutoExposureHighlightProtection);
            Assert.Equal(ForestLook.ProfilePath, post.ResourcePath); // ADR 0169: the look is a file the editor tunes
            Assert.Equal(ForestLook.LensPath, environment.CameraAttributes!.ResourcePath);
            Assert.NotNull(root.GetNode<ForestValley>("Valley"));
            Assert.Equal("Valley/Stream", root.GetNode<ForestAudio>("Audio").RiverPath.ToString());

            // A morning sun: low, from the north-north-east (ADR 0178: the walk's first leg looks into it).
            var towardsSun = -sun.GlobalForward;
            Assert.InRange(float.RadiansToDegrees(MathF.Asin(towardsSun.Y)), 18f, 30f);
            Assert.True(towardsSun.Z < -0.7f && towardsSun.X > 0.2f, $"not north-north-east: {towardsSun}");
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void ProjectUsesTaaAndAFixedPixelSize()
    {
        var settings = Load();
        Assert.Equal(AntiAliasing.Taa, settings.Rendering.AntiAliasing); // ADR 0166: leaves and grass stop shimmering
        Assert.Equal(1f, settings.Window.ContentScale);
        // ADR 0174: High renders at 0.75 and TAAU upscales (1440p from 1080p); `++ --scale 1` is native.
        Assert.Equal((Scaling3DMode.Taau, 0.75f), (settings.Rendering.Scaling3DMode, settings.Rendering.Scaling3DScale));
    }

    [Fact]
    public void TheUiScalesWithTheScreenFrom1080p()
    {
        // ADR 0181: the FPS HUD, the menus and the dev overlay are authored at 1920×1080 and grow with the window
        // (2× at 4K); the window is in pixels (contentScale 1), so a Retina display does not change the scale.
        Assert.Equal(UiScaling.ScaleWithScreenSize, Load().Ui.ToScaling());
    }

    [Fact]
    public void TheScaleFlagParses()
    {
        Assert.Equal(1f, ForestDev.ParseScale("1"));
        Assert.Equal(0.5f, ForestDev.ParseScale("0.5"));
        Assert.Null(ForestDev.ParseScale("2"));
        Assert.Null(ForestDev.ParseScale("native"));
    }

    [Fact]
    public void PathWalkerDrivesTheMoveActionsAlongTheLoop()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        var walker = new PathWalker([new(0, 0), new(0, -20), new(0, -40), new(0, 0)], new System.Numerics.Vector2(0, 0));
        for (var i = 0; i < 120; i++)
        {
            walker.Drive(h.Input, player, ControllerHarness.Step);
            h.Run(1);
        }

        Assert.True(h.Input.IsActionPressed("move_forward"));
        Assert.True(player.GlobalPosition.Z < -3f, $"walked to {player.GlobalPosition}");
        Assert.True(walker.Progress > 0);
    }
}
