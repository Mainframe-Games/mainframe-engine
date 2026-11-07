using System.Numerics;

namespace MainframeEngine.Editor.Tests;

/// <summary>G8e.1 (ADR 0275): the View menu's debug views on the scene's view, and the light probe overlay.</summary>
[Collection(nameof(SerialEditor))]
public sealed class ProbeDebugViewTests : IDisposable
{
    private readonly HeadlessEditor _editor = new();

    public void Dispose() => _editor.Dispose();

    private EditorWorkspace W => _editor.Workspace;

    [Fact]
    public void DebugViewsAreOneAtATimeOnTheScenesView()
    {
        _editor.Tick(2);
        var items = W.Commands.MenuItems("view");
        Assert.Contains(items, i => i.Command == "view.debug.indirect" && i.Icon == "sun-low");
        Assert.Contains(items, i => i.Command == "view.probes" && i.Label == "Show Light Probes");

        Assert.True(W.Commands.Execute("view.debug.indirect"));
        _editor.Tick();
        Assert.Equal(RenderDebugView.IndirectLight, W.Viewport.DebugView);
        Assert.Equal(RenderDebugView.IndirectLight, _editor.Scene.Viewport.DebugView);
        Assert.Contains(W.Commands.MenuItems("view"), i => i.Command == "view.debug.indirect" && i.Icon == "check");

        W.Commands.Execute("view.debug.ao"); // another view replaces it
        _editor.Tick();
        Assert.Equal(RenderDebugView.AmbientOcclusion, _editor.Scene.Viewport.DebugView);

        W.Commands.Execute("view.debug.ao"); // the active one again: off
        _editor.Tick();
        Assert.Equal(RenderDebugView.None, _editor.Scene.Viewport.DebugView);
    }

    [Fact]
    public void ShowLightProbesDrawsTheBakedProbesNearTheCamera()
    {
        var volume = new LightProbeVolume { Name = "Probes", Data = Data(out var grid) };
        _editor.Scene.AddNode(volume, _editor.Scene.Root);
        _editor.Tick(2);
        Assert.Equal(0, W.Viewport.ProbesDrawn);

        W.Commands.Execute("view.probes");
        _editor.Tick();
        Assert.True(W.Viewport.ProbesVisible);
        var near = W.Viewport.ProbesDrawn;
        Assert.True(near > 0, "no probes drawn");
        Assert.True(near <= grid.ProbeCount);
        Assert.Contains(W.Commands.MenuItems("view"), i => i.Command == "view.probes" && i.Label == "Hide Light Probes");

        W.Commands.Execute("view.probes");
        _editor.Tick();
        Assert.Equal(0, W.Viewport.ProbesDrawn);
    }

    [Fact]
    public void TheOverlayKeepsToItsRadiusAndBudgetAndColoursBySkyVisibility()
    {
        var data = Data(out var grid);
        var lines = new DebugLines();
        var camera = grid.Position(10, 0, 10, 0f);

        var drawn = LightProbeOverlay.DrawVolume(data, 1f, camera, lines, int.MaxValue);
        var expected = 0;
        for (var z = 0; z < grid.CountZ; z++)
            for (var y = 0; y < grid.CountY; y++)
                for (var x = 0; x < grid.CountX; x++)
                    if (Vector3.Distance(grid.Position(x, y, z, 0f), camera) <= LightProbeOverlay.Radius)
                        expected++;
        Assert.Equal(expected, drawn);
        Assert.True(drawn < grid.ProbeCount); // the far corners are out of reach
        Assert.Equal(3 * drawn, lines.LineCount);

        lines.Clear();
        Assert.Equal(50, LightProbeOverlay.DrawVolume(data, 1f, camera, lines, 50));

        // Open sky reads white, a hidden sky black; the bounce tints it (× energy).
        Span<float> probe = stackalloc float[LightProbeData.CoefficientsPerProbe];
        probe[0] = 1f / SphericalHarmonics.Y0; // visibility 1 everywhere
        Assert.Equal(new Vector4(1f, 1f, 1f, 1f), LightProbeOverlay.ProbeColor(probe, 1f));
        probe.Clear();
        Assert.Equal(new Vector4(0f, 0f, 0f, 1f), LightProbeOverlay.ProbeColor(probe, 1f));
        probe[4] = 0.4f / SphericalHarmonics.Y0; // red bounce 0.4
        var tinted = LightProbeOverlay.ProbeColor(probe, 2f);
        Assert.Equal(0.8f, tinted.X, 3);
        Assert.Equal(0f, tinted.Y);
    }

    // A 3 m box grid of 24 × 3 × 24 probes from the origin: visibility falling off along +x, no bounce.
    private static LightProbeData Data(out ProbeGrid grid)
    {
        grid = new ProbeGrid(ProbeLayout.Box, Vector3.Zero, new Vector3(3f, 3f, 3f), 24, 3, 24, []);
        var coefficients = new float[grid.ProbeCount * LightProbeData.CoefficientsPerProbe];
        for (var z = 0; z < grid.CountZ; z++)
            for (var y = 0; y < grid.CountY; y++)
                for (var x = 0; x < grid.CountX; x++)
                    coefficients[grid.Index(x, y, z) * LightProbeData.CoefficientsPerProbe] = (1f - x / 24f) / SphericalHarmonics.Y0;
        var data = new LightProbeData();
        data.SetData(grid, coefficients, new float[grid.ColumnCount]);
        return data;
    }
}
