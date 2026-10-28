using System.Numerics;
using System.Text;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Navigation;

/// <summary>G3.3: <see cref="NavigationGridData"/>'s compact RLE strings, through a <c>.mres</c> and inline in a scene.</summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class NavigationGridDataTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-nav-" + Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public NavigationGridDataTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder, "Levels"));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previous;
        Directory.Delete(_project, recursive: true);
    }

    private static AStarGrid2D RandomGrid(int seed, int width = 200, int height = 120)
    {
        var grid = GridMaps.Random(seed, width, height, 0.3, weights: true);
        grid.FillWeightScaleRegion(new Rect2I(10, 10, 40, 3), 2.5f);
        grid.SetPointWeightScale(new Vector2I(width - 1, height - 1), 0f);
        return grid;
    }

    private static void AssertSameCells(AStarGrid2D expected, AStarGrid2D actual)
    {
        Assert.Equal(expected.Region, actual.Region);
        for (var y = 0; y < expected.Region.Size.Y; y++)
        {
            for (var x = 0; x < expected.Region.Size.X; x++)
            {
                var c = new Vector2I(x, y);
                Assert.Equal(expected.IsPointSolid(c), actual.IsPointSolid(c));
                Assert.Equal(expected.GetPointWeightScale(c), actual.GetPointWeightScale(c));
            }
        }
    }

    [Fact]
    public void TheCodecRoundTripsSolidCellsAndWeights()
    {
        foreach (var seed in new[] { 1, 2, 3 })
        {
            var grid = RandomGrid(seed);
            var data = NavigationGridData.FromGrid(grid, NavigationGridSource.Physics);
            Assert.StartsWith("rle1:", data.Solid, StringComparison.Ordinal);
            Assert.StartsWith("rle1:", data.Weights, StringComparison.Ordinal);
            Assert.Equal(grid.SolidCount, data.SolidCount);
            var copy = new AStarGrid2D { Region = grid.Region };
            copy.Update();
            Assert.True(data.TryApply(copy, out var error), error);
            AssertSameCells(grid, copy);
        }

        // Edge cases: all walkable, all solid, the first cell solid.
        var empty = new AStarGrid2D { Region = new Rect2I(0, 0, 5, 5) };
        empty.Update();
        Assert.Equal("", NavigationGridData.FromGrid(empty).Solid);
        Assert.Equal("", NavigationGridData.FromGrid(empty).Weights);
        var full = new AStarGrid2D { Region = new Rect2I(0, 0, 5, 5) };
        full.Update();
        full.FillSolidRegion(full.Region);
        var fullCopy = new AStarGrid2D { Region = full.Region };
        fullCopy.Update();
        Assert.True(NavigationGridData.FromGrid(full).TryApply(fullCopy, out _));
        Assert.Equal(25, fullCopy.SolidCount);
    }

    [Fact]
    public void AResourceFileStaysCompactAndLoadsBack()
    {
        var grid = RandomGrid(9, 256, 256);
        var data = NavigationGridData.FromGrid(grid, NavigationGridSource.Physics);
        data.SourceHash = "0123456789abcdef";
        ResourceSaver.Save(data, "Assets/Levels/maze-nav.mres");
        var text = File.ReadAllText(Path.Combine(_project, "Assets", "Levels", "maze-nav.mres"));
        Assert.True(text.Split('\n').Length < 30, "a 256 × 256 grid is a handful of lines");
        Assert.Contains("\"type\": \"NavigationGridData\"", text, StringComparison.Ordinal);
        Assert.Contains("\"BakedFrom\": \"Physics\"", text, StringComparison.Ordinal);

        ResourceLoader.ClearCache();
        var loaded = ResourceLoader.Load<NavigationGridData>("Assets/Levels/maze-nav.mres");
        Assert.Equal(grid.Region, loaded.Region);
        Assert.Equal("0123456789abcdef", loaded.SourceHash);
        var copy = new AStarGrid2D { Region = loaded.Region };
        copy.Update();
        Assert.True(loaded.TryApply(copy, out _));
        AssertSameCells(grid, copy);
        loaded.Release();
    }

    [Fact]
    public void AGridSceneKeepsItsDataInlineAndAppliesItOnLoad()
    {
        var root = new Node2D { Name = "Level" };
        var source = new NavigationGrid2D { Name = "Nav", Region = new Rect2I(0, 0, 16, 8), CellSize = new Vector2(24), JumpingEnabled = true };
        source.SetCellSolid(new Vector2I(3, 3), true);
        source.SetCellWeight(new Vector2I(4, 4), 3f);
        source.Data = NavigationGridData.FromGrid(source.Grid, NavigationGridSource.Physics);
        root.AddChild(source);
        source.Owner = root;
        root.AddChild(new NavigationAgent2D { Name = "Agent", TargetDesiredDistance = 4f, PathPostprocessing = NavigationPathPostprocessing.EdgeCentered });
        root.GetChild(1).Owner = root;
        var json = SceneSaver.ToJson(root);
        root.Free();

        var copy = PackedScene.Parse(json).Instantiate();
        var nav = copy.GetNode<NavigationGrid2D>("Nav");
        Assert.True(nav.JumpingEnabled);
        Assert.Equal(new Vector2(24), nav.CellSize);
        Assert.True(nav.Grid.IsPointSolid(new Vector2I(3, 3)));
        Assert.Equal(3f, nav.Grid.GetPointWeightScale(new Vector2I(4, 4)));
        var agent = copy.GetNode<NavigationAgent2D>("Agent");
        Assert.Equal(4f, agent.TargetDesiredDistance);
        Assert.Equal(NavigationPathPostprocessing.EdgeCentered, agent.PathPostprocessing);
        Assert.Contains("\"Solid\": \"rle1:", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        copy.Free();
    }

    [Fact]
    public void CorruptOrMismatchedDataLeavesTheGridWalkableWithAWarning()
    {
        var sink = new MemoryLogSink { Levels = Log.Level.Warning };
        Log.AddSink(sink);
        try
        {
            var grid = new NavigationGrid2D { Name = "Broken", Region = new Rect2I(0, 0, 4, 4) };
            grid.Data = new NavigationGridData { Region = new Rect2I(0, 0, 4, 4), Solid = "rle1:!!!not base64" };
            Assert.Equal(0, grid.Grid.SolidCount);
            grid.Data = new NavigationGridData { Region = new Rect2I(0, 0, 4, 4), Solid = "rle1:" + Convert.ToBase64String([0, 200]) };
            Assert.Equal(0, grid.Grid.SolidCount); // runs past the region
            var good = new AStarGrid2D { Region = new Rect2I(0, 0, 5, 5) };
            good.Update();
            good.SetPointSolid(Vector2I.One);
            grid.Data = NavigationGridData.FromGrid(good); // baked for another region
            Assert.Equal(0, grid.Grid.SolidCount);
            grid.Dispose();
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        var warnings = sink.Snapshot().Where(e => e.Message.Contains("'Broken' ignores its navigation data", StringComparison.Ordinal)).ToList();
        Assert.Equal(3, warnings.Count);
        Assert.Contains(warnings, w => w.Message.Contains("not valid rle1", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("run past the region", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Message.Contains("baked for region", StringComparison.Ordinal));
    }
}
