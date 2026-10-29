namespace MainframeEngine.Editor;

/// <summary>
/// The SpriteFrames editor's Play / Play Backwards (ADR 0224): steps an edited <see cref="AnimatedSprite2D"/>'s
/// <see cref="AnimatedSprite2D.Frame"/> from the editor's frame tick — the node is not a tool node and does not
/// process in edit mode — with no undo entry and without dirtying the scene. Stop (or the end of a non-looping
/// animation) puts the frame back.
/// </summary>
public sealed class SpriteFramesPreview
{
    private AnimatedSprite2D? _sprite;
    private int _savedFrame;
    private float _savedProgress;
    private double _progress;

    /// <summary>Raised when a preview starts or ends (the Play/Stop buttons refresh).</summary>
    public event Action? Changed;

    /// <summary>The sprite being previewed, or null.</summary>
    public AnimatedSprite2D? Sprite => _sprite;

    /// <summary>True while playing backwards.</summary>
    public bool Backwards { get; private set; }

    public bool IsPlaying(AnimatedSprite2D? sprite) => sprite is not null && ReferenceEquals(_sprite, sprite);

    /// <summary>Starts previewing <paramref name="sprite"/>'s current animation (from its last frame when backwards).</summary>
    public void Play(AnimatedSprite2D sprite, bool backwards = false)
    {
        ArgumentNullException.ThrowIfNull(sprite);
        Stop();
        if (sprite.CurrentAnimation is not { FrameCount: > 0 } animation)
            return;
        _sprite = sprite;
        _savedFrame = sprite.Frame;
        _savedProgress = sprite.FrameProgress;
        Backwards = backwards;
        _progress = 0;
        sprite.Frame = backwards ? animation.FrameCount - 1 : 0;
        Changed?.Invoke();
    }

    /// <summary>Stops the preview and restores the frame it started from.</summary>
    public void Stop()
    {
        if (_sprite is not { } sprite)
            return;
        _sprite = null;
        if (!sprite.IsFreed)
        {
            sprite.Frame = _savedFrame;
            sprite.FrameProgress = _savedProgress;
        }

        Changed?.Invoke();
    }

    /// <summary>Advances the preview by <paramref name="delta"/> seconds (the editor's frame tick).</summary>
    public void Tick(double delta)
    {
        if (_sprite is not { } sprite)
            return;
        if (sprite.IsFreed || !sprite.IsInsideTree || sprite.CurrentAnimation is not { FrameCount: > 0 } animation)
        {
            Stop();
            return;
        }

        var frame = Math.Clamp(sprite.Frame, 0, animation.FrameCount - 1);
        var speed = animation.Speed * Math.Abs(sprite.SpeedScale == 0 ? 1f : sprite.SpeedScale);
        if (speed <= 0)
            return;
        _progress += delta * speed / animation.GetFrameDuration(frame);
        var steps = 0;
        while (_progress >= 1 && steps++ <= animation.FrameCount)
        {
            _progress -= 1;
            var next = frame + (Backwards ? -1 : 1);
            if (next < 0 || next >= animation.FrameCount)
            {
                if (!animation.Loop)
                {
                    Stop();
                    return;
                }

                next = Backwards ? animation.FrameCount - 1 : 0;
            }

            frame = next;
        }

        if (sprite.Frame != frame)
            sprite.Frame = frame;
    }
}
