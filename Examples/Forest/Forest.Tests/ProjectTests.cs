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
        Assert.Equal("Mainframe Forest", settings.Name);
        Assert.Equal(ForestScene.Path, settings.MainScene);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, settings.MainScene!)));
        Assert.Equal((1920, 1080), (settings.Window.Width, settings.Window.Height));
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
            Assert.True(environment.FogEnabled && post.LightShaftsEnabled && post.AutoExposureEnabled && post.GlowEnabled && post.SsaoEnabled);
            Assert.Equal(ForestLook.ProfilePath, post.ResourcePath); // ADR 0169: the look is a file the editor tunes
            Assert.Equal(ForestLook.LensPath, environment.CameraAttributes!.ResourcePath);
            Assert.NotNull(root.GetNode<ForestValley>("Valley"));
            Assert.Equal("Valley/Stream", root.GetNode<ForestAudio>("Audio").RiverPath.ToString());

            // A morning sun: low, from the east-south-east.
            var towardsSun = -sun.GlobalForward;
            Assert.InRange(float.RadiansToDegrees(MathF.Asin(towardsSun.Y)), 18f, 30f);
            Assert.True(towardsSun.X > 0.8f);
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
