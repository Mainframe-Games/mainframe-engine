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
        Assert.Equal((1600, 900), (settings.Window.Width, settings.Window.Height));
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
    public void SceneHasThePlayerTheStreamAndASun()
    {
        var root = ForestScene.Build();
        try
        {
            var player = root.GetNode<FirstPersonController>("Player");
            Assert.NotNull(player.GetNode<Camera3D>("Head/Camera"));
            Assert.True(root.GetNode<DirectionalLight3D>("Sun").CastsShadows);
            Assert.Equal(SkyEnvironmentType.Procedural, root.GetNode<WorldEnvironment>("Environment").Sky!.Mode);
            var stream = root.GetNode<River3D>("Stream");
            Assert.True(stream.Length > 60f);
            Assert.True(stream.MeshData.VertexCount > 0);
        }
        finally
        {
            root.Free();
        }
    }

    [Fact]
    public void TheStreamIsWadeableInTheTrench()
    {
        using var h = new ControllerHarness(floor: false);
        var scene = ForestScene.Build();
        h.Tree.ChangeScene(scene);
        var x = (ForestScene.TrenchMinX + ForestScene.TrenchMaxX) / 2;
        var bed = new System.Numerics.Vector3(x, -ForestScene.TrenchDepth, 0);
        var water = h.Tree.Root.World3D.Water;
        Assert.InRange(water.ImmersionAt(bed), 0.9f, ForestScene.TrenchDepth);
        Assert.True(WaterQueries.WadeSpeedScale(water.ImmersionAt(bed)) < 0.6f);
        Assert.Equal(0f, water.WaterDepthAt(ForestScene.Spawn));
    }

    [Fact]
    public void AutoWalkDrivesTheMoveActions()
    {
        using var h = new ControllerHarness();
        var player = h.AddPlayer();
        ForestDev.Drive(h.Input, player, 0.5f, ControllerHarness.Step);
        Assert.True(h.Input.IsActionPressed("move_forward"));
        Assert.False(h.Input.IsActionPressed("move_back"));
        h.RunSeconds(1f);
        Assert.True(player.GlobalPosition.Z < -0.5f);
        Assert.True(ForestDev.AutoWalkPath.Sum(l => l.Seconds) > 15f);
    }
}
