using MainframeEngine;

namespace Demo;

/// <summary>
/// The Sprites 2D scene's placeholder art (MIT, generated so real art can replace it without code changes): a 4×4
/// sheet of 32 px cells — a blob idling, running and jumping, and a spinning coin — each frame numbered 1–4 in its
/// bottom-right corner. Written by <c>--write-scenes</c> to <c>Assets/Sprites2D/hero.png</c>.
/// </summary>
public static class HeroArt
{
    public const int Cell = 32, Columns = 4, Rows = 4;
    public const int Width = Cell * Columns, Height = Cell * Rows;

    /// <summary>The animations, one per row of the sheet: name, frames per second, loops.</summary>
    public static readonly (string Name, float Speed, bool Loop)[] Animations =
    [
        ("idle", 4f, true),
        ("run", 10f, true),
        ("jump", 8f, true),
        ("coin", 8f, true),
    ];

    private static readonly (byte R, byte G, byte B) Body = (92, 200, 120), Shade = (40, 120, 70), Eye = (255, 255, 255),
        Pupil = (24, 28, 40), Gold = (250, 200, 60), GoldShade = (190, 130, 30), Digit = (255, 255, 255);

    public static byte[] Pixels()
    {
        var pixels = new byte[Width * Height * 4];
        for (var frame = 0; frame < Columns; frame++)
        {
            var x0 = frame * Cell;
            Idle(pixels, x0, 0, frame);
            Run(pixels, x0, Cell, frame);
            Jump(pixels, x0, Cell * 2, frame);
            Coin(pixels, x0, Cell * 3, frame);
            for (var row = 0; row < Rows; row++)
                Number(pixels, x0 + Cell - 5, row * Cell + Cell - 7, frame + 1);
        }

        return pixels;
    }

    public static void Write(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Png.WriteRgba8(path, Width, Height, Pixels());
    }

    // Breathing: the blob squashes a little on frames 2 and 3.
    private static void Idle(byte[] p, int x0, int y0, int frame)
    {
        var squash = frame is 1 or 3 ? 1 : frame == 2 ? 2 : 0;
        Blob(p, x0 + 15, y0 + 18 + squash, 10 + squash, 10 - squash, 0);
    }

    // Leaning forward, feet alternating.
    private static void Run(byte[] p, int x0, int y0, int frame)
    {
        var bob = frame % 2;
        var stride = frame is 0 or 2 ? 4 : -4;
        Ellipse(p, x0 + 13 + stride, y0 + 28, 3, 2, Shade);
        Ellipse(p, x0 + 17 - stride, y0 + 28, 3, 2, Shade);
        Blob(p, x0 + 15, y0 + 17 - bob, 10, 9, 2);
    }

    // Crouch, stretch up, apex, land.
    private static void Jump(byte[] p, int x0, int y0, int frame)
    {
        var (cy, rx, ry) = frame switch
        {
            0 => (22, 12, 7),
            1 => (14, 8, 12),
            2 => (11, 10, 10),
            _ => (20, 11, 8),
        };
        Blob(p, x0 + 15, y0 + cy, rx, ry, 0);
    }

    // A coin turning: its width narrows to an edge and back.
    private static void Coin(byte[] p, int x0, int y0, int frame)
    {
        var rx = frame switch { 0 => 10, 1 => 6, 2 => 2, _ => 6 };
        Ellipse(p, x0 + 15, y0 + 15, rx + 1, 11, GoldShade);
        Ellipse(p, x0 + 15, y0 + 15, rx, 10, Gold);
        if (rx > 3)
            Ellipse(p, x0 + 15, y0 + 15, Math.Max(1, rx / 3), 5, GoldShade);
    }

    private static void Blob(byte[] p, int cx, int cy, int rx, int ry, int lean)
    {
        Ellipse(p, cx, cy, rx + 1, ry + 1, Shade);
        Ellipse(p, cx, cy, rx, ry, Body);
        var eyeY = cy - ry / 3;
        Ellipse(p, cx + 3 + lean, eyeY, 2, 3, Eye);
        Ellipse(p, cx + 4 + lean, eyeY, 1, 1, Pupil);
    }

    private static void Ellipse(byte[] p, int cx, int cy, int rx, int ry, (byte R, byte G, byte B) color)
    {
        for (var y = cy - ry; y <= cy + ry; y++)
            for (var x = cx - rx; x <= cx + rx; x++)
            {
                var dx = (x - cx) / (rx + 0.5f);
                var dy = (y - cy) / (ry + 0.5f);
                if (dx * dx + dy * dy <= 1f)
                    Set(p, x, y, color);
            }
    }

    // A 3×5 digit (1–4).
    private static void Number(byte[] p, int x0, int y0, int digit)
    {
        string[] glyph = digit switch
        {
            1 => [".#.", "##.", ".#.", ".#.", "###"],
            2 => ["##.", "..#", ".#.", "#..", "###"],
            3 => ["##.", "..#", ".#.", "..#", "##."],
            _ => ["#.#", "#.#", "###", "..#", "..#"],
        };
        for (var y = 0; y < 5; y++)
            for (var x = 0; x < 3; x++)
                if (glyph[y][x] == '#')
                    Set(p, x0 + x, y0 + y, Digit);
    }

    private static void Set(byte[] p, int x, int y, (byte R, byte G, byte B) c)
    {
        if ((uint)x >= Width || (uint)y >= Height)
            return;
        var i = (y * Width + x) * 4;
        (p[i], p[i + 1], p[i + 2], p[i + 3]) = (c.R, c.G, c.B, 255);
    }
}
