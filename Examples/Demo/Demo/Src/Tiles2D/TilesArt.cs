using MainframeEngine;

namespace Demo;

/// <summary>
/// The Tiles 2D scene's placeholder art (MIT, generated so real art can replace it without code changes): a 4×2 atlas of
/// 16 px tiles — grass, dirt path, stone wall, water; a tree's canopy and trunk (two tiles, the canopy sorting at the
/// trunk's base), flowers and a bush. Written by <c>--write-scenes</c> to <c>Assets/Tiles2D/tiles.png</c>.
/// </summary>
public static class TilesArt
{
    public const int Cell = 16, Columns = 4, Rows = 2;
    public const int Width = Cell * Columns, Height = Cell * Rows;

    public static readonly Vector2I Grass = new(0, 0), Path = new(1, 0), Stone = new(2, 0), Water = new(3, 0);
    public static readonly Vector2I Canopy = new(0, 1), Trunk = new(1, 1), Flowers = new(2, 1), Bush = new(3, 1);

    public static byte[] Pixels()
    {
        var p = new byte[Width * Height * 4];
        for (var y = 0; y < Cell; y++)
            for (var x = 0; x < Cell; x++)
            {
                var noise = Hash(x, y);
                // Grass: two greens with a few blades.
                Set(p, x, y, noise % 7 == 0 ? (112, 176, 84) : noise % 3 == 0 ? (86, 150, 70) : (96, 162, 76));
                // Dirt path: warm browns with pebbles.
                Set(p, Cell + x, y, noise % 11 == 0 ? (176, 150, 112) : noise % 2 == 0 ? (150, 118, 84) : (140, 108, 76));
                // Stone wall: bricks (two courses offset), mortar lines darker.
                var mortar = y % 8 == 0 || (x + (y / 8 % 2 == 0 ? 0 : 4)) % 8 == 0;
                Set(p, 2 * Cell + x, y, mortar ? (82, 84, 96) : noise % 5 == 0 ? (140, 142, 156) : (122, 124, 138));
                // Water: blue with light ripples.
                var ripple = (x + y * 2) % 9 == 0 && y % 4 == 1;
                Set(p, 3 * Cell + x, y, ripple ? (150, 200, 240) : noise % 4 == 0 ? (60, 120, 196) : (52, 108, 184));

                // Canopy: a rounded crown with highlights (transparent around it).
                var dx = x - 7.5f;
                var dy = y - 9f;
                if (dx * dx / 64f + dy * dy / 56f <= 1f)
                    Set(p, x, Cell + y, dx + dy < -6 ? (98, 170, 92) : noise % 6 == 0 ? (52, 112, 56) : (64, 132, 64));
                // Trunk: a bark column with roots at the base.
                if (x is >= 6 and < 10 || y >= 13 && x is >= 4 and < 12)
                    Set(p, Cell + x, Cell + y, x == 6 || noise % 5 == 0 ? (96, 64, 40) : (122, 84, 52));
                // Flowers on grass.
                Set(p, 2 * Cell + x, Cell + y, noise % 7 == 0 ? (112, 176, 84) : (96, 162, 76));
                if (Hash(x / 4, y / 4) % 3 == 0 && x % 4 == 1 && y % 4 == 1)
                {
                    Set(p, 2 * Cell + x, Cell + y, (240, 220, 90));
                    Set(p, 2 * Cell + x + 1, Cell + y, (236, 96, 120));
                }

                // Bush: an octagon of leaves.
                var bx = Math.Abs(x - 7.5f);
                var by = Math.Abs(y - 9.5f);
                if (bx <= 6.5f && by <= 5.5f && bx + by <= 9.5f)
                    Set(p, 3 * Cell + x, Cell + y, noise % 4 == 0 ? (60, 128, 70) : (44, 104, 58));
            }

        return p;
    }

    public static void Write(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        Png.WriteRgba8(path, Width, Height, Pixels());
    }

    private static int Hash(int x, int y) => (x * 73856093 ^ y * 19349663 ^ 83492791) & 0x7FFFFFFF;

    private static void Set(byte[] p, int x, int y, (int R, int G, int B) c)
    {
        if ((uint)x >= Width || (uint)y >= Height)
            return;
        var i = (y * Width + x) * 4;
        (p[i], p[i + 1], p[i + 2], p[i + 3]) = ((byte)c.R, (byte)c.G, (byte)c.B, 255);
    }
}
