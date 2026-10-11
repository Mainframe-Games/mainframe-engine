using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// How <see cref="GpuParticles2D"/> particles start and move (the subset of Godot's <c>ParticleProcessMaterial</c> games use
/// in 2D): emission point or box, direction with a spread, an initial speed range, gravity, a linear acceleration range along
/// the velocity, damping, a start angle, spin and scale range, a scale curve, one colour and a colour ramp over the
/// particle's life, and lifetime randomness (ADR 0187).
/// </summary>
[EditorIcon("droplet")]
public sealed class ParticleProcessMaterial : Resource
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
        Justification = "Godot's name (ParticleProcessMaterial.EmissionShapeEnum), kept for ported games.")]
    public enum EmissionShapeEnum
    {
        Point,
        Sphere,
        SphereSurface,
        Box,
    }

    [Export] public EmissionShapeEnum EmissionShape { get; set; } = EmissionShapeEnum.Point;

    /// <summary>Half size of the emission box (z ignored in 2D).</summary>
    [Export] public Vector3 EmissionBoxExtents { get; set; } = Vector3.One;

    [Export] public float EmissionSphereRadius { get; set; } = 1f;

    /// <summary>Unit direction of the initial velocity (z ignored).</summary>
    [Export] public Vector3 Direction { get; set; } = Vector3.UnitX;

    /// <summary>Degrees either side of <see cref="Direction"/>.</summary>
    [Export(Range = "0,180,0.1")] public float Spread { get; set; } = 45f;

    [Export] public float InitialVelocityMin { get; set; }
    [Export] public float InitialVelocityMax { get; set; }

    /// <summary>Pixels per second² (Godot's default pulls 98 down; 2D is Y-down).</summary>
    [Export] public Vector3 Gravity { get; set; } = new(0f, 98f, 0f);

    /// <summary>Acceleration along the velocity, chosen per particle between these.</summary>
    [Export] public float LinearAccelMin { get; set; }
    [Export] public float LinearAccelMax { get; set; }

    /// <summary>Start rotation in degrees, chosen per particle between these.</summary>
    [Export] public float AngleMin { get; set; }
    [Export] public float AngleMax { get; set; }

    [Export] public float ScaleMin { get; set; } = 1f;
    [Export] public float ScaleMax { get; set; } = 1f;

    /// <summary>Speed lost per second (pixels/s²), chosen per particle between these (Godot's <c>damping</c>; ADR 0187).</summary>
    [Export] public float DampingMin { get; set; }
    [Export] public float DampingMax { get; set; }

    /// <summary>Spin in degrees per second, chosen per particle between these (Godot's <c>angular_velocity</c>).</summary>
    [Export] public float AngularVelocityMin { get; set; }
    [Export] public float AngularVelocityMax { get; set; }

    /// <summary>
    /// Multiplies the scale over the particle's life (Godot's <c>scale_curve</c>, a <see cref="Curve"/> here rather than a
    /// CurveTexture).
    /// </summary>
    [Export] public Curve? ScaleCurve { get; set; }

    /// <summary>
    /// Multiplies <see cref="Color"/> over the particle's life (Godot's <c>color_ramp</c>, a <see cref="Gradient"/> here
    /// rather than a GradientTexture1D): sampled at age / lifetime, converted like <see cref="Color"/>.
    /// </summary>
    [Export] public Gradient? ColorRamp { get; set; }

    /// <summary>Each particle lives between (1 − this) × lifetime and the full lifetime (Godot's <c>lifetime_randomness</c>).</summary>
    [Export(Range = "0,1,0.01")] public float LifetimeRandomness { get; set; }

    /// <summary>
    /// Colour multiplied into every particle. Godot's <c>color_value</c> is a <c>source_color</c> uniform, converted to
    /// linear before it reaches the canvas, which then shows those values as they are: (1, 0.9, 0.5) draws as about
    /// (1, 0.79, 0.21). The particles do the same.
    /// </summary>
    [Export] public Vector4 Color { get; set; } = Vector4.One;

    /// <summary>
    /// Godot's <c>particle_flag_disable_z</c>: the angle turns the particle in the plane. Off (Godot's default) the angle
    /// turns it about the 3D Y axis, which in 2D only narrows it by cos(angle).
    /// </summary>
    [Export] public bool ParticleFlagDisableZ { get; set; }

    /// <summary>Godot's <c>particle_flag_align_y</c> (with <see cref="ParticleFlagDisableZ"/>): the particle's Y follows its velocity.</summary>
    [Export] public bool ParticleFlagAlignY { get; set; }
}

/// <summary>
/// A 2D particle emitter (Godot's <c>GPUParticles2D</c> with a <see cref="ParticleProcessMaterial"/>, simulated on the CPU):
/// <see cref="Amount"/> particles, each restarting every <see cref="Lifetime"/> seconds at an even phase squeezed by
/// <see cref="Explosiveness"/> (1: all at once), only the first <see cref="AmountRatio"/> of them emitting; <see cref="Preprocess"/>
/// seconds are simulated when emission starts. <see cref="OneShot"/> emits each particle once, then stops emitting and
/// raises <see cref="Finished"/> (ADR 0187). With <see cref="LocalCoords"/> off particles stay where they were emitted in the world. Each particle is the
/// <see cref="Texture"/> centred on it, rotated, scaled and tinted. Random streams differ from Godot's GPU ones.
/// </summary>
[EditorIcon("star", Family = EditorIconFamily.Space2D)]
public class GpuParticles2D : Node2D
{
    private struct Particle
    {
        public bool Alive;
        public float Age;
        public Vector2 Position;
        public Vector2 Velocity;
        public float Accel;
        public float Angle;
        public float Scale;
        public float Damping;
        public float Spin;
        public float Life;
    }

    private Particle[] _particles = [];
    private float[] _phase = [];
    private Random _random = new(1);
    private bool _emitting = true;
    private double _time;
    private float _phaseLifetime = -1f;
    private float _phaseExplosiveness;

    [Export] public Texture2D? Texture { get; set; }

    [Export] public ParticleProcessMaterial? ProcessMaterial { get; set; }

    [Export] public int Amount { get; set; } = 8;

    [Export] public float Lifetime { get; set; } = 1f;

    /// <summary>Fraction (0..1) of <see cref="Amount"/> that emits.</summary>
    [Export(Range = "0,1,0.01")] public float AmountRatio { get; set; } = 1f;

    /// <summary>Seconds simulated at once when emission starts.</summary>
    [Export] public float Preprocess { get; set; }

    [Export] public bool LocalCoords { get; set; }

    /// <summary>Emit each particle once, then stop (Godot's <c>one_shot</c>); <see cref="Restart"/> or setting <see cref="Emitting"/> fires again.</summary>
    [Export] public bool OneShot { get; set; }

    /// <summary>0 spreads emission over the lifetime, 1 emits every particle at once (Godot's <c>explosiveness</c>).</summary>
    [Export(Range = "0,1,0.01")] public float Explosiveness { get; set; }

    /// <summary>Raised when a <see cref="OneShot"/> emitter's last particle has died (Godot's <c>finished</c>).</summary>
    [Signal] public event Action? Finished;

    /// <summary>Godot's culling rectangle (kept for reference; particles are not culled).</summary>
    [Export] public Rect2 VisibilityRect { get; set; } = new(new Vector2(-100, -100), new Vector2(200, 200));

    /// <summary>Particles alive now (tests, diagnostics).</summary>
    public int LiveCount
    {
        get
        {
            var count = 0;
            foreach (ref readonly var p in _particles.AsSpan())
                if (p.Alive)
                    count++;
            return count;
        }
    }

    [Export]
    public bool Emitting
    {
        get => _emitting;
        set
        {
            if (value == _emitting)
                return;
            _emitting = value;
            if (value && OneShot)
            {
                _time = 0;   // a one-shot fires again from the start
                return;
            }

            if (value && LiveCount == 0 && Preprocess > 0)
                Simulate(Preprocess);
        }
    }

    /// <summary>Kills every particle and starts emitting from the start (Godot's <c>restart</c>); no allocation once sized.</summary>
    public void Restart()
    {
        Array.Clear(_particles);
        _time = 0;
        _emitting = true;
        if (Preprocess > 0)
            Simulate(Preprocess);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        base.OnProcess(gameTime);
        Simulate(gameTime.DeltaTime);
        QueueRedraw();
    }

    private void Simulate(float seconds)
    {
        if (Amount <= 0 || Lifetime <= 0)
            return;
        if (_particles.Length != Amount || _phaseLifetime != Lifetime || _phaseExplosiveness != Explosiveness)
        {
            if (_particles.Length != Amount)
                _particles = new Particle[Amount];
            if (_phase.Length != Amount)
                _phase = new float[Amount];
            var spread = 1f - Math.Clamp(Explosiveness, 0f, 1f);
            for (var i = 0; i < Amount; i++)
                _phase[i] = Lifetime * i / Amount * spread;
            _phaseLifetime = Lifetime;
            _phaseExplosiveness = Explosiveness;
            _random = new Random(Amount * 7919 + 1);
        }

        // Steps of at most 1/30 s keep preprocessing and long frames stable.
        while (seconds > 0)
        {
            var dt = Math.Min(seconds, 1f / 30f);
            seconds -= dt;
            Step(dt);
        }
    }

    private void Step(float dt)
    {
        var material = ProcessMaterial;
        // The first step starts just before 0, so the particle whose phase is 0 emits at once (Godot's first particle).
        var previous = _time == 0 ? -1e-9 : _time;
        _time += dt;
        var active = (int)Math.Ceiling(Amount * Math.Clamp(AmountRatio, 0f, 1f));
        for (var i = 0; i < _particles.Length; i++)
        {
            ref var p = ref _particles[i];
            // A particle's restarts are at phase + k·lifetime; one fell inside (previous, now].
            var cycles = Math.Floor((_time - _phase[i]) / Lifetime);
            var restarted = cycles >= 0 && Math.Floor((previous - _phase[i]) / Lifetime) < cycles;
            if (restarted)
            {
                if (_emitting && i < active && material is not null && (!OneShot || cycles == 0))
                    Emit(ref p, material, (float)(_time - _phase[i] - cycles * Lifetime));
                else
                    p.Alive = false;
                continue;
            }

            if (!p.Alive)
                continue;
            Advance(ref p, material, dt);
        }

        // A one-shot is done once its last particle (phase + lifetime) is over.
        if (OneShot && _emitting && Amount > 0 && previous < _phase[^1] + Lifetime && _time >= _phase[^1] + Lifetime)
        {
            _emitting = false;
            Finished?.Invoke();
        }
    }

    private void Emit(ref Particle p, ParticleProcessMaterial m, float age)
    {
        var origin = Vector2.Zero;
        switch (m.EmissionShape)
        {
            case ParticleProcessMaterial.EmissionShapeEnum.Box:
                origin = new Vector2((Rand() * 2 - 1) * m.EmissionBoxExtents.X, (Rand() * 2 - 1) * m.EmissionBoxExtents.Y);
                break;
            case ParticleProcessMaterial.EmissionShapeEnum.Sphere:
            case ParticleProcessMaterial.EmissionShapeEnum.SphereSurface:
                var a = Rand() * MathF.Tau;
                var r = m.EmissionShape == ParticleProcessMaterial.EmissionShapeEnum.Sphere ? MathF.Sqrt(Rand()) * m.EmissionSphereRadius : m.EmissionSphereRadius;
                origin = new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
                break;
        }

        var baseAngle = MathF.Atan2(m.Direction.Y, m.Direction.X);
        var spread = float.DegreesToRadians(m.Spread) * (Rand() * 2 - 1);
        var speed = m.InitialVelocityMin + (m.InitialVelocityMax - m.InitialVelocityMin) * Rand();
        var velocity = new Vector2(MathF.Cos(baseAngle + spread), MathF.Sin(baseAngle + spread)) * speed;
        p = new Particle
        {
            Alive = true,
            Age = 0,
            Position = LocalCoords ? origin : GlobalTransform.TransformPoint(origin),
            Velocity = LocalCoords ? velocity : GlobalTransform.TransformDirection(velocity),
            Accel = m.LinearAccelMin + (m.LinearAccelMax - m.LinearAccelMin) * Rand(),
            Angle = float.DegreesToRadians(m.AngleMin + (m.AngleMax - m.AngleMin) * Rand()),
            Scale = m.ScaleMin + (m.ScaleMax - m.ScaleMin) * Rand(),
            Damping = m.DampingMin + (m.DampingMax - m.DampingMin) * Rand(),
            Spin = float.DegreesToRadians(m.AngularVelocityMin + (m.AngularVelocityMax - m.AngularVelocityMin) * Rand()),
            Life = m.LifetimeRandomness > 0f ? Lifetime * (1f - Math.Clamp(m.LifetimeRandomness, 0f, 1f) * Rand()) : 0f,   // 0: until its restart
        };
        if (age > 0)
            Advance(ref p, m, age);
    }

    private static void Advance(ref Particle p, ParticleProcessMaterial? m, float dt)
    {
        p.Age += dt;
        if (p.Life > 0f && p.Age >= p.Life)
        {
            p.Alive = false;   // lifetime randomness: dies before its next restart
            return;
        }

        if (m is not null)
        {
            var accel = new Vector2(m.Gravity.X, m.Gravity.Y);
            if (p.Accel != 0 && p.Velocity.LengthSquared() > 1e-8f)
                accel += Vector2.Normalize(p.Velocity) * p.Accel;
            p.Velocity += accel * dt;
            if (p.Damping > 0f)
            {
                var speed = p.Velocity.Length();
                if (speed > 1e-6f)
                    p.Velocity *= Math.Max(0f, speed - p.Damping * dt) / speed;
            }
        }

        p.Angle += p.Spin * dt;
        p.Position += p.Velocity * dt;
    }

    private float Rand() => (float)_random.NextDouble();

    protected override void OnDraw()
    {
        if (Texture is not { } texture || ProcessMaterial is not { } m)
            return;
        var size = new Vector2(texture.Width, texture.Height);
        var toLocal = LocalCoords ? Transform2D.Identity : GlobalTransform.AffineInverse();
        var c = m.Color;
        var color = new Vector4(ColorSpace.SrgbToLinear(c.X), ColorSpace.SrgbToLinear(c.Y), ColorSpace.SrgbToLinear(c.Z), c.W);
        foreach (ref readonly var p in _particles.AsSpan())
        {
            if (!p.Alive)
                continue;
            // Godot's process-material transform (particle_process_material.cpp): TRANSFORM[0] is the X basis.
            Vector2 x, y;
            if (m.ParticleFlagDisableZ && m.ParticleFlagAlignY && p.Velocity.LengthSquared() > 0f)
            {
                y = Vector2.Normalize(p.Velocity);
                x = new Vector2(y.Y, -y.X);
            }
            else if (m.ParticleFlagDisableZ)
            {
                var (sin, cos) = MathF.SinCos(p.Angle);
                (x, y) = (new Vector2(cos, -sin), new Vector2(sin, cos));
            }
            else
            {
                (x, y) = (new Vector2(MathF.Cos(p.Angle), 0f), Vector2.UnitY);   // about the Y axis, seen from the front
            }

            var life = Math.Clamp(p.Age / (p.Life > 0f ? p.Life : Lifetime), 0f, 1f);
            var scale = p.Scale * (m.ScaleCurve?.Sample(life) ?? 1f);
            var tint = color;
            if (m.ColorRamp is { } ramp)
            {
                var r = ramp.Sample(life);
                tint *= new Vector4(ColorSpace.SrgbToLinear(r.X), ColorSpace.SrgbToLinear(r.Y), ColorSpace.SrgbToLinear(r.Z), r.W);
            }

            var transform = new Transform2D(x * scale, y * scale, p.Position);
            DrawSetTransformMatrix(toLocal * transform);
            DrawTexture(texture, -size / 2, tint);
        }

        DrawSetTransformMatrix(Transform2D.Identity);
    }
}
