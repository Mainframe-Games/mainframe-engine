using System.Text;
using System.Text.Json;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// ADR 0223: <see cref="SpriteFrames"/> as a <c>.mres</c> (the sheet referenced once, one line per frame through the
/// built-in <see cref="SpriteFrame"/> codec), name lookups, and an <see cref="AnimatedSprite2D"/> in a scene.
/// </summary>
[Collection(nameof(Scene.SerialResources))]
public sealed class SpriteFramesSerializationTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-sprites-" + Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public SpriteFramesSerializationTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder, "Sprites"));
        Png.WriteRgba8(Path.Combine(_project, AssetDatabase.ContentFolder, "Sprites", "hero.png"), 128, 32, new byte[128 * 32 * 4]);
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previous;
        Directory.Delete(_project, recursive: true);
    }

    private static SpriteFrames Hero(Texture2D sheet)
    {
        var frames = new SpriteFrames();
        var run = frames.AddAnimation("run");
        run.Speed = 10f;
        run.AddFrames(sheet, new SpriteSheetGrid(new Vector2I(32, 32)), [new(0, 0), new(1, 0), new(2, 0), new(3, 0)]);
        run.Frames[3] = run.Frames[3] with { Duration = 2f };
        var idle = frames.AddAnimation("idle");
        idle.Loop = false;
        idle.AddFrame(sheet, new Rect2(0, 0, 32, 32));
        return frames;
    }

    [Fact]
    public void AResourceFileKeepsOneTextureReferenceAndOneLinePerFrame()
    {
        var sheet = ResourceLoader.Load<Texture2D>("Assets/Sprites/hero.png");
        var uid = ResourceSaver.Save(Hero(sheet), "Assets/Sprites/hero.mres");
        var text = File.ReadAllText(Path.Combine(_project, "Assets", "Sprites", "hero.mres"));

        Assert.Contains("\"type\": \"SpriteFrames\"", text, StringComparison.Ordinal);
        Assert.Contains("\"path\": \"Assets/Sprites/hero.png\"", text, StringComparison.Ordinal);
        Assert.Contains("[0, 96, 0, 32, 32, 2]", text, StringComparison.Ordinal); // [texture, x, y, w, h, duration]
        Assert.Contains("[0, 0, 0, 32, 32, 1]", text, StringComparison.Ordinal);
        using (var json = JsonDocument.Parse(text))
            Assert.Single(json.RootElement.GetProperty("resources").EnumerateObject(), r => r.Value.TryGetProperty("path", out _));

        sheet.Release();
        ResourceLoader.ClearCache();
        var loaded = ResourceLoader.Load<SpriteFrames>(uid);
        Assert.Equal(["run", "idle"], loaded.Animations.Select(a => a.Name));
        var run = loaded.GetAnimation("run")!;
        Assert.Equal(10f, run.Speed);
        Assert.True(run.Loop);
        Assert.False(loaded.GetAnimation("idle")!.Loop);
        Assert.Equal(4, run.FrameCount);
        Assert.Equal(new SpriteFrame(0, new Rect2(96, 0, 32, 32), 2f), run.Frames[3]);
        Assert.Same(run.Textures[0], loaded.GetAnimation("idle")!.Textures[0]); // one cached sheet
        Assert.True(run.TryGetFrame(1, out var texture, out var region));
        Assert.Equal((128, new Rect2(32, 0, 32, 32)), (texture.Width, region));

        // Saving the loaded resource again writes the same bytes.
        ResourceSaver.Save(loaded, "Assets/Sprites/hero.mres");
        Assert.Equal(text, File.ReadAllText(Path.Combine(_project, "Assets", "Sprites", "hero.mres")));
        loaded.Release();
    }

    [Fact]
    public void ASceneSpriteRoundTripsWithItsPlaybackSettings()
    {
        var sheet = ResourceLoader.Load<Texture2D>("Assets/Sprites/hero.png");
        var root = new Node2D { Name = "Root" };
        var sprite = new AnimatedSprite2D
        {
            Name = "Hero",
            SpriteFrames = Hero(sheet),
            Animation = "run",
            Autoplay = "run",
            Frame = 2,
            FrameProgress = 0.25f,
            SpeedScale = 1.5f,
            Centered = false,
            Offset = new System.Numerics.Vector2(4, -8),
            FlipH = true,
        };
        root.AddChild(sprite);
        sprite.Owner = root;
        var json = SceneSaver.ToJson(root);
        root.Free();

        var copy = PackedScene.Parse(json).Instantiate().GetNode<AnimatedSprite2D>("Hero");
        Assert.Equal(("run", "run", 2, 0.25f, 1.5f), (copy.Animation, copy.Autoplay, copy.Frame, copy.FrameProgress, copy.SpeedScale));
        Assert.False(copy.Centered);
        Assert.True(copy.FlipH);
        Assert.Equal(new System.Numerics.Vector2(4, -8), copy.Offset);
        Assert.Equal(4, copy.SpriteFrames!.GetFrameCount("run"));
        Assert.Contains("[0, 64, 0, 32, 32, 1]", Encoding.UTF8.GetString(json), StringComparison.Ordinal);
        copy.Free();
        sheet.Release();
    }

    [Fact]
    public void TheCodecWritesSixNumbersAndRejectsOtherShapes()
    {
        var codec = Codecs.Get<SpriteFrame>();
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
            codec.Write(writer, new SpriteFrame(1, new Rect2(2, 3, 4, 5), 0.5f), null!);
        Assert.Equal("[1,2,3,4,5,0.5]", Encoding.UTF8.GetString(buffer.ToArray()));
        using var doc = JsonDocument.Parse("[1, 2, 3]");
        Assert.Throws<JsonException>(() => codec.Read(doc.RootElement, null!));
    }

    [Fact]
    public void LookupsFollowRenamesRemovalsAndInPlaceEdits()
    {
        var frames = new SpriteFrames();
        var run = frames.AddAnimation("run");
        Assert.Same(run, frames.GetAnimation("run"));
        Assert.Throws<ArgumentException>(() => frames.AddAnimation("run"));

        frames.RenameAnimation("run", "sprint");
        Assert.Null(frames.GetAnimation("run"));
        Assert.Same(run, frames.GetAnimation("sprint"));

        run.Name = "dash"; // renamed on the animation itself: the cache notices
        Assert.Same(run, frames.GetAnimation("dash"));
        Assert.False(frames.HasAnimation("sprint"));

        var jump = new SpriteAnimation { Name = "jump" };
        frames.Animations.Insert(0, jump); // edited in place
        Assert.Same(jump, frames.GetAnimation("jump"));
        Assert.Same(run, frames.GetAnimation("dash"));

        Assert.True(frames.RemoveAnimation("dash"));
        Assert.False(frames.RemoveAnimation("dash"));
        Assert.Equal(0, frames.GetFrameCount("dash"));
        Assert.Null(frames.GetAnimation(null));
    }
}
