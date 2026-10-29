using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// G2b's tile map frame cost (docs/design/tile-maps.md): three full-screen layers of 16 px tiles (a 512 × 512 ground, a
/// sparse decoration layer, a y-sorted object layer) in a 1920 × 1080 view, scrolled every frame.
/// <see cref="TileMapLayerFrameBuild"/> is one canvas frame build: chunk culling, the y-sort and the CPU vertex transform
/// of every visible tile (about 8 200 per full layer).
/// </summary>
[MemoryDiagnoser]
public class TileMapBenchmarks : IDisposable
{
    private SceneTree _tree = null!;
    private CanvasServer _server = null!;
    private Node2D _world = null!;
    private int _frame;

    [GlobalSetup]
    public void Setup()
    {
        _tree = new SceneTree(new ServerRegistry());
        _server = new CanvasServer(_tree, () => new Vector2(1920, 1080));
        _world = new Node2D { Name = "World" };
        _tree.ChangeScene(_world);
        var source = new TileSetAtlasSource { Texture = Texture2D.FromPixels(64, 64, new byte[64 * 64 * 4]), UseTexturePadding = false };
        for (var y = 0; y < 4; y++)
            for (var x = 0; x < 4; x++)
                source.CreateTile(new Vector2I(x, y));
        var set = new TileSet();
        set.AddSource(source);

        var ground = new TileMapLayer { Name = "Ground", TileSet = set };
        var decor = new TileMapLayer { Name = "Decor", TileSet = set };
        var objects = new TileMapLayer { Name = "Objects", TileSet = set, YSortEnabled = true };
        for (var y = 0; y < 512; y++)
            for (var x = 0; x < 512; x++)
            {
                ground.SetCell(new Vector2I(x, y), 0, new Vector2I((x * 7 + y) % 4, (x + y * 3) % 4));
                if ((x * 31 + y * 17) % 5 == 0)
                    decor.SetCell(new Vector2I(x, y), 0, new Vector2I(1, 2));
                if ((x * 13 + y * 7) % 11 == 0)
                    objects.SetCell(new Vector2I(x, y), 0, new Vector2I(3, 3));
            }

        _world.AddChild(ground);
        _world.AddChild(decor);
        _world.AddChild(objects);
        for (var i = 0; i < 60; i++)
            TileMapLayerFrameBuild();
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _server?.Dispose();
        _tree?.Shutdown();
        GC.SuppressFinalize(this);
    }

    [Benchmark]
    public int TileMapLayerFrameBuild()
    {
        _frame++;
        _tree.Root.CanvasTransform = Transform2D.FromTrs(new Vector2(-(_frame * 13 % 6000), -(_frame * 7 % 6000)), 0, Vector2.One);
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        return _server.Frame.VertexCount;
    }
}
