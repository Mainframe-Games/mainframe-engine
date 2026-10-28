using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// G2b: <see cref="TileMapLayer"/> cell storage in chunks (negative coordinates, removal, re-chunking), Godot's
/// coordinate maths, used cells and rects, transform flags, invalid tiles, chunk drawing through canvas sub-items
/// (culling, order, y-sorted rows, batching, flips) and the tile set's data (padding, opaque regions, custom data).
/// </summary>
public sealed class TileMapLayerTests : IDisposable
{
    private readonly SceneTree _tree = new(new ServerRegistry());
    private readonly CanvasServer _server;
    private readonly Node2D _root = new() { Name = "Root" };

    public TileMapLayerTests()
    {
        _server = new CanvasServer(_tree, () => new Vector2(320, 240));
        _tree.ChangeScene(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        _tree.Shutdown();
    }

    // A 4×2 atlas of 16 px tiles; every tile a solid colour (red, green, blue, white / ...), all created.
    internal static TileSet MakeTileSet(bool padding = false)
    {
        var pixels = new byte[64 * 32 * 4];
        for (var y = 0; y < 32; y++)
            for (var x = 0; x < 64; x++)
            {
                var tile = x / 16 + y / 16 * 4;
                var i = (y * 64 + x) * 4;
                pixels[i] = (byte)(tile * 30);
                pixels[i + 1] = (byte)(255 - tile * 30);
                pixels[i + 2] = (byte)(x % 16 * 16);
                pixels[i + 3] = 255;
            }

        var source = new TileSetAtlasSource { Texture = Texture2D.FromPixels(64, 32, pixels), UseTexturePadding = padding };
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 4; x++)
                source.CreateTile(new Vector2I(x, y));
        var set = new TileSet();
        set.AddSource(source);
        return set;
    }

    private TileMapLayer AddLayer(TileSet? set = null, string name = "Layer")
    {
        var layer = new TileMapLayer { Name = name, TileSet = set ?? MakeTileSet() };
        _root.AddChild(layer);
        return layer;
    }

    private List<CulledCanvasItem> Cull(Rect2? clip = null)
    {
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        var culled = new List<CulledCanvasItem>();
        new CanvasCuller().Cull(_tree.Root.RootCanvas, Transform2D.Identity, clip ?? new Rect2(-100000, -100000, 200000, 200000), culled);
        return culled;
    }

    [Fact]
    public void CellsAreStoredInChunksAndEmptyChunksAreRemoved()
    {
        var layer = AddLayer();
        layer.SetCell(new Vector2I(0, 0), 0, new Vector2I(1, 0));
        layer.SetCell(new Vector2I(-1, -1), 0, new Vector2I(2, 1), TileSetAtlasSource.TransformFlipH);
        layer.SetCell(new Vector2I(-17, 40), 0, new Vector2I(0, 0));
        Assert.Equal(3, layer.UsedCellCount);
        Assert.Equal(3, layer.ChunkCount); // (0,0), (-1,-1), (-2,2)

        Assert.Equal(0, layer.GetCellSourceId(new Vector2I(0, 0)));
        Assert.Equal(new Vector2I(2, 1), layer.GetCellAtlasCoords(new Vector2I(-1, -1)));
        Assert.Equal(TileSetAtlasSource.TransformFlipH, layer.GetCellAlternativeTile(new Vector2I(-1, -1)));
        Assert.True(layer.IsCellFlippedH(new Vector2I(-1, -1)));
        Assert.False(layer.IsCellFlippedV(new Vector2I(-1, -1)));
        Assert.Equal(-1, layer.GetCellSourceId(new Vector2I(5, 5)));
        Assert.Equal(new Vector2I(-1, -1), layer.GetCellAtlasCoords(new Vector2I(5, 5)));
        Assert.Equal(-1, layer.GetCellAlternativeTile(new Vector2I(5, 5)));
        Assert.Null(layer.GetCellTileData(new Vector2I(5, 5)));
        Assert.NotNull(layer.GetCellTileData(new Vector2I(0, 0)));

        layer.EraseCell(new Vector2I(-1, -1));
        layer.SetCell(new Vector2I(-17, 40)); // source -1 erases
        Assert.Equal(1, layer.UsedCellCount);
        Assert.Equal(1, layer.ChunkCount);
        layer.Clear();
        Assert.Equal((0, 0), (layer.UsedCellCount, layer.ChunkCount));
        Assert.Throws<ArgumentOutOfRangeException>(() => layer.SetCell(new Vector2I(40000, 0), 0, Vector2I.Zero));
    }

    [Fact]
    public void ChangedFiresForEveryCellEdit()
    {
        var layer = AddLayer();
        var changes = 0;
        layer.Changed += () => changes++;
        layer.SetCell(new Vector2I(1, 1), 0, Vector2I.Zero);
        layer.SetCell(new Vector2I(1, 1), 0, Vector2I.Zero); // same cell: no change
        layer.EraseCell(new Vector2I(1, 1));
        Assert.Equal(2, changes);
    }

    [Fact]
    public void CoordinatesFollowGodot()
    {
        var layer = AddLayer();
        Assert.Equal(new Vector2(8, 8), layer.MapToLocal(Vector2I.Zero));
        Assert.Equal(new Vector2(-8, 24), layer.MapToLocal(new Vector2I(-1, 1)));
        Assert.Equal(new Vector2I(0, 0), layer.LocalToMap(new Vector2(15.9f, 0)));
        Assert.Equal(new Vector2I(-1, -1), layer.LocalToMap(new Vector2(-0.1f, -16)));
        Assert.Equal(new Vector2I(-2, 1), layer.LocalToMap(new Vector2(-16.5f, 16)));
        Assert.Equal(new Vector2I(3, 4), TileMapLayer.GetNeighborCell(new Vector2I(3, 3), TileCellNeighbor.BottomSide));
        Assert.Equal(new Vector2I(4, 2), TileMapLayer.GetNeighborCell(new Vector2I(3, 3), TileCellNeighbor.TopRightCorner));
        Assert.Equal(new Vector2I(3, 3), TileMapLayer.GetNeighborCell(new Vector2I(3, 3), TileCellNeighbor.RightCorner)); // not a square neighbour

        layer.Position = new Vector2(100, 50);
        layer.Scale = new Vector2(2, 2);
        Assert.Equal(new Vector2I(1, 0), layer.LocalToMap(layer.ToLocal(new Vector2(100 + 40, 50 + 10))));
        Assert.Equal(new Vector2(100 + 48, 50 + 16), layer.ToGlobal(layer.MapToLocal(new Vector2I(1, 0))));
    }

    [Fact]
    public void UsedCellsAndRectCoverEveryChunk()
    {
        var layer = AddLayer();
        layer.SetCell(new Vector2I(-3, 2), 0, new Vector2I(0, 0));
        layer.SetCell(new Vector2I(20, -5), 0, new Vector2I(1, 0), TileSetAtlasSource.TransformTranspose);
        layer.SetCell(new Vector2I(4, 4), 0, new Vector2I(1, 0));
        Assert.Equal(new Rect2I(-3, -5, 24, 10), layer.GetUsedRect());

        var cells = new List<Vector2I>();
        Assert.Equal(3, layer.GetUsedCells(cells));
        Assert.Equal([new(20, -5), new(-3, 2), new(4, 4)], cells); // chunk order: (1,-1), (-1,0), (0,0)
        cells.Clear();
        Assert.Equal(2, layer.GetUsedCellsById(cells, atlasCoords: new Vector2I(1, 0)));
        cells.Clear();
        Assert.Equal(1, layer.GetUsedCellsById(cells, 0, new Vector2I(1, 0), alternative: 0));
        Assert.Equal(new Vector2I(4, 4), cells[0]);
        Assert.Equal(default, AddLayer(name: "Empty").GetUsedRect());
    }

    [Fact]
    public void RechunkingKeepsEveryCell()
    {
        var layer = AddLayer();
        for (var i = -20; i < 20; i++)
            layer.SetCell(new Vector2I(i, i * 2), 0, new Vector2I(i & 3, 0));
        Assert.Equal(40, layer.UsedCellCount);
        var before = Snapshot(layer);
        layer.RenderingQuadrantSize = 5;
        Assert.Equal(40, layer.UsedCellCount);
        Assert.Equal(before, Snapshot(layer));
        Assert.True(layer.ChunkCount > 10);
    }

    private static Dictionary<Vector2I, TileCell> Snapshot(TileMapLayer layer)
    {
        var cells = new List<Vector2I>();
        layer.GetUsedCells(cells);
        return cells.ToDictionary(c => c, layer.GetCell);
    }

    [Fact]
    public void ChunksDrawAsSubItemsInOneBatch()
    {
        var layer = AddLayer();
        for (var y = 0; y < 15; y++) // 320 × 240 px: the whole view
            for (var x = 0; x < 20; x++)
                layer.SetCell(new Vector2I(x, y), 0, new Vector2I((x + y) % 4, y % 2));
        var culled = Cull();
        Assert.Equal(2, culled.Count); // 2 × 1 chunks of 16 cells
        Assert.All(culled, c => Assert.Same(layer, c.Item));
        Assert.Equal(300 * 4, culled.Sum(c => c.DrawList.VertexCount));
        Assert.Single(_server.Frame.Batches); // one atlas, one state: one batch for the whole layer
        Assert.Equal(300 * 6, _server.Frame.Batches[0].IndexCount);
    }

    [Fact]
    public void OffScreenChunksAreCulledOnTheirOwnBounds()
    {
        var layer = AddLayer();
        layer.SetCell(new Vector2I(0, 0), 0, Vector2I.Zero);
        layer.SetCell(new Vector2I(100, 0), 0, Vector2I.Zero);
        var culled = Cull(new Rect2(0, 0, 320, 240));
        Assert.Single(culled);
        Assert.Equal(new Rect2(0, 0, 16, 16), culled[0].DrawList.Bounds);
    }

    [Fact]
    public void SubItemsDrawAfterTheOwnerAndBeforeItsChildrenWithTheOwnersModulateAndMaterial()
    {
        var layer = AddLayer();
        layer.SetCell(Vector2I.Zero, 0, Vector2I.Zero);
        layer.Modulate = new Vector4(1, 0.5f, 1, 1);
        layer.SelfModulate = new Vector4(0.5f, 1, 1, 1);
        var material = new CanvasItemMaterial { BlendMode = CanvasBlendMode.Add };
        layer.Material = material;
        var child = new Sprite2D { Name = "Child", Texture = Texture2D.FromPixels(4, 4, new byte[64]) };
        layer.AddChild(child);
        var behind = new Sprite2D { Name = "Behind", Texture = child.Texture, ShowBehindParent = true };
        layer.AddChild(behind);

        var culled = Cull();
        Assert.Equal(["Behind", "Layer", "Child"], culled.Select(c => c.Item.Name));
        Assert.NotNull(culled[1].DrawList);
        Assert.Equal(new Vector4(0.5f, 0.5f, 1, 1), culled[1].Modulate);
        Assert.Same(material, culled[1].Material);
    }

    [Fact]
    public void TilesWithAZIndexGoToTheirOwnSubItem()
    {
        var set = MakeTileSet();
        set.GetSource(0)!.GetOrCreateTileData(new Vector2I(1, 0)).ZIndex = 3;
        var layer = AddLayer(set);
        layer.ZIndex = 1;
        layer.SetCell(new Vector2I(0, 0), 0, new Vector2I(0, 0));
        layer.SetCell(new Vector2I(1, 0), 0, new Vector2I(1, 0));
        var culled = Cull();
        Assert.Equal([1, 4], culled.Select(c => c.Z));
    }

    [Fact]
    public void YSortedRowsInterleaveWithTheLayersChildren()
    {
        var set = MakeTileSet();
        // A two-tile tree: its top (atlas (0,1)) sorts at the trunk's base, 16 px below its own row.
        set.GetSource(0)!.GetOrCreateTileData(new Vector2I(0, 1)).YSortOrigin = 16;
        var layer = AddLayer(set);
        layer.YSortEnabled = true;
        layer.SetCell(new Vector2I(2, 0), 0, new Vector2I(0, 1)); // tree top, row 0 (sort y 8 + 16 = 24)
        layer.SetCell(new Vector2I(2, 1), 0, new Vector2I(1, 0)); // trunk, row 1 (sort y 24)
        layer.SetCell(new Vector2I(5, 3), 0, new Vector2I(2, 0)); // grass, row 3 (sort y 56)
        var hero = new Sprite2D { Name = "Hero", Texture = Texture2D.FromPixels(4, 4, new byte[64]), Position = new Vector2(40, 30) };
        layer.AddChild(hero);

        var culled = Cull();
        // Rows by their sort y (8+16, 24 tie: tree top first in index order, trunk), then the hero at 30, then row 3.
        Assert.Equal(["Layer", "Layer", "Hero", "Layer"], culled.Select(c => c.Item.Name));
        Assert.Equal(24, culled[0].Transform.Origin.Y);
        Assert.Equal(56, culled[3].Transform.Origin.Y);

        hero.Position = new Vector2(40, 10); // above both tree rows now
        Assert.Equal(["Hero", "Layer", "Layer", "Layer"], Cull().Select(c => c.Item.Name));

        layer.YSortEnabled = false; // one sub-item per chunk again, drawn before the child
        Assert.Equal(["Layer", "Hero"], Cull().Select(c => c.Item.Name));
    }

    [Fact]
    public void FlipsAndTransposeMirrorTheQuadAndKeepTheRegion()
    {
        var layer = AddLayer();
        layer.SetCell(new Vector2I(0, 0), 0, new Vector2I(1, 0));
        layer.SetCell(new Vector2I(2, 0), 0, new Vector2I(1, 0), TileSetAtlasSource.TransformFlipH);
        layer.SetCell(new Vector2I(4, 0), 0, new Vector2I(1, 0), TileSetAtlasSource.TransformTranspose | TileSetAtlasSource.TransformFlipV);
        var list = Cull()[0].DrawList;
        Assert.Equal(12, list.VertexCount);
        var v = list.Vertices.ToArray();
        // UV ranges stay the tile's (x 16…32 of 64), positions stay the cell's.
        for (var q = 0; q < 3; q++)
        {
            var quad = v.AsSpan(q * 4, 4).ToArray();
            Assert.Equal(16f / 64f, quad.Min(p => p.Uv.X), 4);
            Assert.Equal(32f / 64f, quad.Max(p => p.Uv.X), 4);
            Assert.Equal(q * 32f, quad.Min(p => p.Position.X), 3);
            Assert.Equal(q * 32f + 16f, quad.Max(p => p.Position.X), 3);
        }

        // The unflipped tile's left edge samples the region's left; the flipped one's samples its right.
        float UvAtLeft(int q) => v.Skip(q * 4).Take(4).Where(p => MathF.Abs(p.Position.X - q * 32f) < 0.01f).Average(p => p.Uv.X);
        Assert.Equal(16f / 64f, UvAtLeft(0), 4);
        Assert.Equal(32f / 64f, UvAtLeft(1), 4);
    }

    [Fact]
    public void CellsNamingMissingTilesAreKeptButNotDrawn()
    {
        var layer = AddLayer();
        layer.SetCell(new Vector2I(0, 0), 7, Vector2I.Zero);   // no source 7
        layer.SetCell(new Vector2I(1, 0), 0, new Vector2I(9, 9)); // not created
        layer.SetCell(new Vector2I(2, 0), 0, Vector2I.Zero);
        Assert.Equal(3, layer.UsedCellCount);
        var culled = Cull();
        Assert.Equal(4, culled.Sum(c => c.DrawList.VertexCount));
        Assert.Equal(7, layer.GetCellSourceId(new Vector2I(0, 0)));
    }

    [Fact]
    public void TileSetChangesRedrawTheLayer()
    {
        var set = MakeTileSet();
        var layer = AddLayer(set);
        layer.SetCell(Vector2I.Zero, 0, Vector2I.Zero);
        Cull();
        set.GetSource(0)!.GetOrCreateTileData(Vector2I.Zero).Modulate = new Vector4(1, 0, 0, 1);
        var color = Cull()[0].DrawList.Vertices[0].Color;
        Assert.Equal(new Vector4(1, 0, 0, 1), color);
        layer.Enabled = false;
        Assert.Empty(Cull());
    }

    [Fact]
    public void TexturePaddingCopiesEdgesAroundEveryTile()
    {
        var set = MakeTileSet(padding: true);
        var source = set.GetSource(0)!;
        Assert.True(source.TryGetRuntimeRegion(new Vector2I(1, 1), out var padded, out var region));
        Assert.Equal((4 * 18, 2 * 18), (padded.Width, padded.Height));
        Assert.Equal(new Rect2(19, 19, 16, 16), region);
        var (rgba, w, _) = padded.DecodePixels();
        var (src, sw, _) = source.Texture!.DecodePixels();
        int Px(byte[] p, int width, int x, int y) => BitConverter.ToInt32(p, (y * width + x) * 4);
        // The padding pixel left of tile (1,1) repeats its first column, the one above repeats its first row.
        Assert.Equal(Px(src, sw, 16, 16), Px(rgba, w, 18, 19));
        Assert.Equal(Px(src, sw, 16, 16), Px(rgba, w, 19, 18));
        Assert.Equal(Px(src, sw, 31, 31), Px(rgba, w, 35, 35));
        Assert.Equal(Px(src, sw, 31, 31), Px(rgba, w, 34, 34));
        Assert.Equal(Px(src, sw, 20, 20), Px(rgba, w, 23, 23));
    }

    [Fact]
    public void OpaqueRegionsCreateTiles()
    {
        var pixels = new byte[48 * 16 * 4];
        pixels[(3 * 48 + 2) * 4 + 3] = 255;      // one pixel in tile (0,0)
        pixels[(15 * 48 + 47) * 4 + 3] = 1;      // the last pixel of tile (2,0)
        var source = new TileSetAtlasSource { Texture = Texture2D.FromPixels(48, 16, pixels) };
        Assert.Equal(2, source.CreateTilesFromOpaqueRegions());
        Assert.True(source.HasTile(new Vector2I(0, 0)));
        Assert.False(source.HasTile(new Vector2I(1, 0)));
        Assert.True(source.HasTile(new Vector2I(2, 0)));
        Assert.Equal(0, source.CreateTilesFromOpaqueRegions());
    }

    [Fact]
    public void AlternativesAndOverridesResolveToTheirData()
    {
        var source = new TileSetAtlasSource();
        source.CreateTile(new Vector2I(1, 2));
        Assert.Same(source.GetTileData(new Vector2I(1, 2)), source.GetTileData(new Vector2I(1, 2), TileSetAtlasSource.TransformFlipV));
        var data = source.GetOrCreateTileData(new Vector2I(1, 2));
        data.Probability = 0.25f;
        var alt = source.CreateAlternativeTile(new Vector2I(1, 2));
        Assert.Equal(1, alt);
        source.GetOrCreateTileData(new Vector2I(1, 2), alt).Modulate = new Vector4(0, 1, 0, 1);
        Assert.Equal(0.25f, source.GetTileData(new Vector2I(1, 2), alt | TileSetAtlasSource.TransformFlipH).Probability); // copied from the base
        Assert.Equal(new Vector4(0, 1, 0, 1), source.GetTileData(new Vector2I(1, 2), alt).Modulate);
        var alternatives = new List<int>();
        Assert.Equal(2, source.GetAlternativeTiles(new Vector2I(1, 2), alternatives));
        Assert.Equal([0, 1], alternatives);
        source.RemoveTile(new Vector2I(1, 2));
        Assert.Empty(source.TileOverrides);
    }

    [Fact]
    public void CustomDataIsTypedAndParsedOnce()
    {
        var set = new TileSet();
        Assert.Equal(0, set.AddCustomDataLayer("damage", CustomDataType.Int));
        Assert.Equal(1, set.AddCustomDataLayer("surface", CustomDataType.String));
        set.AddCustomDataLayer("solid", CustomDataType.Bool);
        set.AddCustomDataLayer("push", CustomDataType.Vector2);
        set.AddCustomDataLayer("tint", CustomDataType.Color);
        Assert.Equal(1, set.GetCustomDataLayerByName("surface"));
        Assert.Equal(-1, set.GetCustomDataLayerByName("missing"));

        var data = new TileData();
        data.SetCustomData(0, 12L);
        data.SetCustomData(1, "grass");
        data.SetCustomData(2, true);
        data.SetCustomData(3, new Vector2(1.5f, -2));
        data.SetCustomData(4, new Vector4(0.25f, 0.5f, 0.75f, 1));
        Assert.Equal(["12", "grass", "true", "1.5,-2", "0.25,0.5,0.75,1"], data.CustomData);
        Assert.Equal(12L, data.GetCustomInt(0));
        Assert.Equal(12.0, data.GetCustomFloat(0));
        Assert.Equal("grass", data.GetCustomString(1));
        Assert.True(data.GetCustomBool(2));
        Assert.Equal(new Vector2(1.5f, -2), data.GetCustomVector2(3));
        Assert.Equal(new Vector4(0.25f, 0.5f, 0.75f, 1), data.GetCustomColor(4));
        Assert.False(data.GetCustomBool(9));
        Assert.Equal(new Vector4(0, 0, 0, 1), data.GetCustomColor(9));

        _ = data.GetCustomInt(0);
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 1000; i++)
                _ = data.GetCustomInt(0) + (data.GetCustomBool(2) ? 1 : 0) + data.GetCustomVector2(3).X;
        }));
    }

    [Fact]
    public void ALargeLayerAndYSortedObjectsAllocateNothingWhileTheCameraScrolls()
    {
        var set = MakeTileSet();
        var ground = AddLayer(set, "Ground");
        for (var y = 0; y < 256; y++)
            for (var x = 0; x < 256; x++)
                ground.SetCell(new Vector2I(x, y), 0, new Vector2I((x * 7 + y) % 4, (x + y) % 2));
        var objects = AddLayer(set, "Objects");
        objects.YSortEnabled = true;
        for (var i = 0; i < 400; i++)
            objects.SetCell(new Vector2I(i * 13 % 256, i * 7 % 256), 0, new Vector2I(0, 1));
        var sprites = new Texture2D[1] { Texture2D.FromPixels(8, 8, new byte[256]) };
        for (var i = 0; i < 50; i++)
            objects.AddChild(new Sprite2D { Texture = sprites[0], Position = new Vector2(i * 37 % 4096, i * 53 % 4096) });

        var t = 0;
        void Frame()
        {
            t++;
            _tree.Root.CanvasTransform = Transform2D.FromTrs(new Vector2(-(t * 7 % 3500), -(t * 5 % 3700)), 0, Vector2.One);
            _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
            _server.Process(new GameTime { DeltaTime = 1f / 60f });
        }

        for (var f = 0; f < 120; f++)
            Frame();
        Assert.True(_server.Frame.Batches.Count is > 0 and < 8, $"{_server.Frame.Batches.Count} batches");
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var f = 0; f < 300; f++)
                Frame();
        }));
    }
}
