using System.Numerics;

namespace MainframeEngine.Tests.Physics;

/// <summary>
/// G2b: a <see cref="TileMapLayer"/>'s static bodies — one per chunk per physics layer, full tiles merged into
/// rectangles, concave polygons in convex pieces of at most eight points, flips, queries and slides reporting the
/// layer, <see cref="TileMapLayer.GetCoordsForCollision"/>, erasing, moving the layer, areas and no allocation.
/// </summary>
[Collection(nameof(SerialBox2D))]
public sealed class TileBodies2DTests : IDisposable
{
    private readonly PhysicsHarness2D _h = new();

    public void Dispose() => _h.Dispose();

    // 16 px tiles: (0,0) full square, (1,0) a concave "L" (10 points), (2,0) a slope triangle, (3,0) no collision.
    private static TileSet MakeTileSet()
    {
        var source = new TileSetAtlasSource { Texture = Texture2D.FromPixels(64, 16, new byte[64 * 16 * 4]) };
        for (var x = 0; x < 4; x++)
            source.CreateTile(new Vector2I(x, 0));
        var set = new TileSet();
        set.AddSource(source);
        set.AddPhysicsLayer();
        source.GetOrCreateTileData(new Vector2I(0, 0)).Collision.Add(TileCollisionPolygon.FullTile(set.TileSize));
        source.GetOrCreateTileData(new Vector2I(1, 0)).AddCollisionPolygon(0,
            [new(-8, -8), new(-4, -8), new(-4, 0), new(0, 0), new(0, 2), new(4, 2), new(8, 2), new(8, 8), new(0, 8), new(-8, 8)]);
        source.GetOrCreateTileData(new Vector2I(2, 0)).AddCollisionPolygon(0, [new(-8, 8), new(8, -8), new(8, 8)]);
        set.NotifyChanged();
        return set;
    }

    private TileMapLayer AddLayer(TileSet? set = null)
    {
        var layer = new TileMapLayer { Name = "Tiles", TileSet = set ?? MakeTileSet() };
        return _h.Add(layer);
    }

    [Fact]
    public void FullTilesMergeIntoOneRectanglePerRunAndChunk()
    {
        var layer = AddLayer();
        for (var x = 0; x < 20; x++)
            layer.SetCell(new Vector2I(x, 0), 0, Vector2I.Zero); // a floor across two chunks
        for (var y = 1; y < 4; y++)
            for (var x = 0; x < 4; x++)
                layer.SetCell(new Vector2I(x, y), 0, Vector2I.Zero); // a 4 × 3 block under its start
        layer.UpdateInternals();

        Assert.Equal(2, _h.Space.TileBodyCount);
        var chunk = layer.Chunks[0];
        // Row 0: one 16-cell run; rows 1–3: one 4 × 3 rectangle.
        Assert.Equal(2, chunk.Bodies![0]!.Shapes.Count);
        Assert.Single(layer.Chunks[1].Bodies![0]!.Shapes);
    }

    [Fact]
    public void ConcavePolygonsSplitIntoConvexPiecesOfAtMostEightPoints()
    {
        var pieces = new List<Vector2[]>();
        Vector2[] l = [new(-8, -8), new(-4, -8), new(-4, 0), new(0, 0), new(0, 2), new(4, 2), new(8, 2), new(8, 8), new(0, 8), new(-8, 8)];
        Assert.False(TilePolygons.IsConvex(l));
        Assert.True(TilePolygons.Decompose(l, pieces));
        Assert.InRange(pieces.Count, 2, 4); // fewer pieces than the 8 triangles
        Assert.All(pieces, p => Assert.True(p.Length <= 8 && TilePolygons.IsConvex(p)));
        var area = pieces.Sum(p => MathF.Abs(Area(p)));
        Assert.Equal(MathF.Abs(Area(l)), area, 2);

        var layer = AddLayer();
        layer.SetCell(new Vector2I(2, 2), 0, new Vector2I(1, 0));
        layer.UpdateInternals();
        Assert.InRange(layer.Chunks[0].Bodies![0]!.Shapes.Count, 2, 4); // the cell's offset may triangulate differently
    }

    private static float Area(Vector2[] p)
    {
        var a = 0f;
        for (var i = 0; i < p.Length; i++)
            a += p[i].X * p[(i + 1) % p.Length].Y - p[(i + 1) % p.Length].X * p[i].Y;
        return a / 2;
    }

    [Fact]
    public void ARayHitsATileAndReportsTheLayerAndTheCell()
    {
        var layer = AddLayer();
        layer.Position = new Vector2(100, 0);
        layer.SetCell(new Vector2I(3, 5), 0, Vector2I.Zero);
        _h.Run(1);
        Assert.True(_h.Query.RayCast(new Vector2(100 + 56, -50), new Vector2(100 + 56, 500), out var hit));
        Assert.Same(layer, hit.Collider);
        Assert.Null(hit.Shape);
        Assert.Equal(80, hit.Position.Y, 1);
        Assert.True(layer.GetCoordsForCollision(hit.Position, hit.Normal, out var coords));
        Assert.Equal(new Vector2I(3, 5), coords);

        var found = new List<Node2D>();
        Assert.Equal(1, _h.Query.IntersectPoint(new Vector2(100 + 56, 88), found));
        Assert.Same(layer, found[0]);
    }

    [Fact]
    public void FlippedTilesFlipTheirPolygons()
    {
        var layer = AddLayer();
        layer.SetCell(new Vector2I(0, 0), 0, new Vector2I(2, 0)); // slope rising to the right: solid at the bottom right
        layer.SetCell(new Vector2I(2, 0), 0, new Vector2I(2, 0), TileSetAtlasSource.TransformFlipH); // rising to the left
        _h.Run(1);
        Assert.True(_h.Query.RayCast(new Vector2(14, -10), new Vector2(14, 40), out var a));
        Assert.True(_h.Query.RayCast(new Vector2(32 + 2, -10), new Vector2(32 + 2, 40), out var b));
        Assert.Equal(2, a.Position.Y, 1);  // near the high end of the slope
        Assert.Equal(2, b.Position.Y, 1);  // mirrored: high at the left
    }

    [Fact]
    public void ACharacterSlidesAlongATwentyTileFloorWithoutSnagging()
    {
        var layer = AddLayer();
        for (var x = 0; x < 20; x++)
            layer.SetCell(new Vector2I(x, 10), 0, Vector2I.Zero); // floor top at y = 160, across two chunks
        var hero = new WalkerCharacter2D { Name = "Hero", Position = new Vector2(20, 140), PhysicsInterpolation = false, Walk = new Vector2(150, 0) };
        hero.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(12, 12) } });
        _h.Add(hero);
        _h.Run(20); // land
        Assert.True(hero.IsOnFloor());
        Assert.Same(layer, hero.GetSlideCollision(0).Collider);
        var snags = 0;
        while (hero.Position.X < 300)
        {
            var before = hero.Position.X;
            _h.Run(1);
            if (hero.Position.X - before < 2.4f) // 150 px/s = 2.5 px per step
                snags++;
            Assert.True(hero.IsOnFloor(), $"left the floor at {hero.Position}");
        }

        Assert.Equal(0, snags);
        Assert.InRange(hero.Position.Y, 160 - 6 - 2.5f, 160 - 6 - 0.5f); // ~1 px safe margin above the floor
    }

    [Fact]
    public void ErasingACellRemovesItsShapeAndABodyWithoutShapes()
    {
        var layer = AddLayer();
        layer.SetCell(new Vector2I(0, 0), 0, Vector2I.Zero);
        layer.SetCell(new Vector2I(2, 0), 0, Vector2I.Zero);
        layer.SetCell(new Vector2I(40, 0), 0, Vector2I.Zero);
        layer.SetCell(new Vector2I(41, 0), 0, new Vector2I(3, 0)); // no collision
        _h.Run(1);
        Assert.Equal(2, _h.Space.TileBodyCount);
        Assert.True(_h.Query.RayCast(new Vector2(40, -10), new Vector2(40, 40), out _));
        layer.EraseCell(new Vector2I(2, 0));
        Assert.False(_h.Query.RayCast(new Vector2(40, -10), new Vector2(40, 40), out _));
        layer.EraseCell(new Vector2I(40, 0));
        _h.Run(1);
        Assert.Equal(1, _h.Space.TileBodyCount); // the far chunk's tile has no collision: no body
        layer.QueueFree();
        _h.Run(1);
        Assert.Equal(0, _h.Space.TileBodyCount);
    }

    [Fact]
    public void BodiesFollowTheLayer()
    {
        var layer = AddLayer();
        layer.SetCell(new Vector2I(0, 0), 0, Vector2I.Zero);
        _h.Run(1);
        layer.Position = new Vector2(500, 0);
        _h.Run(1);
        Assert.False(_h.Query.RayCast(new Vector2(8, -10), new Vector2(8, 40), out _));
        Assert.True(_h.Query.RayCast(new Vector2(508, -10), new Vector2(508, 40), out var hit));
        Assert.Equal(0, hit.Position.Y, 1);
        layer.Scale = new Vector2(2, 2);
        _h.Run(1);
        Assert.True(_h.Query.RayCast(new Vector2(530, -10), new Vector2(530, 40), out hit));
    }

    [Fact]
    public void BoxesRestOnTilesAndAnAreaSeesTheLayerOnce()
    {
        var layer = AddLayer();
        for (var x = -10; x < 30; x++)
            layer.SetCell(new Vector2I(x, 0), 0, Vector2I.Zero); // three chunks
        var box = _h.AddBox(new Vector2(40, -200), size: 20);
        var area = _h.AddArea(new Vector2(100, 8), new Vector2(600, 20));
        var entered = new List<Node2D>();
        area.BodyEntered += entered.Add;
        _h.RunSeconds(2f);
        Assert.InRange(box.Position.Y, -11, -9);
        Assert.Single(entered, n => ReferenceEquals(n, layer));

        // Erase the cells under the box: it wakes and falls.
        for (var x = 0; x < 6; x++)
            layer.EraseCell(new Vector2I(x, 0));
        _h.RunSeconds(1f);
        Assert.True(box.Position.Y > 100, $"box at {box.Position}");
    }

    [Fact]
    public void ALayerWithoutPhysicsLayersCreatesNoBodies()
    {
        var source = new TileSetAtlasSource { Texture = Texture2D.FromPixels(16, 16, new byte[16 * 16 * 4]) };
        source.CreateTile(Vector2I.Zero);
        var set = new TileSet();
        set.AddSource(source);
        var layer = AddLayer(set);
        layer.SetCell(Vector2I.Zero, 0, Vector2I.Zero);
        _h.Run(1);
        Assert.Null(layer.Bodies);
        set.AddPhysicsLayer();
        Assert.NotNull(layer.Bodies);
        layer.CollisionEnabled = false;
        Assert.Null(layer.Bodies);
    }

    [Fact]
    public void SteppingWithTileBodiesAllocatesNothing()
    {
        var layer = AddLayer();
        for (var y = 0; y < 64; y++)
            for (var x = 0; x < 64; x++)
                if ((x + y) % 5 != 0)
                    layer.SetCell(new Vector2I(x, y + 20), 0, new Vector2I((x * 3 + y) % 3, 0));
        var hero = new CharacterBody2D { Name = "Hero", Position = new Vector2(30, 300) };
        hero.AddChild(new CollisionShape2D { Shape = new RectangleShape2D { Size = new Vector2(10, 10) } });
        _h.Add(hero);
        PhysicsSpace2D.MeasureLibraryAllocations = true;
        try
        {
            void Frame()
            {
                hero.Velocity = new Vector2(30, 60);
                hero.MoveAndSlide();
                _h.Run(1);
            }

            for (var i = 0; i < 60; i++)
                Frame();
            var library = _h.Space.LibraryAllocatedBytes;
            var total = AllocationGate.SmallestWindow(() =>
            {
                library = _h.Space.LibraryAllocatedBytes;
                for (var i = 0; i < 120; i++)
                    Frame();
                library = _h.Space.LibraryAllocatedBytes - library;
            });
            Assert.Equal(0, total - library);
        }
        finally
        {
            PhysicsSpace2D.MeasureLibraryAllocations = false;
        }
    }
}
