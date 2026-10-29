using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// <see cref="AnimatedSprite2D"/> (ADR 0223) through the canvas, at Godot's 480×270 canvas_items stretch with nearest
/// filtering: one generated 4×2 sheet of 16 px cells (a colour per cell, a white notch in the top-left corner that
/// shows flips, and one tick per frame number along the bottom). Sprites, ×4: "run" at 10 fps; flipped on both axes;
/// not centred with an offset; three times as fast; played backwards; uneven frame durations; a stopped sprite on
/// row 1's frame 2. Capture frames 1 and 15 differ exactly in the sprites whose frame changed.
/// With <c>--count N</c> it adds N small playing sprites in a grid instead (the allocation gate).
/// </summary>
public sealed class AnimatedSpriteScene(HostOptions host) : RenderTestGame(host)
{
    private const int Cell = 16;

    protected override void LoadScene()
    {
        Servers.Get<CanvasServer>()!.ClearColor = CanvasServer.GodotDefaultClearColor;
        Root.ContentScaleMode = ContentScaleMode.CanvasItems;
        Root.ContentScaleAspect = ContentScaleAspect.Expand;
        Root.ContentScaleSize = new Vector2(480, 270);
        var frames = Frames(Sheet());
        var root = new Node2D { Name = "Canvas", TextureFilter = CanvasTextureFilter.Nearest };

        if (Host.Count > 0)
        {
            for (var i = 0; i < Host.Count; i++)
            {
                var sprite = new AnimatedSprite2D
                {
                    Name = $"S{i}",
                    SpriteFrames = frames,
                    Position = new Vector2(8 + i % 40 * 12, 8 + i / 40 % 22 * 12),
                    Scale = new Vector2(0.5f),
                    SpeedScale = 0.5f + i % 5 * 0.5f,
                };
                root.AddChild(sprite);
                sprite.Play(i % 4 == 0 ? "once" : "run");
            }

            Tree.ChangeScene(root);
            return;
        }

        AnimatedSprite2D Add(string name, Vector2 position, string animation = "run")
        {
            var sprite = new AnimatedSprite2D { Name = name, SpriteFrames = frames, Animation = animation, Position = position, Scale = new Vector2(4) };
            root.AddChild(sprite);
            return sprite;
        }

        Add("Plain", new Vector2(60, 60)).Play();
        var flipped = Add("Flipped", new Vector2(160, 60));
        flipped.FlipH = true;
        flipped.FlipV = true;
        flipped.Play();
        var offset = Add("Offset", new Vector2(220, 28));
        offset.Centered = false;
        offset.Offset = new Vector2(4, 2);
        offset.Play();
        var fast = Add("Fast", new Vector2(380, 60));
        fast.SpeedScale = 3f;
        fast.Play();
        Add("Backwards", new Vector2(60, 180)).PlayBackwards("run");
        Add("Durations", new Vector2(160, 180), "uneven").Play();
        var stopped = Add("Stopped", new Vector2(260, 180), "once");
        stopped.Frame = 2;

        Tree.ChangeScene(root);
    }

    // "run": row 0 at 10 fps, looping; "once": row 1, not looping; "uneven": row 0 with durations 3, 1, 1, 1.
    private static SpriteFrames Frames(Texture2D sheet)
    {
        var frames = new SpriteFrames();
        var grid = new SpriteSheetGrid(new Vector2I(Cell, Cell));
        var run = frames.AddAnimation("run");
        run.Speed = 10f;
        run.AddFrames(sheet, grid, [new(0, 0), new(1, 0), new(2, 0), new(3, 0)]);
        var once = frames.AddAnimation("once");
        once.Speed = 10f;
        once.Loop = false;
        once.AddFrames(sheet, grid, [new(0, 1), new(1, 1), new(2, 1), new(3, 1)]);
        var uneven = frames.AddAnimation("uneven");
        uneven.Speed = 10f;
        uneven.Frames = [.. run.Frames.Select((f, i) => f with { Duration = i == 0 ? 3f : 1f })];
        uneven.Textures = run.Textures;
        return frames;
    }

    private static Texture2D Sheet()
    {
        const int w = Cell * 4, h = Cell * 2;
        var pixels = new byte[w * h * 4];
        (byte, byte, byte)[] colours =
        [
            (220, 60, 60), (240, 180, 40), (60, 200, 90), (60, 120, 230),
            (180, 70, 210), (40, 200, 200), (230, 120, 170), (150, 150, 60),
        ];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var (cx, cy, lx, ly) = (x / Cell, y / Cell, x % Cell, y % Cell);
                var index = cy * 4 + cx;
                var (r, g, b) = colours[index];
                var notch = lx is >= 1 and < 5 && ly is >= 1 and < 5;
                var tick = ly is >= 12 and < 14 && lx % 3 == 1 && lx / 3 <= cx;
                var border = lx == 0 || ly == 0 || lx == Cell - 1 || ly == Cell - 1;
                if (notch || tick)
                    (r, g, b) = (255, 255, 255);
                else if (border)
                    (r, g, b) = ((byte)(r / 2), (byte)(g / 2), (byte)(b / 2));
                var i = (y * w + x) * 4;
                (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (r, g, b, 255);
            }

        return Texture2D.FromPixels(w, h, pixels);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}
