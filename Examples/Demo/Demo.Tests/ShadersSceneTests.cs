using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class ShadersSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public ShadersSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(ShadersScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private ShadersPanel Panel => Scene.GetNode<ShadersPanel>("Ui/Panel");

    private ShaderMaterial Material(string path) => (ShaderMaterial)Scene.GetNode<GeometryInstance3D>(path).MaterialOverride!;

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    [Fact]
    public void TheGroundStalksAndPropsUseSpatialShaders()
    {
        Tick();
        Assert.NotNull(Material("Ground").Shader?.Spatial);
        var stalks = Scene.GetNode<MultiMeshInstance3D>("Stalks");
        Assert.NotNull(Material("Stalks").Shader?.Spatial);
        Assert.True(stalks.Multimesh!.UseCustomData);
        Assert.NotEqual(stalks.Multimesh.GetInstanceCustomData(0), stalks.Multimesh.GetInstanceCustomData(1));
        foreach (var prop in ShadersScene.Props)
        {
            var material = Material("Props/" + prop.Name);
            Assert.Equal(AlphaMode.Cutout, material.RenderState.Alpha); // the dissolve's alpha scissor
            Assert.IsType<float>(material.GetShaderParameter("progress"));
        }
    }

    [Fact]
    public void ThePanelDrivesTheUniforms()
    {
        Tick();
        Panel.Wind = 0.9f;
        Assert.Equal(0.9f, Material("Stalks").GetShaderParameter("sway"));

        Panel.Animate = false;
        Panel.Dissolve = 0.5f;
        Tick();
        Assert.Equal(0.5f * 1.02f, (float)Material("Props/Crate").GetShaderParameter("progress")!, 5);

        Panel.Hit();
        Tick();
        var flash = (Vector4)Material("Props/Pod").GetShaderParameter("hit_flash")!;
        Assert.True(flash.W > 0.5f, $"the flash should be strong right after the hit ({flash.W})");
        for (var i = 0; i < 60; i++)
            Tick();
        Assert.Equal(0f, ((Vector4)Material("Props/Pod").GetShaderParameter("hit_flash")!).W);
    }
}
