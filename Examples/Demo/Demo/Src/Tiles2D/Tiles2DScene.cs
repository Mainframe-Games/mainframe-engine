using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// Tiles 2D (G2b, G3.6): three <see cref="TileMapLayer"/>s painted from a small ASCII level with one generated atlas
/// (<see cref="TilesArt"/>, <c>Assets/Tiles2D/tiles.png</c> + the <see cref="TileSet"/> <c>tiles.mfres</c>): "Ground"
/// (grass, paths, flowers), "Walls" (what blocks: stone and water, with full-tile collision) and the y-sorted "Objects"
/// (trees whose canopy sorts at the trunk's base, bushes with polygon collision). The Sprites 2D hero
/// (<c>hero.mfres</c>) is a <see cref="TileHero2D"/> inside the objects layer, so it walks behind and in front of the
/// trees; a camera follows it. A <see cref="TileShadow2D"/> chases it on a <see cref="NavigationGrid2D"/> that reads the
/// walls layer (<see cref="NavigationGridSource.TileMapLayer"/>). The panel shows collision and navigation, toggles y-sort,
/// the walk speed and the chase, and a click on the map adds or removes a wall cell at run time (the chunk's drawing and
/// body rebuild, and the shadow repaths).
/// </summary>
public static class Tiles2DScene
{
    public const string AtlasPath = "Assets/Tiles2D/tiles.png";
    public const string TileSetPath = "Assets/Tiles2D/tiles.mfres";

    /// <summary>The level: # wall, . grass, = path, ~ water, * flowers, T tree (canopy above), b bush, @ hero start, s shadow start.</summary>
    public static readonly string[] Level =
    [
        "##########################################",
        "#....*....T........====.........T....*...#",
        "#..T...........b...=..=....T.............#",
        "#.......~~~~~......=..=.........b....T...#",
        "#...*..~~~~~~~.....=..=..............*...#",
        "#.......~~~~~...T..=..=====......T.......#",
        "#..b.........=======......=..............#",
        "#.....T......=.....#####..=....b....T....#",
        "#............=.....#...#..=..............#",
        "#..==========@.....#...#..=====....*.....#",
        "#..=...............##.##......=..........#",
        "#..=....T....b...........T....=....T..b..#",
        "#..=.....................*....=..........#",
        "#..=======.....T.....b........=====......#",
        "#........=...........~~~~~........=..T...#",
        "#..T.....=.....*....~~~~~~~.......=......#",
        "#........==========.~~~~~~~..T....=......#",
        "#...b..............~~~~~~~........=..*...#",
        "#.........T.....*.........b.......=.....s#",
        "##########################################",
    ];

    public static Node Build()
    {
        var tiles = ResourceLoader.Load<TileSet>(TileSetPath);
        var frames = ResourceLoader.Load<SpriteFrames>(Sprites2DScene.FramesPath);
        var root = new Node2D { Name = "tiles_2d", TextureFilter = CanvasTextureFilter.Nearest };
        var ground = Add(root, root, new TileMapLayer { Name = "Ground", TileSet = tiles });
        var walls = Add(root, root, new TileMapLayer { Name = "Walls", TileSet = tiles });
        var objects = Add(root, root, new TileMapLayer { Name = "Objects", TileSet = tiles, YSortEnabled = true });
        var start = Vector2.Zero;
        var shadowStart = Vector2.Zero;
        for (var y = 0; y < Level.Length; y++)
            for (var x = 0; x < Level[y].Length; x++)
            {
                var cell = new Vector2I(x, y);
                var c = Level[y][x];
                ground.SetCell(cell, 0, c switch
                {
                    '=' or '@' => TilesArt.Path,
                    '*' => TilesArt.Flowers,
                    _ => TilesArt.Grass,
                }, (x * 7 + y * 3) % 5 == 0 && c == '.' ? TileSetAtlasSource.TransformFlipH : 0);
                switch (c)
                {
                    case '#':
                        walls.SetCell(cell, 0, TilesArt.Stone);
                        break;
                    case '~':
                        walls.SetCell(cell, 0, TilesArt.Water);
                        break;
                    case 'T':
                        objects.SetCell(cell + new Vector2I(0, -1), 0, TilesArt.Canopy);
                        objects.SetCell(cell, 0, TilesArt.Trunk);
                        break;
                    case 'b':
                        objects.SetCell(cell, 0, TilesArt.Bush);
                        break;
                    case '@':
                        start = ground.MapToLocal(cell) + new Vector2(0, 6);
                        break;
                    case 's':
                        shadowStart = ground.MapToLocal(cell) + new Vector2(0, 6);
                        break;
                }
            }

        var hero = Add(root, objects, new TileHero2D { Name = "Hero", Position = start, MotionMode = CharacterMotionMode2D.Floating });
        Add(root, hero, new CollisionShape2D { Name = "Shape", Shape = new CircleShape2D { Radius = 4 }, Position = new Vector2(0, -3) });
        Add(root, hero, new AnimatedSprite2D
        {
            Name = "Sprite",
            SpriteFrames = frames,
            Animation = "idle",
            Autoplay = "idle",
            Scale = new Vector2(0.75f),
            Offset = new Vector2(0, -14), // feet on the origin (the y-sort position)
        });
        Add(root, hero, new Camera2D
        {
            Name = "Camera",
            Current = true,
            Zoom = new Vector2(3),
            PositionSmoothingEnabled = true,
            PositionSmoothingSpeed = 6,
            LimitLeft = 0,
            LimitTop = 0,
            LimitRight = Level[0].Length * TilesArt.Cell,
            LimitBottom = Level.Length * TilesArt.Cell,
        });

        // The shadow's navigation: the walls layer owns the grid's layout and solid cells, and is re-read when it changes.
        Add(root, root, new NavigationGrid2D
        {
            Name = "Navigation",
            Source = NavigationGridSource.TileMapLayer,
            TileMapLayerPath = "../Walls",
        });
        var shadow = Add(root, objects, new TileShadow2D { Name = "Shadow", Position = shadowStart });
        Add(root, shadow, new AnimatedSprite2D
        {
            Name = "Sprite",
            SpriteFrames = frames,
            Animation = "idle",
            Autoplay = "idle",
            Scale = new Vector2(0.75f),
            Offset = new Vector2(0, -14),
            Modulate = new Vector4(0.25f, 0.2f, 0.4f, 0.85f), // a dark copy of the hero
        });
        Add(root, shadow, new NavigationAgent2D { Name = "Agent", PathDesiredDistance = 4f }); // 16 px cells: never skip a corner

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Tiles2DPanel { Name = "Panel" });
        return root;
    }

    /// <summary>The tile set: one physics layer (stone, water: full tiles; trunks and bushes: polygons) and "surface" data.</summary>
    public static TileSet CreateTileSet(Texture2D atlas)
    {
        var set = new TileSet { TileSize = new Vector2I(TilesArt.Cell, TilesArt.Cell) };
        set.AddPhysicsLayer();
        var surface = set.AddCustomDataLayer("surface", CustomDataType.String);
        var source = new TileSetAtlasSource { Texture = atlas, TextureRegionSize = set.TileSize };
        set.AddSource(source);
        for (var y = 0; y < TilesArt.Rows; y++)
            for (var x = 0; x < TilesArt.Columns; x++)
                source.CreateTile(new Vector2I(x, y));

        source.GetOrCreateTileData(TilesArt.Grass).SetCustomData(surface, "grass");
        source.GetOrCreateTileData(TilesArt.Flowers).SetCustomData(surface, "grass");
        source.GetOrCreateTileData(TilesArt.Path).SetCustomData(surface, "dirt");
        var stone = source.GetOrCreateTileData(TilesArt.Stone);
        stone.SetCustomData(surface, "stone");
        stone.Collision = [TileCollisionPolygon.FullTile(set.TileSize)];
        var water = source.GetOrCreateTileData(TilesArt.Water);
        water.SetCustomData(surface, "water");
        water.Collision = [TileCollisionPolygon.FullTile(set.TileSize)];
        source.GetOrCreateTileData(TilesArt.Canopy).YSortOrigin = TilesArt.Cell; // sorts at the trunk's base
        source.GetOrCreateTileData(TilesArt.Trunk).AddCollisionPolygon(0, [new(-3, 2), new(3, 2), new(3, 8), new(-3, 8)]);
        var bush = source.GetOrCreateTileData(TilesArt.Bush);
        bush.AddCollisionPolygon(0, [new(-6, 1), new(-3, -2), new(3, -2), new(6, 1), new(6, 4), new(3, 7), new(-3, 7), new(-6, 4)]);
        bush.Probability = 0.5f;
        return set;
    }

    /// <summary>
    /// Writes <c>tiles.png</c> (+ its <c>.mfmeta</c>) and <c>tiles.mfres</c> under <paramref name="assets"/> and where the
    /// running game loads content from, like <see cref="Sprites2DScene.WriteAssets"/>.
    /// </summary>
    public static void WriteAssets(string assets)
    {
        var folder = Path.Combine(assets, "Tiles2D");
        var png = Path.Combine(folder, "tiles.png");
        TilesArt.Write(png);
        if (!File.Exists(png + ".mfmeta"))
            File.WriteAllText(png + ".mfmeta", "{\n  \"uid\": \"tex_7a3e91c40d25\"\n}"); // a fixed UID: the tile set references it
        var output = Path.GetDirectoryName(ContentPaths.Resolve(AtlasPath))!;
        Mirror(folder, output, "tiles.png", "tiles.png.mfmeta");
        AssetDatabase.Current.Scan();
        var atlas = ResourceLoader.Load<Texture2D>(AtlasPath);
        try
        {
            var uid = ResourceSaver.Save(CreateTileSet(atlas), TileSetPath);
            Mirror(output, folder, "tiles.mfres");
            Log.Info($"Wrote tiles.png and tiles.mfres ({uid})");
        }
        finally
        {
            atlas.Release();
        }
    }

    private static void Mirror(string from, string to, params string[] files)
    {
        if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(to), StringComparison.Ordinal))
            return;
        Directory.CreateDirectory(to);
        foreach (var file in files)
            File.Copy(Path.Combine(from, file), Path.Combine(to, file), overwrite: true);
    }
}
