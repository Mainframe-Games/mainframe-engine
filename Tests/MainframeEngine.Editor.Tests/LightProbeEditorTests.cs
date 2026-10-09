using System.Numerics;

namespace MainframeEngine.Editor.Tests;

/// <summary>ADR 0170 in the editor: the <see cref="LightProbeVolume"/> inspector's Bake Lighting button and status.</summary>
[Collection(nameof(SerialEditor))]
public sealed class LightProbeEditorTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    [Fact]
    public void BakeLightingBakesInTheBackgroundAndSetsTheDataAsOneUndo()
    {
        var floor = new MeshInstance3D { Name = "Floor", Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) }, Position = new Vector3(0f, -3f, 0f) };
        var volume = new LightProbeVolume { Name = "Probes", Size = new Vector3(4f), ProbeSpacing = new Vector3(2f), RaysPerProbe = 32, Bounces = 1 };
        _editor.Scene.AddNode(floor, _editor.Scene.Root);
        _editor.Scene.AddNode(volume, _editor.Scene.Root);
        _editor.Tick();

        var inspector = CustomInspectors.Find(typeof(LightProbeVolume));
        Assert.IsType<LightProbeVolumeInspector>(inspector);
        Assert.Contains("probes-bake", inspector!.GetHeaderRml(volume));
        Assert.StartsWith("Not baked", LightProbeVolumeInspector.Status(volume));

        _editor.Scene.Selection.Set(volume);
        _editor.Tick();
        _editor.Workspace.Inspector.RunHeaderAction("probes-bake");
        Assert.True(volume.IsBaking || volume.Data is not null);
        for (var i = 0; i < 600 && volume.Data is null; i++)
        {
            Thread.Sleep(5);
            _editor.Tick();
        }

        Assert.False(volume.IsBaking);
        Assert.Null(volume.LastBakeError);
        Assert.NotNull(volume.Data);
        Assert.Equal(27, volume.Data!.Grid.ProbeCount);
        Assert.StartsWith("Baked", LightProbeVolumeInspector.Status(volume));
        _editor.Scene.History.Undo();
        Assert.Null(volume.Data);
        _editor.Scene.History.Redo();
        Assert.NotNull(volume.Data);

        floor.Position = new Vector3(0f, -2f, 0f);
        Assert.StartsWith("Stale", LightProbeVolumeInspector.Status(volume));
    }

    [Fact]
    public void TheDataGoesNextToTheScene()
    {
        Assert.Null(LightProbeVolumeInspector.TargetPath(null));
        Assert.Equal("Content/Scenes/forest-lighting.mres", LightProbeVolumeInspector.TargetPath("Content/Scenes/forest.mscene")?.Replace('\\', '/'));
    }
}
