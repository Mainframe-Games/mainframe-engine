using System.Text;
using MainframeEngine;

namespace Neon.Tests;

public sealed class ProjectTests
{
    private static string NeonRoot => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static ProjectSettings Load() => ProjectSettings.Load(Path.Combine(AppContext.BaseDirectory, "project.mfproj"));

    [Fact]
    public void ProjectFileNamesTheNeonScene()
    {
        var settings = Load();
        Assert.Equal("Neon Demo", settings.Name);
        Assert.Equal("Neon Demo", settings.ToEngineOptions().GameName);
        Assert.Equal(NeonScene.Path, settings.MainScene);
        Assert.True(File.Exists(Path.Combine(AppContext.BaseDirectory, settings.MainScene!)));
        Assert.Equal(AntiAliasing.Taa, settings.Rendering.AntiAliasing);
    }

    [Theory]
    [InlineData("move_forward")]
    [InlineData("move_back")]
    [InlineData("move_left")]
    [InlineData("move_right")]
    [InlineData("jump")]
    [InlineData("sprint")]
    [InlineData("crouch")]
    [InlineData("interact")]
    [InlineData("pause")]
    [InlineData("photo")]
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
    [InlineData("interact")]
    [InlineData("pause")]
    [InlineData("photo")]
    public void GamepadActionsHaveAPadBinding(string action) =>
        Assert.Contains(Load().Input.GetAction(action)!.Bindings, b => b.Kind is InputBindingKind.GamepadButton or InputBindingKind.GamepadAxis);

    [Fact]
    public void ThereIsNoWading() => Assert.Null(Load().Input.GetAction("wade"));

    [Fact]
    public void CommittedSceneMatchesTheBuilder()
    {
        var root = NeonScene.Build();
        try
        {
            var committed = File.ReadAllText(Path.Combine(NeonRoot, NeonScene.Path)).ReplaceLineEndings();
            var uid = System.Text.Json.JsonDocument.Parse(committed).RootElement.GetProperty("uid").GetString()!;
            Assert.Equal(committed, Encoding.UTF8.GetString(SceneSaver.ToJson(root, uid)).ReplaceLineEndings());
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void SceneHasTheMoonTheNightTheApartmentThePlayerAndTheSafetyNet()
    {
        var root = NeonScene.Build();
        try
        {
            Assert.IsType<DirectionalLight3D>(root.Children[0]); // the first directional light is the sky's sun
            var environment = root.GetNode<WorldEnvironment>("Environment");
            Assert.Equal(SkyEnvironmentType.Procedural, environment.Sky!.Mode);
            Assert.True(environment.Sky.SkyColor.Length() < 0.1f, "not a night sky");
            Assert.NotNull(root.GetNode<NeonApartment>("Apartment"));
            var player = root.GetNode<FirstPersonController>("Player");
            Assert.NotNull(player.GetNode<Camera3D>("Head/Camera"));
            Assert.NotNull(player.GetNode<Interactor>("Interactor"));
            Assert.Equal(NeonScene.Spawn, player.Position);
            var safety = root.GetNode<FallSafety>("FallSafety");
            Assert.Equal(NeonScene.Spawn, safety.RespawnPosition);
            Assert.NotNull(safety.GetNode<CollisionShape3D>("Shape"));
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void TheViewFlagParses()
    {
        Assert.Equal((new System.Numerics.Vector3(1f, 3f, -2.5f), 90f, -10f), NeonDev.ParseView("1,3,-2.5,90,-10"));
        Assert.Equal(0f, NeonDev.ParseView("1,0,2,180")!.Value.Pitch);
        Assert.Null(NeonDev.ParseView("1,2,3"));
        Assert.Null(NeonDev.ParseView("1,2,x,4"));
    }
}
