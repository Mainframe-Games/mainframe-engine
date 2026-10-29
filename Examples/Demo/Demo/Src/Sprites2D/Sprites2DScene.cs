using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// Sprites 2D (ADR 0223): <see cref="AnimatedSprite2D"/>s playing a <see cref="SpriteFrames"/> sliced from one generated
/// sheet (<see cref="HeroArt"/>, <c>Assets/Sprites2D/hero.png</c> + <c>hero.mres</c>), drawn with nearest filtering:
/// a hero patrolling the ground (run, flipped at each turn), the showcase hero the panel drives (idle, run or jump,
/// speed, flip, backwards), and a row of coins spinning at different speeds, one of them backwards.
/// </summary>
public static class Sprites2DScene
{
    public const string SheetPath = "Assets/Sprites2D/hero.png";
    public const string FramesPath = "Assets/Sprites2D/hero.mres";

    public static Node Build()
    {
        var frames = ResourceLoader.Load<SpriteFrames>(FramesPath);
        var root = new Node2D { Name = "sprites_2d", TextureFilter = CanvasTextureFilter.Nearest };
        Add(root, root, new Camera2D { Name = "Camera", Current = true });
        Add(root, root, new DemoShape2D
        {
            Name = "Sky",
            Kind = DemoShapeKind.Rect,
            Size = new Vector2(4000, 2400),
            Color = new Vector4(0.16f, 0.2f, 0.34f, 1),
        });
        Add(root, root, new DemoShape2D
        {
            Name = "Ground",
            Kind = DemoShapeKind.Rect,
            Size = new Vector2(4000, 1200),
            Position = new Vector2(0, 760),
            Color = new Vector4(0.24f, 0.42f, 0.28f, 1),
            ColorBottom = new Vector4(0.12f, 0.22f, 0.16f, 1),
        });

        var showcase = Add(root, root, new AnimatedSprite2D
        {
            Name = "Showcase",
            SpriteFrames = frames,
            Animation = "idle",
            Autoplay = "idle",
            Position = new Vector2(-130, 160),
            Scale = new Vector2(8),
        });
        showcase.Offset = new Vector2(0, -14); // feet on the ground line

        var patrol = Add(root, root, new Patrol2D { Name = "Patrol", Position = new Vector2(240, 160), MinX = 120, MaxX = 540, Speed = 140 });
        Add(root, patrol, new AnimatedSprite2D
        {
            Name = "Sprite",
            SpriteFrames = frames,
            Animation = "run",
            Autoplay = "run",
            Scale = new Vector2(4),
            Offset = new Vector2(0, -14),
        });

        var coins = Add(root, root, new Node2D { Name = "Coins", Position = new Vector2(130, -150) });
        float[] speeds = [0.5f, 1f, 1.5f, 2f, -1f];
        for (var i = 0; i < speeds.Length; i++)
            Add(root, coins, new AnimatedSprite2D
            {
                Name = $"Coin{i}",
                SpriteFrames = frames,
                Animation = "coin",
                Autoplay = "coin",
                SpeedScale = speeds[i],
                Position = new Vector2(i * 90, 0),
                Scale = new Vector2(2.5f),
            });

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new Sprites2DPanel { Name = "Panel" });
        return root;
    }

    /// <summary>The sheet and its SpriteFrames (one animation per row), from <c>--write-scenes</c>.</summary>
    public static SpriteFrames CreateFrames(Texture2D sheet)
    {
        var frames = new SpriteFrames();
        var grid = new SpriteSheetGrid(new Vector2I(HeroArt.Cell, HeroArt.Cell));
        for (var row = 0; row < HeroArt.Animations.Length; row++)
        {
            var (name, speed, loop) = HeroArt.Animations[row];
            var animation = frames.AddAnimation(name);
            animation.Speed = speed;
            animation.Loop = loop;
            animation.AddFrames(sheet, grid, [new(0, row), new(1, row), new(2, row), new(3, row)]);
        }

        return frames;
    }

    /// <summary>
    /// Writes <c>hero.png</c> (+ its <c>.meta</c>) and <c>hero.mres</c> under <paramref name="assets"/> (the Demo's
    /// <c>Assets</c> folder), and copies them where the running game loads content from (the build output), so the
    /// scenes written next can load them before the next build.
    /// </summary>
    public static void WriteAssets(string assets)
    {
        var folder = Path.Combine(assets, "Sprites2D");
        var png = Path.Combine(folder, "hero.png");
        HeroArt.Write(png);
        if (!File.Exists(png + ".meta"))
            File.WriteAllText(png + ".meta", "{\n  \"uid\": \"tex_5f1d2c3b4a69\"\n}"); // a fixed UID: scenes reference it
        var output = Path.GetDirectoryName(ContentPaths.Resolve(SheetPath))!;
        Mirror(folder, output, "hero.png", "hero.png.meta");
        AssetDatabase.Current.Scan();
        var sheet = ResourceLoader.Load<Texture2D>(SheetPath);
        try
        {
            // Saved where the game loads it (so scenes reference it by its project path), then copied back to the source.
            var uid = ResourceSaver.Save(CreateFrames(sheet), FramesPath);
            Mirror(output, folder, "hero.mres");
            Log.Info($"Wrote hero.png and hero.mres ({uid})");
        }
        finally
        {
            sheet.Release();
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

/// <summary>Walks back and forth between <see cref="MinX"/> and <see cref="MaxX"/>, turning its sprite child to face the way it goes.</summary>
public sealed class Patrol2D : Node2D
{
    [Export] public float MinX { get; set; }
    [Export] public float MaxX { get; set; } = 400f;
    [Export(Range = "0,1000,1")] public float Speed { get; set; } = 120f;
    [Export] public bool Paused { get; set; }

    private float _direction = 1f;
    private AnimatedSprite2D? _sprite;

    protected override void OnReady() => _sprite = GetNodeOrNull<AnimatedSprite2D>("Sprite");

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Paused)
            return;
        var x = Position.X + _direction * Speed * gameTime.DeltaTime;
        if (x > MaxX || x < MinX)
        {
            _direction = -_direction;
            x = Math.Clamp(x, MinX, MaxX);
        }

        Position = new Vector2(x, Position.Y);
        if (_sprite is { } sprite)
            sprite.FlipH = _direction < 0;
    }
}
