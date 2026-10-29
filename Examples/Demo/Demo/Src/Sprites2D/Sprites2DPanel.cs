using System.Globalization;
using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>
/// The Sprites 2D scene's controls (ADR 0223): the showcase hero's animation (idle, run, jump), played forwards or
/// backwards and flipped; one speed scale for every sprite; pausing everything. It shows the showcase's frame and how
/// often its animation looped (the <see cref="AnimatedSprite2D.FrameChanged"/> and
/// <see cref="AnimatedSprite2D.AnimationLooped"/> signals).
/// </summary>
public sealed class Sprites2DPanel : UiDocument
{
    private readonly List<AnimatedSprite2D> _sprites = [];
    private readonly List<float> _baseSpeeds = []; // each sprite's speed scale as built (the coins differ, one is negative)
    private RmlDataModel? _model;
    private AnimatedSprite2D? _showcase;
    private Patrol2D? _patrol;
    private string _animation = "idle";
    private float _speed = 1f;
    private bool _paused, _backwards;
    private int _loops;
    private string _frame = "";

    public Sprites2DPanel()
    {
        Source = "Assets/Sprites2D/panel.rml";
        AutoFocus = false;
    }

    /// <summary>The showcase hero's animation.</summary>
    public string Animation
    {
        get => _animation;
        set
        {
            _animation = value;
            _loops = 0;
            Restart();
            _model?.Dirty("loops");
        }
    }

    /// <summary>Multiplies every sprite's speed scale as built (the coins keep their relative speeds, one backwards).</summary>
    public float Speed
    {
        get => _speed;
        set
        {
            _speed = Math.Clamp(value, 0f, 4f);
            for (var i = 0; i < _sprites.Count; i++)
                _sprites[i].SpeedScale = _baseSpeeds[i] * _speed;
        }
    }

    /// <summary>Plays the showcase backwards.</summary>
    public bool Backwards
    {
        get => _backwards;
        set
        {
            _backwards = value;
            Restart();
        }
    }

    public bool Flip
    {
        get => _showcase?.FlipH ?? false;
        set
        {
            if (_showcase is { } showcase)
                showcase.FlipH = value;
        }
    }

    /// <summary>Pauses every sprite and the patrol.</summary>
    public bool Paused
    {
        get => _paused;
        set
        {
            _paused = value;
            foreach (var sprite in _sprites)
                if (value)
                    sprite.Pause();
                else if (!ReferenceEquals(sprite, _showcase))
                    sprite.Play();
            if (!value)
                Restart();
            if (_patrol is { } patrol)
                patrol.Paused = value;
        }
    }

    /// <summary>"Frame 2 / 4" of the showcase.</summary>
    public string FrameText => _frame;

    public int Loops => _loops;

    public AnimatedSprite2D? Showcase => _showcase;

    protected override void OnReady()
    {
        var scene = Parent!.Parent!;
        _showcase = scene.GetNode<AnimatedSprite2D>("Showcase");
        _patrol = scene.GetNodeOrNull<Patrol2D>("Patrol");
        Collect(scene);
        _showcase.FrameChanged += OnFrameChanged;
        _showcase.AnimationLooped += OnLooped;
        UpdateFrameText();
        _model = CreateDataModel("sprites2d")
            .Bind("animation", this, static d => d._animation, static (d, v) => d.Animation = v)
            .Bind("speed", this, static d => d._speed, static (d, v) => d.Speed = v)
            .Bind("backwards", this, static d => d._backwards, static (d, v) => d.Backwards = v)
            .Bind("flip", this, static d => d.Flip, static (d, v) => d.Flip = v)
            .Bind("paused", this, static d => d._paused, static (d, v) => d.Paused = v)
            .Bind("frame", this, static d => d._frame)
            .Bind("loops", this, static d => d._loops);
    }

    protected override void OnExitTree()
    {
        if (_showcase is { } showcase)
        {
            showcase.FrameChanged -= OnFrameChanged;
            showcase.AnimationLooped -= OnLooped;
        }

        _showcase = null;
        _patrol = null;
        _sprites.Clear();
        _baseSpeeds.Clear();
        base.OnExitTree();
    }

    private void Collect(Node node)
    {
        if (node is AnimatedSprite2D sprite)
        {
            _sprites.Add(sprite);
            _baseSpeeds.Add(sprite.SpeedScale);
        }

        foreach (var child in node.Children)
            Collect(child);
    }

    private void Restart()
    {
        if (_showcase is not { } showcase || _paused)
            return;
        if (_backwards)
            showcase.PlayBackwards(_animation);
        else
            showcase.Play(_animation);
        UpdateFrameText();
    }

    private void OnFrameChanged()
    {
        UpdateFrameText();
        _model?.Dirty("frame");
    }

    private void OnLooped()
    {
        _loops++;
        _model?.Dirty("loops");
    }

    private void UpdateFrameText()
    {
        if (_showcase is not { } showcase)
            return;
        var count = showcase.SpriteFrames?.GetFrameCount(showcase.Animation) ?? 0;
        _frame = $"{(showcase.Frame + 1).ToString(CultureInfo.InvariantCulture)} / {count.ToString(CultureInfo.InvariantCulture)}";
    }
}
