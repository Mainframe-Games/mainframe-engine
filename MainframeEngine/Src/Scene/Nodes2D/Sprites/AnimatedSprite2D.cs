using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A flipbook sprite (Godot 4's <c>AnimatedSprite2D</c>, ADR 0223): plays the animations of a <see cref="SpriteFrames"/>
/// with Godot's timing and signals, and draws the current frame like <see cref="Sprite2D"/> (centred or offset, flips).
/// Each frame lasts <c>Duration / (Speed × |playing speed|)</c> seconds; a long delta steps several frames, raising
/// <see cref="FrameChanged"/> once per step. Playback allocates nothing; the node redraws only when its frame changes.
/// It is not a tool node: in the editor it shows <see cref="Animation"/>/<see cref="Frame"/> statically and
/// <see cref="Autoplay"/> does not run.
/// </summary>
[EditorIcon("slideshow", Family = EditorIconFamily.Space2D)]
public class AnimatedSprite2D : Node2D
{
    private SpriteFrames? _spriteFrames;
    private SpriteAnimation? _bound; // the animation whose Changed we listen to
    private Action? _resourceChanged;
    private string _animation = SpriteFrames.DefaultAnimation;
    private string _autoplay = "";
    private int _frame;
    private float _frameProgress;
    private float _speedScale = 1f;
    private float _customSpeedScale = 1f;
    private bool _playing;
    private bool _centered = true;
    private Vector2 _offset;
    private bool _flipH, _flipV;

    /// <summary>The animations this sprite plays. Setting it stops playback and keeps <see cref="Animation"/> if the new
    /// resource has it (else its first animation).</summary>
    [Export]
    public SpriteFrames? SpriteFrames
    {
        get => _spriteFrames;
        set
        {
            if (ReferenceEquals(_spriteFrames, value))
                return;
            _resourceChanged ??= OnResourceChanged;
            if (_spriteFrames is not null)
                _spriteFrames.Changed -= _resourceChanged;
            Stop();
            _spriteFrames = value;
            if (value is not null)
            {
                value.Changed += _resourceChanged;
                // Godot picks the first animation when the current one is missing. An empty resource keeps the names
                // (Godot clears them; its SpriteFrames always start with "default", ours get it from the editor).
                if (value.Animations.Count > 0 && value.Animations[0] is { } first)
                {
                    if (!value.HasAnimation(_animation))
                        Animation = first.Name;
                    if (_autoplay.Length > 0 && !value.HasAnimation(_autoplay))
                        _autoplay = "";
                }
            }

            Rebind();
            QueueRedraw();
            SpriteFramesChanged?.Invoke();
        }
    }

    /// <summary>The current animation's name. Setting it restarts at the first frame (the last when playing backwards).</summary>
    [Export]
    public string Animation
    {
        get => _animation;
        set
        {
            value ??= "";
            if (string.Equals(_animation, value, StringComparison.Ordinal))
                return;
            _animation = value;
            Rebind();
            AnimationChanged?.Invoke();
            if (_spriteFrames is null)
                return; // kept for when SpriteFrames is set (scene loading order)
            var count = _bound?.FrameCount ?? 0;
            if (value.Length == 0 || count == 0)
            {
                Stop();
                return;
            }

            if (float.IsNegative(GetPlayingSpeed()))
                SetFrameAndProgress(count - 1, 1f);
            else
                SetFrameAndProgress(0, 0f);
            QueueRedraw();
        }
    }

    /// <summary>The animation played when the node is ready in a running game ("" = none).</summary>
    [Export]
    public string Autoplay
    {
        get => _autoplay;
        set => _autoplay = value ?? "";
    }

    /// <summary>The displayed frame of <see cref="Animation"/>. Setting it resets <see cref="FrameProgress"/>.</summary>
    [Export]
    public int Frame
    {
        get => _frame;
        set => SetFrameAndProgress(value, float.IsNegative(GetPlayingSpeed()) ? 1f : 0f);
    }

    /// <summary>How far through the current frame playback is, 0…1.</summary>
    [Export(Range = "0,1,0.0001")]
    public float FrameProgress
    {
        get => _frameProgress;
        set => _frameProgress = value;
    }

    /// <summary>Multiplies the animation's speed; negative plays backwards.</summary>
    [Export(Range = "-8,8,0.01")]
    public float SpeedScale
    {
        get => _speedScale;
        set => _speedScale = value;
    }

    /// <summary>Centres the frame on the node's origin (otherwise its top-left corner is at <see cref="Offset"/>).</summary>
    [Export]
    public bool Centered
    {
        get => _centered;
        set => Set(ref _centered, value);
    }

    [Export]
    public Vector2 Offset
    {
        get => _offset;
        set => Set(ref _offset, value);
    }

    [Export]
    public bool FlipH
    {
        get => _flipH;
        set => Set(ref _flipH, value);
    }

    [Export]
    public bool FlipV
    {
        get => _flipV;
        set => Set(ref _flipV, value);
    }

    /// <summary>Raised when <see cref="Animation"/> changes.</summary>
    [Signal]
    public event Action? AnimationChanged;

    /// <summary>Raised when a non-looping animation reaches its end (the first frame when playing backwards).</summary>
    [Signal]
    public event Action? AnimationFinished;

    /// <summary>Raised when a looping animation wraps around.</summary>
    [Signal]
    public event Action? AnimationLooped;

    /// <summary>Raised when <see cref="Frame"/> changes.</summary>
    [Signal]
    public event Action? FrameChanged;

    /// <summary>Raised when <see cref="SpriteFrames"/> changes.</summary>
    [Signal]
    public event Action? SpriteFramesChanged;

    /// <summary>
    /// Plays <paramref name="name"/> (null or "": <see cref="Animation"/>) at <paramref name="customSpeed"/> × the speed
    /// scale; <paramref name="fromEnd"/> starts at the last frame. Playing the current animation again resumes it (and
    /// restarts it only when it had finished). Godot's <c>play</c>.
    /// </summary>
    public void Play(string? name = null, float customSpeed = 1f, bool fromEnd = false)
    {
        if (string.IsNullOrEmpty(name))
            name = _animation;
        if (_spriteFrames is null)
        {
            Log.Warning($"[AnimatedSprite2D] '{Name}': Play('{name}') without SpriteFrames.");
            return;
        }

        var animation = _spriteFrames.GetAnimation(name);
        if (animation is null)
        {
            Log.Warning($"[AnimatedSprite2D] '{Name}': there is no animation named '{name}'.");
            return;
        }

        if (animation.FrameCount == 0)
            return;
        _playing = true;
        _customSpeedScale = customSpeed;
        var end = Math.Max(0, animation.FrameCount - 1);
        if (!string.Equals(name, _animation, StringComparison.Ordinal))
        {
            _animation = name;
            Rebind();
            if (fromEnd)
                SetFrameAndProgress(end, 1f);
            else
                SetFrameAndProgress(0, 0f);
            AnimationChanged?.Invoke();
        }
        else
        {
            var backward = float.IsNegative(_speedScale * _customSpeedScale);
            if (fromEnd && backward && _frame == 0 && _frameProgress <= 0f)
                SetFrameAndProgress(end, 1f);
            else if (!fromEnd && !backward && _frame == end && _frameProgress >= 1f)
                SetFrameAndProgress(0, 0f);
        }

        QueueRedraw();
    }

    /// <summary>Plays <paramref name="name"/> backwards from its end (Godot's <c>play_backwards</c>).</summary>
    public void PlayBackwards(string? name = null) => Play(name, -1f, true);

    /// <summary>Stops advancing and keeps the frame and progress (<see cref="Play"/> resumes).</summary>
    public void Pause() => _playing = false;

    /// <summary>Stops and returns to frame 0 with no progress and the custom speed reset.</summary>
    public void Stop()
    {
        _playing = false;
        _customSpeedScale = 1f;
        SetFrameAndProgress(0, 0f);
    }

    public bool IsPlaying() => _playing;

    /// <summary>Sets the frame (clamped to the animation) and progress at once; raises <see cref="FrameChanged"/> when the frame changes.</summary>
    public void SetFrameAndProgress(int frame, float progress)
    {
        var animation = CurrentAnimation;
        var changed = _frame != frame; // Godot compares the requested frame, before clamping
        if (frame < 0)
            _frame = 0;
        else if (animation is not null && frame > Math.Max(0, animation.FrameCount - 1))
            _frame = Math.Max(0, animation.FrameCount - 1);
        else
            _frame = frame;
        _frameProgress = progress;
        if (!changed)
            return;
        QueueRedraw();
        FrameChanged?.Invoke();
    }

    /// <summary>The signed playback speed multiplier: <see cref="SpeedScale"/> × the custom speed while playing, else 0.</summary>
    public float GetPlayingSpeed() => _playing ? _speedScale * _customSpeedScale : 0f;

    /// <summary>
    /// The drawn rectangle in local space (Godot's <c>get_rect</c>): empty without a drawable frame, 1×1 for an empty
    /// region. Editor picking uses it.
    /// </summary>
    public Rect2 GetRect()
    {
        if (CurrentAnimation is not { } animation || !animation.TryGetFrame(_frame, out _, out var region))
            return default;
        var size = region.Size;
        var offset = _offset;
        if (_centered)
            offset -= size / 2f;
        if (size == Vector2.Zero)
            size = Vector2.One;
        return new Rect2(offset, size);
    }

    /// <summary>The <see cref="SpriteAnimation"/> named <see cref="Animation"/>, or null.</summary>
    public SpriteAnimation? CurrentAnimation => _spriteFrames?.GetAnimation(_animation);

    protected override void OnReady()
    {
        base.OnReady();
        if (Tree is { EditMode: false } && _autoplay.Length > 0 && _spriteFrames?.HasAnimation(_autoplay) == true)
            Play(_autoplay);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        base.OnProcess(gameTime);
        if (_playing)
            Advance(gameTime.DeltaTime);
    }

    /// <summary>Godot's <c>AnimatedSprite2D::_notification(INTERNAL_PROCESS)</c>: advances by <paramref name="delta"/> seconds.</summary>
    internal void Advance(double delta)
    {
        var remaining = delta;
        var steps = 0;
        while (remaining > 0 && _playing)
        {
            // Signal handlers may change the animation, its speed or its frames: read them again every step.
            if (CurrentAnimation is not { } animation)
                return;
            var count = animation.FrameCount;
            if (count == 0)
                return;
            // As Godot, the speed is taken before a frame step: the rest of a step that starts a frame runs at the
            // previous frame's rate (it only shows with uneven durations and long deltas).
            var speed = (double)animation.Speed * _speedScale * _customSpeedScale / animation.GetFrameDuration(_frame);
            if (speed == 0)
                return;
            var absSpeed = Math.Abs(speed);
            var last = count - 1;
            if (!double.IsNegative(speed))
            {
                if (_frameProgress >= 1f)
                {
                    if (_frame >= last)
                    {
                        if (animation.Loop)
                        {
                            _frame = 0;
                            AnimationLooped?.Invoke();
                        }
                        else
                        {
                            _frame = last;
                            Pause();
                            AnimationFinished?.Invoke();
                            return;
                        }
                    }
                    else
                    {
                        _frame++;
                    }

                    _frameProgress = 0f;
                    QueueRedraw();
                    FrameChanged?.Invoke();
                }

                var toProcess = Math.Min((1.0 - _frameProgress) / absSpeed, remaining);
                _frameProgress = (float)(_frameProgress + toProcess * absSpeed);
                remaining -= toProcess;
            }
            else
            {
                if (_frameProgress <= 0f)
                {
                    if (_frame <= 0)
                    {
                        if (animation.Loop)
                        {
                            _frame = last;
                            AnimationLooped?.Invoke();
                        }
                        else
                        {
                            _frame = 0;
                            Pause();
                            AnimationFinished?.Invoke();
                            return;
                        }
                    }
                    else
                    {
                        _frame--;
                    }

                    _frameProgress = 1f;
                    QueueRedraw();
                    FrameChanged?.Invoke();
                }

                var toProcess = Math.Min(_frameProgress / absSpeed, remaining);
                _frameProgress = (float)(_frameProgress - toProcess * absSpeed);
                remaining -= toProcess;
            }

            if (++steps > count)
                return; // as Godot: a tiny step per iteration must not spin
        }
    }

    private protected override void DrawBuiltin()
    {
        if (CurrentAnimation is not { } animation || !animation.TryGetFrame(_frame, out var texture, out var region))
            return;
        var dst = SpriteRects.Destination(region.Size, _offset, _centered, _flipH, _flipV);
        CanvasPrimitives.TextureRectRegion(DrawList, texture, dst, region, Vector4.One, false);
    }

    // Godot's _res_changed: the frames may have changed under the current frame.
    private void OnResourceChanged()
    {
        Rebind();
        SetFrameAndProgress(_frame, _frameProgress);
        QueueRedraw();
    }

    // Listens to the current animation's own Changed (its frames, speed or name edited in place).
    private void Rebind()
    {
        var animation = CurrentAnimation;
        if (ReferenceEquals(animation, _bound))
            return;
        _resourceChanged ??= OnResourceChanged;
        if (_bound is not null)
            _bound.Changed -= _resourceChanged;
        _bound = animation;
        if (animation is not null)
            animation.Changed += _resourceChanged;
    }

    private void Set<T>(ref T field, T value)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        QueueRedraw();
    }
}
