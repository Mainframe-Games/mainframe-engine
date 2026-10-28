using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// <see cref="TileMapLayer"/> (G2b) through the canvas at Godot's 480×270 canvas_items stretch, nearest filtering, one
/// generated 4×2 atlas of 16 px tiles (a colour per tile with a white notch in its top-left corner that shows flips;
/// (0,1)/(1,1) are a two-tile tree whose top sorts at the trunk's base).
/// <list type="bullet">
/// <item><c>tiles</c>: a "Ground" layer of four tiles in a checker, a row of the notched tile with every flip and
/// transpose, and a y-sorted "Objects" layer (scaled 2×) with three trees and two sprites, one behind a tree and one in
/// front of another (rows sort with the layer's children).</item>
/// <item><c>tiles-seam</c>: one solid tile surrounded in the atlas by magenta ones, filling the view under a Camera2D at
/// zoom 1.37 with linear filtering and texture padding: no magenta may bleed into the tile edges.</item>
/// <item><c>--count N</c>: an N × N layer scrolled under the view (the allocation gate).</item>
/// </list>
/// </summary>
public sealed class TilesScene(HostOptions host, bool seam = false) : RenderTestGame(host)
{
    private const int Cell = 16;
    private int _frame;
    private TileMapLayer? _big;

    protected override void LoadScene()
    {
        Servers.Get<CanvasServer>()!.ClearColor = CanvasServer.GodotDefaultClearColor;
        Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        Root.ContentScaleAspect = ContentScaleAspect.Expand;
        Root.ContentScaleSize = new Vector2(480, 270);
        var root = new Node2D { Name = "Canvas", TextureFilter = seam ? CanvasTextureFilter.Linear : CanvasTextureFilter.Nearest };
        var set = TileSetFor(seam);

        if (seam)
        {
            var layer = new TileMapLayer { Name = "Seam", TileSet = set };
            for (var y = -2; y < 20; y++)
                for (var x = -2; x < 34; x++)
                    layer.SetCell(new Vector2I(x, y), 0, new Vector2I(1, 1), (x + y) % 3 == 0 ? TileSetAtlasSource.TransformFlipH : 0);
            root.AddChild(layer);
            root.AddChild(new Camera2D { Name = "Camera", Position = new Vector2(241.3f, 133.7f), Zoom = new Vector2(1.37f) });
            Tree.ChangeScene(root);
            return;
        }

        if (Host.Count > 0)
        {
            var big = _big = new TileMapLayer { Name = "Big", TileSet = set };
            for (var y = 0; y < Host.Count; y++)
                for (var x = 0; x < Host.Count; x++)
                    big.SetCell(new Vector2I(x, y), 0, new Vector2I((x * 3 + y) % 4, 0), ((x ^ y) & 1) * TileSetAtlasSource.TransformFlipH);
            root.AddChild(big);
            Tree.ChangeScene(root);
            return;
        }

        var ground = new TileMapLayer { Name = "Ground", TileSet = set };
        for (var y = 0; y < 17; y++)
            for (var x = 0; x < 30; x++)
                ground.SetCell(new Vector2I(x, y), 0, new Vector2I((x / 2 + y / 2) % 2 == 0 ? 2 : 3, 0));
        // Row 1: the notched tile with each transform (none, H, V, H+V, T, T+H, T+V, T+H+V).
        for (var i = 0; i < 8; i++)
        {
            var flags = ((i & 1) != 0 ? TileSetAtlasSource.TransformFlipH : 0) | ((i & 2) != 0 ? TileSetAtlasSource.TransformFlipV : 0) |
                        ((i & 4) != 0 ? TileSetAtlasSource.TransformTranspose : 0);
            ground.SetCell(new Vector2I(1 + i * 2, 1), 0, new Vector2I(0, 0), flags);
        }

        root.AddChild(ground);

        var objects = new TileMapLayer { Name = "Objects", TileSet = set, YSortEnabled = true, Scale = new Vector2(2), Position = new Vector2(0, 40) };
        foreach (var x in (ReadOnlySpan<int>)[2, 5, 9])
        {
            objects.SetCell(new Vector2I(x, 2), 0, new Vector2I(0, 1)); // top
            objects.SetCell(new Vector2I(x, 3), 0, new Vector2I(1, 1)); // trunk
        }

        // Both tree rows sort at y 56 (the top's row centre 40 + its y-sort origin 16, the trunk's row centre 56): a hero
        // whose origin is above that (y 50) draws behind the tree at x 2, one below it (y 62) in front of the tree at x 5.
        var texture = HeroTexture();
        objects.AddChild(new Sprite2D { Name = "Behind", Texture = texture, Position = new Vector2(2 * Cell + 8, 50), Offset = new Vector2(0, -6) });
        objects.AddChild(new Sprite2D { Name = "Front", Texture = texture, Position = new Vector2(5 * Cell + 8, 62), Offset = new Vector2(0, -6) });
        root.AddChild(objects);
        Tree.ChangeScene(root);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        if (_big is null)
            return;

        _frame++;
        // The layer scrolls under a fixed view (a moving Camera2D also drives the 3D path's orthographic camera).
        _big.Position = -new Vector2(_frame * 7 % (Host.Count * Cell - 480), _frame * 3 % (Host.Count * Cell - 270));
    }

    protected override void DisposeScene()
    {
    }

    private static TileSet TileSetFor(bool seam)
    {
        var source = new TileSetAtlasSource { Texture = Atlas(seam), UseTexturePadding = true };
        for (var y = 0; y < 2; y++)
            for (var x = 0; x < 4; x++)
                source.CreateTile(new Vector2I(x, y));
        source.GetOrCreateTileData(new Vector2I(0, 1)).YSortOrigin = Cell; // the tree top sorts at the trunk's base
        var set = new TileSet { TileSize = new Vector2I(Cell, Cell) };
        set.AddSource(source);
        return set;
    }

    private static Texture2D Atlas(bool seam)
    {
        const int w = Cell * 4, h = Cell * 2;
        var pixels = new byte[w * h * 4];
        (byte, byte, byte)[] colours =
        [
            (220, 60, 60), (240, 180, 40), (90, 170, 70), (110, 150, 80),
            (40, 130, 60), (130, 90, 50), (230, 120, 170), (150, 150, 60),
        ];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (cx, cy, lx, ly) = (x / Cell, y / Cell, x % Cell, y % Cell);
                var (r, g, b) = colours[cy * 4 + cx];
                byte a = 255;
                if (seam)
                    (r, g, b) = cx == 1 && cy == 1 ? ((byte)40, (byte)120, (byte)200) : ((byte)255, (byte)0, (byte)255);
                else if (cx == 0 && cy == 0 && lx is >= 1 and < 6 && ly is >= 1 and < 4)
                    (r, g, b) = (255, 255, 255); // the notch: wider than tall, so a transpose shows too
                else if (cy == 1 && cx == 0)
                    a = (byte)(Math.Abs(lx - 7.5f) + Math.Abs(ly - 10f) < 9 ? 255 : 0); // the canopy
                else if (cy == 1 && cx == 1)
                    a = (byte)(lx is >= 6 and < 10 ? 255 : 0); // the trunk
                var i = (y * w + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, a);
            }

        return Texture2D.FromPixels(w, h, pixels);
    }

    private static Texture2D HeroTexture()
    {
        const int w = 8, h = 12;
        var pixels = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
            (pixels[i * 4], pixels[i * 4 + 1], pixels[i * 4 + 2], pixels[i * 4 + 3]) = ((byte)60, (byte)90, (byte)230, (byte)255);
        return Texture2D.FromPixels(w, h, pixels);
    }
}
