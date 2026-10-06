using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// How <see cref="GpuParticles2D"/> particles start and move (the subset of Godot's <c>ParticleProcessMaterial</c> games use
/// in 2D): emission point or box, direction with a spread, an initial speed range, gravity, a linear acceleration range along
/// the velocity, a start angle and scale range, and one colour.
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

    /// <summary>Straight colour multiplied into every particle.</summary>
    [Export] public Vector4 Color { get; set; } = Vector4.One;
}

/// <summary>
/// A 2D particle emitter (Godot's <c>GPUParticles2D</c> with a <see cref="ParticleProcessMaterial"/>, simulated on the CPU):
/// <see cref="Amount"/> particles, each restarting every <see cref="Lifetime"/> seconds at an even phase (explosiveness 0),
/// only the first <see cref="AmountRatio"/> of them emitting; <see cref="Preprocess"/> seconds are simulated when emission
/// starts. With <see cref="LocalCoords"/> off particles stay where they were emitted in the world. Each particle is the
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
    }

    private Particle[] _particles = [];
    private float[] _phase = [];
    private Random _random = new(1);
    private bool _emitting = true;
    private double _time;

    [Export] public Texture2D? Texture { get; set; }

    [Export] public ParticleProcessMaterial? ProcessMaterial { get; set; }

    [Export] public int Amount { get; set; } = 8;

    [Export] public float Lifetime { get; set; } = 1f;

    /// <summary>Fraction (0..1) of <see cref="Amount"/> that emits.</summary>
    [Export(Range = "0,1,0.01")] public float AmountRatio { get; set; } = 1f;

    /// <summary>Seconds simulated at once when emission starts.</summary>
    [Export] public float Preprocess { get; set; }

    [Export] public bool LocalCoords { get; set; }

    /// <summary>Godot's culling rectangle (kept for reference; particles are not culled).</summary>
    [Export] public Rect2 VisibilityRect { get; set; } = new(new Vector2(-100, -100), new Vector2(200, 200));

    /// <summary>Particles alive now (tests, diagnostics).</summary>
    public int LiveCount => _particles.Count(p => p.Alive);

    [Export]
    public bool Emitting
    {
        get => _emitting;
        set
        {
            if (value == _emitting)
                return;
            _emitting = value;
            if (value && LiveCount == 0 && Preprocess > 0)
                Simulate(Preprocess);
        }
    }

    /// <summary>Restarts every particle (Godot's <c>restart</c>).</summary>
    public void Restart()
    {
        _particles = [];
        _time = 0;
        if (_emitting && Preprocess > 0)
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
        if (_particles.Length != Amount)
        {
            _particles = new Particle[Amount];
            _phase = new float[Amount];
            for (var i = 0; i < Amount; i++)
                _phase[i] = Lifetime * i / Amount;
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
        var previous = _time;
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
                if (_emitting && i < active && material is not null)
                    Emit(ref p, material, (float)(_time - _phase[i] - cycles * Lifetime));
                else
                    p.Alive = false;
                continue;
            }

            if (!p.Alive)
                continue;
            Advance(ref p, material, dt);
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
        };
        if (age > 0)
            Advance(ref p, m, age);
    }

    private static void Advance(ref Particle p, ParticleProcessMaterial? m, float dt)
    {
        p.Age += dt;
        if (m is not null)
        {
            var accel = new Vector2(m.Gravity.X, m.Gravity.Y);
            if (p.Accel != 0 && p.Velocity.LengthSquared() > 1e-8f)
                accel += Vector2.Normalize(p.Velocity) * p.Accel;
            p.Velocity += accel * dt;
        }

        p.Position += p.Velocity * dt;
    }

    private float Rand() => (float)_random.NextDouble();

    protected override void OnDraw()
    {
        if (Texture is not { } texture || ProcessMaterial is not { } m)
            return;
        var size = new Vector2(texture.Width, texture.Height);
        var toLocal = LocalCoords ? Transform2D.Identity : GlobalTransform.AffineInverse();
        foreach (ref readonly var p in _particles.AsSpan())
        {
            if (!p.Alive)
                continue;
            var (sin, cos) = MathF.SinCos(p.Angle);
            var transform = new Transform2D(new Vector2(cos, sin) * p.Scale, new Vector2(-sin, cos) * p.Scale, p.Position);
            DrawSetTransformMatrix(toLocal * transform);
            DrawTexture(texture, -size / 2, m.Color);
        }

        DrawSetTransformMatrix(Transform2D.Identity);
    }
}
