using System.Numerics;
using System.Reflection;

namespace MainframeEngine;

/// <summary>
/// Animates properties, waits and calls over time (a port of Godot 4.7's <c>Tween</c>): steps of tweeners run one after
/// another (<see cref="Chain"/>) or together (<see cref="Parallel"/>, <see cref="SetParallel"/>), each step finishing when its
/// slowest tweener does, the time left over carried into the next step; <see cref="SetLoops"/> repeats the whole sequence.
/// Create one with <see cref="Node.CreateTween"/> (bound to the node: it pauses with it and dies when it is freed) or
/// <see cref="SceneTree.CreateTween"/>. The tree steps tweens after timers, in process or physics frames
/// (<see cref="SetProcessMode"/>). Property paths are Godot's: <c>"modulate:a"</c>, <c>"position:x"</c>, <c>"rotation"</c>
/// (snake_case names map to the C# properties; <c>:x/:y/:z/:w</c> and <c>:r/:g/:b/:a</c> index vector components).
/// </summary>
public sealed class Tween
{
    /// <summary>Godot's <c>Tween.TransitionType</c>.</summary>
    public enum TransitionType
    {
        Linear,
        Sine,
        Quint,
        Quart,
        Quad,
        Expo,
        Elastic,
        Cubic,
        Circ,
        Bounce,
        Back,
        Spring,
    }

    /// <summary>Godot's <c>Tween.EaseType</c>.</summary>
    public enum EaseType
    {
        In,
        Out,
        InOut,
        OutIn,
    }

    /// <summary>Godot's <c>Tween.TweenProcessMode</c>.</summary>
    public enum TweenProcessMode
    {
        Physics,
        Idle,
    }

    /// <summary>Godot's <c>Tween.TweenPauseMode</c>.</summary>
    public enum TweenPauseMode
    {
        /// <summary>Pauses with the bound node (or the tree, when unbound).</summary>
        Bound,
        Stop,
        Process,
    }

    private readonly List<List<Tweener>> _tweeners = [];
    private Node? _bound;
    private bool _parallelEnabled;
    private bool _defaultParallel;
    private int _currentStep;
    private int _loops = 1;
    private int _loopsDone;
    private bool _started;
    private bool _dead;

    internal Tween(SceneTree tree) => Tree = tree;

    public SceneTree Tree { get; }

    public TransitionType DefaultTransition { get; private set; } = TransitionType.Linear;

    public EaseType DefaultEase { get; private set; } = EaseType.InOut;

    public TweenProcessMode ProcessMode { get; private set; } = TweenProcessMode.Idle;

    public TweenPauseMode PauseMode { get; private set; } = TweenPauseMode.Bound;

    public float SpeedScale { get; private set; } = 1f;

    public bool Running { get; private set; } = true;

    /// <summary>Seconds this tween has run (scaled).</summary>
    public double TotalElapsedTime { get; private set; }

    /// <summary>Raised when every loop has finished (never for infinite loops).</summary>
    public event Action? Finished;

    /// <summary>Raised when a step (a group of parallel tweeners) finishes, with its index.</summary>
    public event Action<int>? StepFinished;

    /// <summary>Raised after each loop but the last, with the number done.</summary>
    public event Action<int>? LoopFinished;

    /// <summary>False once the tween finished or was killed (Godot's <c>is_valid</c>).</summary>
    public bool IsValid() => !_dead;

    public bool IsRunning() => Running && !_dead;

    /// <summary>Animates <paramref name="target"/>'s <paramref name="property"/> (Godot path) to <paramref name="finalValue"/>.</summary>
    public PropertyTweener TweenProperty(object target, string property, object finalValue, double duration)
    {
        ArgumentNullException.ThrowIfNull(target);
        var tweener = new PropertyTweener(this, target, property, finalValue, duration);
        Append(tweener);
        return tweener;
    }

    public IntervalTweener TweenInterval(double time)
    {
        var tweener = new IntervalTweener(this, time);
        Append(tweener);
        return tweener;
    }

    public CallbackTweener TweenCallback(Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var tweener = new CallbackTweener(this, callback);
        Append(tweener);
        return tweener;
    }

    /// <summary>Calls <paramref name="method"/> with a value eased from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public MethodTweener TweenMethod(Action<float> method, float from, float to, double duration)
    {
        ArgumentNullException.ThrowIfNull(method);
        var tweener = new MethodTweener(this, method, from, to, duration);
        Append(tweener);
        return tweener;
    }

    /// <summary>Binds the tween to <paramref name="node"/>: it pauses with it and dies when the node is freed.</summary>
    public Tween BindNode(Node node)
    {
        _bound = node ?? throw new ArgumentNullException(nameof(node));
        return this;
    }

    public Tween SetTrans(TransitionType trans)
    {
        DefaultTransition = trans;
        return this;
    }

    public Tween SetEase(EaseType ease)
    {
        DefaultEase = ease;
        return this;
    }

    /// <summary>Tweeners appended from now on run together (<c>true</c>) or one after another.</summary>
    public Tween SetParallel(bool parallel = true)
    {
        _defaultParallel = _parallelEnabled = parallel;
        return this;
    }

    /// <summary>The next tweener runs with the previous one.</summary>
    public Tween Parallel()
    {
        _parallelEnabled = true;
        return this;
    }

    /// <summary>The next tweener runs after the previous step.</summary>
    public Tween Chain()
    {
        _parallelEnabled = false;
        return this;
    }

    /// <summary>Repeats the sequence <paramref name="loops"/> times; 0 = forever.</summary>
    public Tween SetLoops(int loops = 0)
    {
        _loops = loops;
        return this;
    }

    public Tween SetSpeedScale(float speed)
    {
        SpeedScale = speed;
        return this;
    }

    public Tween SetProcessMode(TweenProcessMode mode)
    {
        ProcessMode = mode;
        return this;
    }

    public Tween SetPauseMode(TweenPauseMode mode)
    {
        PauseMode = mode;
        return this;
    }

    public void Pause() => Running = false;

    public void Play() => Running = true;

    /// <summary>Stops the tween for good (it is removed from the tree).</summary>
    public void Kill()
    {
        Running = false;
        _dead = true;
    }

    private void Append(Tweener tweener)
    {
        if (_parallelEnabled && _tweeners.Count > 0)
            _tweeners[^1].Add(tweener);
        else
            _tweeners.Add([tweener]);
        _parallelEnabled = _defaultParallel;
    }

    internal bool CanProcess(bool treePaused)
    {
        if (_bound is not null && !Node.IsInstanceValid(_bound))
            return true; // Godot: a freed bound node lets the step run, which ends the tween
        if (_bound is not null && PauseMode == TweenPauseMode.Bound)
            return _bound.IsInsideTree && _bound.CanProcess(treePaused);
        return !treePaused || PauseMode == TweenPauseMode.Process;
    }

    /// <summary>Advances by <paramref name="delta"/> seconds; false when the tween is finished (Godot's <c>Tween::step</c>).</summary>
    internal bool Step(double delta)
    {
        if (_dead)
            return false;
        if (_bound is not null)
        {
            if (!Node.IsInstanceValid(_bound))
                return false;
            if (!_bound.IsInsideTree)
                return true;
        }

        if (!Running)
            return true;
        if (!_started)
        {
            if (_tweeners.Count == 0)
            {
                Log.Error($"[Tween] {(_bound is null ? "A tween" : $"A tween bound to '{_bound.Name}'")} started with no tweeners.");
                return false;
            }

            _currentStep = 0;
            _loopsDone = 0;
            TotalElapsedTime = 0;
            StartTweeners();
            _started = true;
        }

        var remaining = delta * SpeedScale;
        TotalElapsedTime += remaining;
        var potentialInfinite = false;
        var initial = remaining;
        while (Running && remaining > 0)
        {
            var stepDelta = remaining;
            var stepActive = false;
            foreach (var tweener in _tweeners[_currentStep])
            {
                var t = remaining;
                stepActive = tweener.Step(ref t) || stepActive;
                stepDelta = Math.Min(t, stepDelta);
            }

            remaining = stepDelta;
            if (stepActive)
                continue;
            StepFinished?.Invoke(_currentStep);
            _currentStep++;
            if (_currentStep == _tweeners.Count)
            {
                _loopsDone++;
                if (_loopsDone == _loops)
                {
                    Running = false;
                    _dead = true;
                    Finished?.Invoke();
                    break;
                }

                LoopFinished?.Invoke(_loopsDone);
                _currentStep = 0;
                StartTweeners();
                if (_loops <= 0 && Math.Abs(remaining - initial) < 1e-9)
                {
                    if (potentialInfinite)
                    {
                        Log.Error("[Tween] Infinite loop detected (every tweener takes no time); stopping the tween.");
                        Kill();
                        return false;
                    }

                    potentialInfinite = true;
                }
            }
            else
            {
                StartTweeners();
            }
        }

        return true;
    }

    private void StartTweeners()
    {
        foreach (var tweener in _tweeners[_currentStep])
            tweener.Start();
    }

    // ---- equations (Godot's scene/animation/easing_equations.h) ----

    /// <summary>Godot's <c>Tween.interpolate_value</c> for a float: <paramref name="initial"/> + eased(<paramref name="delta"/>).</summary>
    public static float InterpolateValue(float initial, float delta, double elapsed, double duration, TransitionType trans, EaseType ease) =>
        duration == 0 ? initial + delta : Equation(trans, ease, (float)elapsed, initial, delta, (float)duration);

    private static float Equation(TransitionType trans, EaseType ease, float t, float b, float c, float d)
    {
        switch (trans)
        {
            case TransitionType.Linear:
                return c * t / d + b;
            case TransitionType.Bounce:
            case TransitionType.Spring:
                {
                    Func<float, float, float, float, float> outF = trans == TransitionType.Bounce ? BounceOut : SpringOut;
                    float InF(float t2, float b2, float c2, float d2) => c2 - outF(d2 - t2, 0, c2, d2) + b2;
                    return ease switch
                    {
                        EaseType.In => InF(t, b, c, d),
                        EaseType.Out => outF(t, b, c, d),
                        EaseType.InOut => t < d / 2 ? InF(t * 2, b, c / 2, d) : outF(t * 2 - d, b + c / 2, c / 2, d),
                        _ => t < d / 2 ? outF(t * 2, b, c / 2, d) : InF(t * 2 - d, b + c / 2, c / 2, d),
                    };
                }
        }

        if (ease == EaseType.OutIn)
            return t < d / 2 ? Equation(trans, EaseType.Out, t * 2, b, c / 2, d) : Equation(trans, EaseType.In, t * 2 - d, b + c / 2, c / 2, d);

        return (trans, ease) switch
        {
            (TransitionType.Sine, EaseType.In) => -c * MathF.Cos(t / d * (MathF.PI / 2)) + c + b,
            (TransitionType.Sine, EaseType.Out) => c * MathF.Sin(t / d * (MathF.PI / 2)) + b,
            (TransitionType.Sine, _) => -c / 2 * (MathF.Cos(MathF.PI * t / d) - 1) + b,
            (TransitionType.Quint, EaseType.In) => c * MathF.Pow(t / d, 5) + b,
            (TransitionType.Quint, EaseType.Out) => c * (MathF.Pow(t / d - 1, 5) + 1) + b,
            (TransitionType.Quint, _) => PowInOut(t, b, c, d, 5),
            (TransitionType.Quart, EaseType.In) => c * MathF.Pow(t / d, 4) + b,
            (TransitionType.Quart, EaseType.Out) => -c * (MathF.Pow(t / d - 1, 4) - 1) + b,
            (TransitionType.Quart, _) => QuartInOut(t, b, c, d),
            (TransitionType.Quad, EaseType.In) => c * MathF.Pow(t / d, 2) + b,
            (TransitionType.Quad, EaseType.Out) => QuadOut(t, b, c, d),
            (TransitionType.Quad, _) => QuadInOut(t, b, c, d),
            (TransitionType.Expo, EaseType.In) => t == 0 ? b : c * MathF.Pow(2, 10 * (t / d - 1)) + b - c * 0.001f,
            (TransitionType.Expo, EaseType.Out) => t == d ? b + c : c * 1.001f * (-MathF.Pow(2, -10 * t / d) + 1) + b,
            (TransitionType.Expo, _) => ExpoInOut(t, b, c, d),
            (TransitionType.Elastic, EaseType.In) => ElasticIn(t, b, c, d),
            (TransitionType.Elastic, EaseType.Out) => ElasticOut(t, b, c, d),
            (TransitionType.Elastic, _) => ElasticInOut(t, b, c, d),
            (TransitionType.Cubic, EaseType.In) => CubicIn(t, b, c, d),
            (TransitionType.Cubic, EaseType.Out) => CubicOut(t, b, c, d),
            (TransitionType.Cubic, _) => CubicInOut(t, b, c, d),
            (TransitionType.Circ, EaseType.In) => CircIn(t, b, c, d),
            (TransitionType.Circ, EaseType.Out) => CircOut(t, b, c, d),
            (TransitionType.Circ, _) => CircInOut(t, b, c, d),
            (TransitionType.Back, EaseType.In) => BackIn(t, b, c, d),
            (TransitionType.Back, EaseType.Out) => BackOut(t, b, c, d),
            _ => BackInOut(t, b, c, d),
        };
    }

    private static float PowInOut(float t, float b, float c, float d, int p)
    {
        t = t / d * 2;
        return t < 1 ? c / 2 * MathF.Pow(t, p) + b : c / 2 * (MathF.Pow(t - 2, p) + 2) + b;
    }

    private static float QuartInOut(float t, float b, float c, float d)
    {
        t = t / d * 2;
        return t < 1 ? c / 2 * MathF.Pow(t, 4) + b : -c / 2 * (MathF.Pow(t - 2, 4) - 2) + b;
    }

    private static float QuadOut(float t, float b, float c, float d)
    {
        t /= d;
        return -c * t * (t - 2) + b;
    }

    private static float QuadInOut(float t, float b, float c, float d)
    {
        t = t / d * 2;
        return t < 1 ? c / 2 * MathF.Pow(t, 2) + b : -c / 2 * ((t - 1) * (t - 3) - 1) + b;
    }

    private static float ExpoInOut(float t, float b, float c, float d)
    {
        if (t == 0)
            return b;
        if (t == d)
            return b + c;
        t = t / d * 2;
        return t < 1 ? c / 2 * MathF.Pow(2, 10 * (t - 1)) + b - c * 0.0005f : c / 2 * 1.0005f * (-MathF.Pow(2, -10 * (t - 1)) + 2) + b;
    }

    private static float ElasticIn(float t, float b, float c, float d)
    {
        if (t == 0)
            return b;
        t /= d;
        if (t == 1)
            return b + c;
        t -= 1;
        var p = d * 0.3f;
        var a = c * MathF.Pow(2, 10 * t);
        var s = p / 4;
        return -(a * MathF.Sin((t * d - s) * (2 * MathF.PI) / p)) + b;
    }

    private static float ElasticOut(float t, float b, float c, float d)
    {
        if (t == 0)
            return b;
        t /= d;
        if (t == 1)
            return b + c;
        var p = d * 0.3f;
        var s = p / 4;
        return c * MathF.Pow(2, -10 * t) * MathF.Sin((t * d - s) * (2 * MathF.PI) / p) + c + b;
    }

    private static float ElasticInOut(float t, float b, float c, float d)
    {
        if (t == 0)
            return b;
        if ((t /= d / 2) == 2)
            return b + c;
        var p = d * (0.3f * 1.5f);
        var a = c;
        var s = p / 4;
        if (t < 1)
        {
            t -= 1;
            a *= MathF.Pow(2, 10 * t);
            return -0.5f * (a * MathF.Sin((t * d - s) * (2 * MathF.PI) / p)) + b;
        }

        t -= 1;
        a *= MathF.Pow(2, -10 * t);
        return a * MathF.Sin((t * d - s) * (2 * MathF.PI) / p) * 0.5f + c + b;
    }

    private static float CubicIn(float t, float b, float c, float d)
    {
        t /= d;
        return c * t * t * t + b;
    }

    private static float CubicOut(float t, float b, float c, float d)
    {
        t = t / d - 1;
        return c * (t * t * t + 1) + b;
    }

    private static float CubicInOut(float t, float b, float c, float d)
    {
        t /= d / 2;
        if (t < 1)
            return c / 2 * t * t * t + b;
        t -= 2;
        return c / 2 * (t * t * t + 2) + b;
    }

    private static float CircIn(float t, float b, float c, float d)
    {
        t /= d;
        return -c * (MathF.Sqrt(1 - t * t) - 1) + b;
    }

    private static float CircOut(float t, float b, float c, float d)
    {
        t = t / d - 1;
        return c * MathF.Sqrt(1 - t * t) + b;
    }

    private static float CircInOut(float t, float b, float c, float d)
    {
        t /= d / 2;
        if (t < 1)
            return -c / 2 * (MathF.Sqrt(1 - t * t) - 1) + b;
        t -= 2;
        return c / 2 * (MathF.Sqrt(1 - t * t) + 1) + b;
    }

    private static float BounceOut(float t, float b, float c, float d)
    {
        t /= d;
        if (t < 1 / 2.75f)
            return c * (7.5625f * t * t) + b;
        if (t < 2 / 2.75f)
        {
            t -= 1.5f / 2.75f;
            return c * (7.5625f * t * t + 0.75f) + b;
        }

        if (t < 2.5 / 2.75)
        {
            t -= 2.25f / 2.75f;
            return c * (7.5625f * t * t + 0.9375f) + b;
        }

        t -= 2.625f / 2.75f;
        return c * (7.5625f * t * t + 0.984375f) + b;
    }

    private static float SpringOut(float t, float b, float c, float d)
    {
        t /= d;
        var s = 1f - t;
        t = (MathF.Sin(t * MathF.PI * (0.2f + 2.5f * t * t * t)) * MathF.Pow(s, 2.2f) + t) * (1f + 1.2f * s);
        return c * t + b;
    }

    private static float BackIn(float t, float b, float c, float d)
    {
        const float s = 1.70158f;
        t /= d;
        return c * t * t * ((s + 1) * t - s) + b;
    }

    private static float BackOut(float t, float b, float c, float d)
    {
        const float s = 1.70158f;
        t = t / d - 1;
        return c * (t * t * ((s + 1) * t + s) + 1) + b;
    }

    private static float BackInOut(float t, float b, float c, float d)
    {
        const float s = 1.70158f * 1.525f;
        t /= d / 2;
        if (t < 1)
            return c / 2 * (t * t * ((s + 1) * t - s)) + b;
        t -= 2;
        return c / 2 * (t * t * ((s + 1) * t + s) + 2) + b;
    }
}

/// <summary>One animation inside a <see cref="Tween"/> step.</summary>
public abstract class Tweener
{
    private protected Tweener(Tween tween) => Tween = tween;

    protected Tween Tween { get; }

    protected double Elapsed { get; set; }

    protected bool IsFinished { get; set; }

    /// <summary>Raised when this tweener is done.</summary>
    public event Action? Finished;

    internal virtual void Start()
    {
        Elapsed = 0;
        IsFinished = false;
    }

    /// <summary>Consumes delta; leaves in <paramref name="delta"/> the time it did not use; false once done.</summary>
    internal abstract bool Step(ref double delta);

    protected void Finish()
    {
        IsFinished = true;
        Finished?.Invoke();
    }
}

/// <summary>Animates a property (Godot's <c>PropertyTweener</c>).</summary>
public sealed class PropertyTweener : Tweener
{
    private readonly WeakReference<object> _target;
    private readonly TweenPropertyPath _path;
    private readonly object _baseFinal;
    private readonly double _duration;
    private object _initial;
    private object _final;
    private float[] _delta = [];
    private Tween.TransitionType? _trans;
    private Tween.EaseType? _ease;
    private double _delay;
    private bool _relative;
    private bool _continue = true;
    private bool _continueDelayed;

    internal PropertyTweener(Tween tween, object target, string property, object finalValue, double duration) : base(tween)
    {
        _target = new WeakReference<object>(target);
        _path = TweenPropertyPath.Parse(target.GetType(), property);
        _initial = _path.Get(target);
        _baseFinal = TweenValues.Coerce(finalValue, _initial);
        _final = _baseFinal;
        _duration = duration;
    }

    /// <summary>Starts from <paramref name="value"/> instead of the value when the tweener starts.</summary>
    public PropertyTweener From(object value)
    {
        if (_target.TryGetTarget(out var target))
        {
            _initial = TweenValues.Coerce(value, _path.Get(target));
            _continue = false;
        }

        return this;
    }

    public PropertyTweener FromCurrent()
    {
        _continue = false;
        return this;
    }

    /// <summary>The final value is added to the start value.</summary>
    public PropertyTweener AsRelative()
    {
        _relative = true;
        return this;
    }

    public PropertyTweener SetTrans(Tween.TransitionType trans)
    {
        _trans = trans;
        return this;
    }

    public PropertyTweener SetEase(Tween.EaseType ease)
    {
        _ease = ease;
        return this;
    }

    public PropertyTweener SetDelay(double delay)
    {
        _delay = delay;
        return this;
    }

    internal override void Start()
    {
        base.Start();
        if (!_target.TryGetTarget(out var target))
            return;
        if (_continue)
        {
            if (_delay == 0)
                _initial = _path.Get(target);
            else
                _continueDelayed = true;
        }

        if (_relative)
            _final = TweenValues.Add(_initial, _baseFinal);
        _delta = TweenValues.Subtract(_final, _initial);
    }

    internal override bool Step(ref double delta)
    {
        if (IsFinished)
            return false;
        if (!_target.TryGetTarget(out var target) || target is Node { IsFreed: true })
        {
            Finish();
            return false;
        }

        Elapsed += delta;
        if (Elapsed < _delay)
        {
            delta = 0;
            return true;
        }

        if (_continueDelayed && _delay != 0)
        {
            _initial = _path.Get(target);
            _delta = TweenValues.Subtract(_final, _initial);
            _continueDelayed = false;
        }

        var time = Math.Min(Elapsed - _delay, _duration);
        if (time < _duration)
        {
            _path.Set(target, TweenValues.Interpolate(_initial, _delta, time, _duration, _trans ?? Tween.DefaultTransition, _ease ?? Tween.DefaultEase));
            delta = 0;
            return true;
        }

        _path.Set(target, _final);
        delta = Elapsed - _delay - _duration;
        Finish();
        return false;
    }
}

/// <summary>Waits (Godot's <c>IntervalTweener</c>).</summary>
public sealed class IntervalTweener : Tweener
{
    private readonly double _duration;

    internal IntervalTweener(Tween tween, double duration) : base(tween) => _duration = duration;

    internal override bool Step(ref double delta)
    {
        if (IsFinished)
            return false;
        Elapsed += delta;
        if (Elapsed < _duration)
        {
            delta = 0;
            return true;
        }

        delta = Elapsed - _duration;
        Finish();
        return false;
    }
}

/// <summary>Calls a method once (Godot's <c>CallbackTweener</c>).</summary>
public sealed class CallbackTweener : Tweener
{
    private readonly Action _callback;
    private double _delay;

    internal CallbackTweener(Tween tween, Action callback) : base(tween) => _callback = callback;

    public CallbackTweener SetDelay(double delay)
    {
        _delay = delay;
        return this;
    }

    internal override bool Step(ref double delta)
    {
        if (IsFinished)
            return false;
        Elapsed += delta;
        if (Elapsed >= _delay)
        {
            _callback();
            delta = Elapsed - _delay;
            Finish();
            return false;
        }

        delta = 0;
        return true;
    }
}

/// <summary>Calls a method with an eased value every step (Godot's <c>MethodTweener</c>).</summary>
public sealed class MethodTweener : Tweener
{
    private readonly Action<float> _method;
    private readonly float _from;
    private readonly float _to;
    private readonly double _duration;
    private Tween.TransitionType? _trans;
    private Tween.EaseType? _ease;
    private double _delay;

    internal MethodTweener(Tween tween, Action<float> method, float from, float to, double duration) : base(tween)
    {
        _method = method;
        _from = from;
        _to = to;
        _duration = duration;
    }

    public MethodTweener SetTrans(Tween.TransitionType trans)
    {
        _trans = trans;
        return this;
    }

    public MethodTweener SetEase(Tween.EaseType ease)
    {
        _ease = ease;
        return this;
    }

    public MethodTweener SetDelay(double delay)
    {
        _delay = delay;
        return this;
    }

    internal override bool Step(ref double delta)
    {
        if (IsFinished)
            return false;
        Elapsed += delta;
        if (Elapsed < _delay)
        {
            delta = 0;
            return true;
        }

        var time = Math.Min(Elapsed - _delay, _duration);
        if (time < _duration)
        {
            _method(Tween.InterpolateValue(_from, _to - _from, time, _duration, _trans ?? Tween.DefaultTransition, _ease ?? Tween.DefaultEase));
            delta = 0;
            return true;
        }

        _method(_to);
        delta = Elapsed - _delay - _duration;
        Finish();
        return false;
    }
}

/// <summary>A Godot property path on a C# object: <c>"self_modulate"</c> → <c>SelfModulate</c>, <c>":a"</c> → a component.</summary>
internal sealed class TweenPropertyPath
{
    private readonly PropertyInfo _property;
    private readonly int _component; // −1: the whole value

    private TweenPropertyPath(PropertyInfo property, int component)
    {
        _property = property;
        _component = component;
    }

    public static TweenPropertyPath Parse(Type type, string path)
    {
        var colon = path.IndexOf(':', StringComparison.Ordinal);
        var name = colon < 0 ? path : path[..colon];
        var pascal = string.Concat(name.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..]));
        var property = type.GetProperty(pascal, BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new ArgumentException($"{type.Name} has no property '{name}' ({pascal}) to tween.", nameof(path));
        var component = colon < 0 ? -1 : path[(colon + 1)..] switch
        {
            "x" or "r" => 0,
            "y" or "g" => 1,
            "z" or "b" => 2,
            "w" or "a" => 3,
            var sub => throw new ArgumentException($"Unknown sub-property ':{sub}' in '{path}'.", nameof(path)),
        };
        return new TweenPropertyPath(property, component);
    }

    public object Get(object target)
    {
        var value = _property.GetValue(target)!;
        return _component < 0 ? value : TweenValues.Components(value)[_component];
    }

    public void Set(object target, object value)
    {
        if (_component < 0)
        {
            _property.SetValue(target, TweenValues.ToType(value, _property.PropertyType));
            return;
        }

        var whole = _property.GetValue(target)!;
        var parts = TweenValues.Components(whole);
        parts[_component] = Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture);
        _property.SetValue(target, TweenValues.FromComponents(parts, _property.PropertyType));
    }
}

/// <summary>Component-wise arithmetic for tweened values: float, double, int and vector-like structs of floats.</summary>
internal static class TweenValues
{
    public static object Coerce(object value, object like) => like switch
    {
        float => Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture),
        double => Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
        int => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
        _ when value.GetType() == like.GetType() => value,
        _ => FromComponents(Components(value), like.GetType()),
    };

    public static float[] Components(object value) => value switch
    {
        float f => [f],
        double d => [(float)d],
        int i => [i],
        Vector2 v => [v.X, v.Y],
        Vector3 v => [v.X, v.Y, v.Z],
        Vector4 v => [v.X, v.Y, v.Z, v.W],
        _ => FieldsOf(value),
    };

    // Game structs (a Godot Color or Vector2 port): their public float fields in declaration order.
    private static float[] FieldsOf(object value)
    {
        var fields = value.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType == typeof(float)).ToArray();
        if (fields.Length == 0)
            throw new NotSupportedException($"Cannot tween a {value.GetType().Name}.");
        return [.. fields.Select(f => (float)f.GetValue(value)!)];
    }

    public static object FromComponents(float[] c, Type type)
    {
        if (type == typeof(float))
            return c[0];
        if (type == typeof(double))
            return (double)c[0];
        if (type == typeof(int))
            return (int)MathF.Round(c[0]);
        if (type == typeof(Vector2))
            return new Vector2(c[0], c[1]);
        if (type == typeof(Vector3))
            return new Vector3(c[0], c[1], c[2]);
        if (type == typeof(Vector4))
            return new Vector4(c[0], c[1], c[2], c.Length > 3 ? c[3] : 1f);
        var boxed = Activator.CreateInstance(type)!;
        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance).Where(f => f.FieldType == typeof(float)).ToArray();
        for (var i = 0; i < fields.Length && i < c.Length; i++)
            fields[i].SetValue(boxed, c[i]);
        return boxed;
    }

    public static object ToType(object value, Type type) => value.GetType() == type ? value : FromComponents(Components(value), type);

    public static float[] Subtract(object a, object b)
    {
        var x = Components(a);
        var y = Components(b);
        var r = new float[x.Length];
        for (var i = 0; i < r.Length; i++)
            r[i] = x[i] - y[i];
        return r;
    }

    public static object Add(object a, object b)
    {
        var x = Components(a);
        var y = Components(b);
        for (var i = 0; i < x.Length; i++)
            x[i] += y[i];
        return FromComponents(x, a.GetType());
    }

    public static object Interpolate(object initial, float[] delta, double time, double duration, Tween.TransitionType trans, Tween.EaseType ease)
    {
        var x = Components(initial);
        for (var i = 0; i < x.Length; i++)
            x[i] = Tween.InterpolateValue(x[i], delta[i], time, duration, trans, ease);
        return FromComponents(x, initial.GetType());
    }
}
