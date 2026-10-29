using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>
/// ADR 0223: <see cref="AnimatedSprite2D"/> against Godot 4's <c>AnimatedSprite2D</c> — frame timing (durations,
/// negative speed, backwards, from the end), signal order on loop and finish, several frames in one long tick,
/// <see cref="AnimatedSprite2D.SetFrameAndProgress"/>, autoplay in edit mode, drawing and redraws, no allocation.
/// </summary>
public sealed class AnimatedSprite2DTests : IDisposable
{
    private readonly SceneTree _tree = new(new ServerRegistry());
    private readonly Texture2D _sheet = Texture2D.FromPixels(64, 32, new byte[64 * 32 * 4]);

    public void Dispose() => _tree.Shutdown();

    // "run": 4 frames of a 4×2 grid of 16 px cells (row 0) at 10 fps, looping; "jump": 3 frames of row 1, once.
    private SpriteFrames Frames(float[]? runDurations = null)
    {
        var frames = new SpriteFrames();
        var grid = SpriteSheetGrid.FromCounts(new Vector2I(64, 32), 4, 2);
        var run = frames.AddAnimation("run");
        run.Speed = 10f;
        run.AddFrames(_sheet, grid, [new(0, 0), new(1, 0), new(2, 0), new(3, 0)]);
        if (runDurations is not null)
            run.Frames = [.. run.Frames.Select((f, i) => f with { Duration = runDurations[i] })];
        var jump = frames.AddAnimation("jump");
        jump.Speed = 10f;
        jump.Loop = false;
        jump.AddFrames(_sheet, grid, [new(0, 1), new(1, 1), new(2, 1)]);
        return frames;
    }

    private AnimatedSprite2D Add(SpriteFrames frames, string animation = "run")
    {
        var sprite = new AnimatedSprite2D { SpriteFrames = frames, Animation = animation };
        _tree.Root.AddChild(sprite);
        return sprite;
    }

    private static List<string> Record(AnimatedSprite2D sprite)
    {
        var log = new List<string>();
        sprite.FrameChanged += () => log.Add($"frame {sprite.Frame}");
        sprite.AnimationLooped += () => log.Add("looped");
        sprite.AnimationFinished += () => log.Add("finished");
        sprite.AnimationChanged += () => log.Add($"animation {sprite.Animation}");
        return log;
    }


    [Fact]
    public void FramesAdvanceAtTheAnimationSpeedAndALongTickStepsSeveralFrames()
    {
        var sprite = Add(Frames());
        var log = Record(sprite);
        sprite.Play();
        Assert.True(sprite.IsPlaying());
        sprite.Advance(0.05f);
        Assert.Equal((0, 0.5f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());

        // 0.25 s: the rest of frame 0, all of frame 1, half of frame 2 — one FrameChanged per step.
        sprite.Advance(0.2f);
        Assert.Equal((2, 0.5f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
        Assert.Equal(["frame 1", "frame 2"], log);
    }

    [Fact]
    public void RelativeDurationsAndSpeedScaleChangeHowLongAFrameLasts()
    {
        var sprite = Add(Frames([1f, 2f, 1f, 1f]));
        sprite.Play();
        // At 60 Hz frame 0 lasts 0.1 s (6 ticks) and frame 1 (duration 2) 0.2 s (12 ticks): the tick a boundary falls
        // in shows the next frame (float rounding decides which tick that is), and in Godot's order the rest of that
        // tick runs at the old frame's rate, so frame 1 is shown for 11 to 13 ticks.
        var shown = new List<int>();
        for (var tick = 1; tick <= 20; tick++)
        {
            sprite.Advance(1.0 / 60.0);
            shown.Add(sprite.Frame);
        }

        Assert.InRange(shown.Count(f => f == 0), 5, 6);
        Assert.InRange(shown.Count(f => f == 1), 11, 13);
        Assert.Equal(2, shown[^1]);

        // Godot computes a step's speed before it moves to the next frame, so in one long step the first part of frame 1
        // still runs at frame 0's rate: 0.1 s (frame 0) + 0.1 s (frame 1 at rate 10) + 0.05 s of frame 2 at rate 5.
        sprite.Stop();
        sprite.Play();
        sprite.Advance(0.25);
        Assert.Equal((2, 0.25f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());

        sprite.SpeedScale = 2f;
        sprite.SetFrameAndProgress(0, 0.5f);
        sprite.Advance(0.025); // twice as fast: the second half of frame 0 takes 0.025 s
        Assert.Equal((0, 1f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
        Assert.Equal(2f, sprite.GetPlayingSpeed());
        sprite.Play("run", customSpeed: 0.5f);
        Assert.Equal(1f, sprite.GetPlayingSpeed()); // SpeedScale × the custom speed
    }

    [Fact]
    public void ALoopingAnimationWrapsWithLoopedBeforeFrameChanged()
    {
        var sprite = Add(Frames());
        sprite.Play();
        sprite.SetFrameAndProgress(3, 0.5f);
        var log = Record(sprite);
        sprite.Advance(0.1f);
        Assert.Equal(["looped", "frame 0"], log);
        Assert.Equal((0, 0.5f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
        Assert.True(sprite.IsPlaying());
    }

    [Fact]
    public void ANonLoopingAnimationFinishesOnItsLastFrameAndPauses()
    {
        var sprite = Add(Frames(), "jump");
        var log = Record(sprite);
        sprite.Play();
        sprite.Advance(0.5f);
        Assert.Equal(["frame 1", "frame 2", "finished"], log);
        Assert.Equal(2, sprite.Frame);
        Assert.False(sprite.IsPlaying());

        // Playing it again restarts it, because it had finished; Stop returns to the start.
        sprite.Play();
        Assert.Equal(0, sprite.Frame);
        sprite.Stop();
        Assert.Equal((0, 0f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
        Assert.False(sprite.IsPlaying());
    }

    [Fact]
    public void PlayBackwardsStartsAtTheEndAndFinishesAtTheFirstFrame()
    {
        var sprite = Add(Frames(), "run");
        var log = Record(sprite);
        sprite.PlayBackwards("jump");
        Assert.Equal((2, 1f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
        Assert.Equal(-1f, sprite.GetPlayingSpeed());
        Assert.Equal(["frame 2", "animation jump"], log); // Godot: the frame, then animation_changed
        log.Clear();

        sprite.Advance(0.05f);
        Assert.Equal((2, 0.5f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
        sprite.Advance(0.3f);
        Assert.Equal(["frame 1", "frame 0", "finished"], log);
        Assert.Equal(0, sprite.Frame);
        Assert.False(sprite.IsPlaying());

        // A negative speed scale also plays backwards, and setting Frame then starts at the frame's end.
        sprite.SpeedScale = -1f;
        sprite.Play("run");
        sprite.Frame = 2;
        Assert.Equal(1f, sprite.FrameProgress);
        sprite.Advance(0.15f);
        Assert.Equal((1, 0.5f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
    }

    [Fact]
    public void PlayFromEndAndResumingTheCurrentAnimation()
    {
        var sprite = Add(Frames());
        sprite.Play("jump", fromEnd: true);
        Assert.Equal((2, 1f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());

        sprite.Play("run");
        sprite.Advance(0.15f);
        sprite.Pause();
        sprite.Advance(1f);
        Assert.Equal((1, 0.5f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
        sprite.Play(); // resumes where it paused
        Assert.Equal((1, 0.5f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
    }

    [Fact]
    public void SetFrameAndProgressClampsAndSignalsOnlyChanges()
    {
        var sprite = Add(Frames());
        var log = Record(sprite);
        sprite.SetFrameAndProgress(0, 0.3f);
        Assert.Empty(log);
        sprite.SetFrameAndProgress(9, 0.2f);
        Assert.Equal((3, 0.2f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
        sprite.Frame = -4;
        Assert.Equal((0, 0f), (sprite.Frame, sprite.FrameProgress), new FrameComparer());
        Assert.Equal(["frame 3", "frame 0"], log);
    }

    [Fact]
    public void SwitchingAnimationsAndResourcesKeepsGodotsRules()
    {
        var frames = Frames();
        var sprite = new AnimatedSprite2D { Animation = "jump" };
        sprite.SpriteFrames = frames;
        Assert.Equal("jump", sprite.Animation); // the resource has it

        var other = new SpriteFrames();
        other.AddAnimation("idle").AddFrame(_sheet);
        var changed = 0;
        sprite.SpriteFramesChanged += () => changed++;
        sprite.SpriteFrames = other;
        Assert.Equal("idle", sprite.Animation); // missing: the first animation
        Assert.Equal(1, changed);

        _tree.Root.AddChild(sprite);
        sprite.Play("nope"); // a warning, nothing else
        Assert.False(sprite.IsPlaying());
        Assert.Equal("idle", sprite.Animation);
    }

    [Fact]
    public void AutoplayRunsInAGameButNotInEditMode()
    {
        var game = new AnimatedSprite2D { SpriteFrames = Frames(), Autoplay = "run" };
        _tree.Root.AddChild(game);
        Assert.True(game.IsPlaying());
        Assert.Equal("run", game.Animation);

        var editor = new SceneTree(new ServerRegistry()) { EditMode = true };
        try
        {
            var edited = new AnimatedSprite2D { SpriteFrames = Frames(), Autoplay = "run" };
            editor.Root.AddChild(edited);
            Assert.False(edited.IsPlaying());
        }
        finally
        {
            editor.Shutdown();
        }
    }

    [Fact]
    public void DrawsTheFrameRegionCentredOffsetAndFlipped()
    {
        var sprite = Add(Frames());
        sprite.Frame = 2; // cell (2, 0): texture pixels 32..48 × 0..16
        sprite.Offset = new Vector2(3, 4);
        _tree.FlushCanvasRedraws();
        var v = sprite.DrawList.Vertices;
        Assert.Equal(4, v.Length);
        Assert.Equal(new Vector2(-5, -4), v[0].Position); // centred: −8 + 3, −8 + 4
        Assert.Equal(new Vector2(11, 12), v[2].Position);
        Assert.Equal(new Vector2(0.5f, 0f), v[0].Uv);
        Assert.Equal(new Vector2(0.75f, 0.5f), v[2].Uv);
        Assert.Equal(new Rect2(-5, -4, 16, 16), sprite.GetRect());

        sprite.FlipH = true;
        sprite.Centered = false;
        _tree.FlushCanvasRedraws();
        v = sprite.DrawList.Vertices;
        var topLeft = v.ToArray().Single(x => x.Position == new Vector2(3, 4));
        Assert.Equal(new Vector2(0.75f, 0f), topLeft.Uv); // mirrored: the left edge samples the cell's right edge
        Assert.Equal(new Rect2(3, 4, 16, 16), sprite.GetRect());

        // A frame without a region draws the whole texture; no resource draws nothing.
        var whole = new SpriteFrames();
        whole.AddAnimation("default").AddFrame(_sheet);
        sprite.SpriteFrames = whole;
        Assert.Equal(new Rect2(3, 4, 64, 32), sprite.GetRect());
        sprite.SpriteFrames = null;
        _tree.FlushCanvasRedraws();
        Assert.Equal(0, sprite.DrawList.VertexCount);
        Assert.Equal(default, sprite.GetRect());
    }

    [Fact]
    public void RedrawsOnlyWhenTheFrameChangesAndWhenTheResourceIsEdited()
    {
        var frames = Frames();
        var sprite = Add(frames);
        var draws = 0;
        sprite.Draw += () => draws++;
        sprite.Play();
        _tree.FlushCanvasRedraws();
        draws = 0;
        for (var i = 0; i < 15; i++) // 0.25 s at 60 Hz: two frame changes (at 0.1 and 0.2 s)
        {
            _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
            _tree.FlushCanvasRedraws();
        }

        Assert.Equal(2, draws);

        // Editing the playing animation's frames in place redraws (the resource's Changed).
        var run = frames.GetAnimation("run")!;
        run.Frames = [.. Enumerable.Reverse(run.Frames)];
        _tree.FlushCanvasRedraws();
        Assert.Equal(3, draws);
    }

    [Fact]
    public void AThousandPlayingSpritesAllocateNothing()
    {
        var frames = Frames();
        var sprites = new List<AnimatedSprite2D>();
        for (var i = 0; i < 1000; i++)
        {
            var sprite = Add(frames, i % 3 == 0 ? "jump" : "run");
            sprite.SpeedScale = 0.5f + i % 7 * 0.25f;
            sprite.FrameChanged += static () => { };
            sprite.Play();
            sprites.Add(sprite);
        }

        void Frame()
        {
            for (var i = 0; i < sprites.Count; i += 97)
                if (!sprites[i].IsPlaying())
                    sprites[i].Play(i % 2 == 0 ? "run" : "jump");
            _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
            _tree.FlushCanvasRedraws();
        }

        for (var f = 0; f < 120; f++)
            Frame();
        Assert.Equal(0, AllocationGate.SmallestWindow(() =>
        {
            for (var f = 0; f < 300; f++)
                Frame();
        }));
    }

    private sealed class FrameComparer : IEqualityComparer<(int Frame, float Progress)>
    {
        public bool Equals((int Frame, float Progress) a, (int Frame, float Progress) b) =>
            a.Frame == b.Frame && MathF.Abs(a.Progress - b.Progress) < 1e-3f;

        public int GetHashCode((int Frame, float Progress) obj) => obj.Frame;
    }
}
