using MainframeEngine;

namespace Demo.Tests;

[Collection(nameof(SerialRmlUi))]
public sealed class ParticlesSceneTests : IDisposable
{
    private readonly ServerRegistry _servers = new();
    private readonly SceneTree _tree;
    private uint _frame;

    public ParticlesSceneTests()
    {
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false }));
        _tree = new SceneTree(_servers);
        _tree.ChangeScene(ParticlesScene.Build());
    }

    public void Dispose()
    {
        _tree.Shutdown();
        _servers.Dispose();
    }

    private Node Scene => _tree.CurrentScene!;

    private ParticlesPanel Panel => Scene.GetNode<ParticlesPanel>("Ui/Panel");

    private GpuParticles3D Emitter(string path) => Scene.GetNode<GpuParticles3D>(path);

    private void Tick() => TestTime.Tick(_tree, ref _frame);

    [Fact]
    public void TheCampfireAndTheRainAreGpuParticlesWithTheirLooks()
    {
        Tick();
        var flames = Emitter("Campfire/Flames");
        var flameMaterial = Assert.IsType<StandardMaterial3D>(flames.MaterialOverride);
        Assert.Equal(StandardMaterial3D.BlendModeEnum.Add, flameMaterial.BlendMode);
        Assert.True(flameMaterial.VertexColorUseAsAlbedo);
        Assert.NotNull(flameMaterial.AlbedoTexture);
        Assert.True(flames.ProcessMaterial!.Color.X > 1f, "HDR flames, for the glow");

        var smoke = Assert.IsType<StandardMaterial3D>(Emitter("Campfire/Smoke").MaterialOverride);
        Assert.Equal(AlphaMode.Blend, smoke.Transparency);
        Assert.Equal(ShadingMode.BlinnPhong, smoke.ShadingMode); // lit by the fire
        Assert.Equal(GpuParticles3D.DrawOrderEnum.ViewDepth, Emitter("Campfire/Smoke").DrawOrder);

        var rain = Emitter("Rain");
        Assert.True(rain.ProcessMaterial!.ParticleFlagAlignY);
        Assert.Equal(BillboardMode.Particles, Assert.IsType<StandardMaterial3D>(rain.MaterialOverride).BillboardMode);

        // Without a renderer (this test, a dedicated server) the emitters are inert.
        Assert.Equal(0, flames.LiveCount);
    }

    [Fact]
    public void ThePanelTogglesTheRainAndTheGlowAndFiresTheSparks()
    {
        Tick();
        var environment = Scene.GetNode<WorldEnvironment>("Environment");
        Assert.True(Panel.Rain);
        Assert.True(Panel.Glow);

        Panel.Rain = false;
        Panel.Glow = false;
        Assert.False(Emitter("Rain").Emitting);
        Assert.False(environment.PostProcess!.GlowEnabled);

        var sparks = Emitter("Campfire/Sparks");
        Assert.False(sparks.Emitting);
        Assert.True(sparks.OneShot);
        Panel.Burst();
        Assert.True(sparks.Emitting);
    }
}
