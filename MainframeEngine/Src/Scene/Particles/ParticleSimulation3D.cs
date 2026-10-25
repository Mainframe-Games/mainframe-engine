using System.Numerics;

namespace MainframeEngine;

/// <summary>One particle of a <see cref="GpuParticles3D"/> (ADR 0206).</summary>
internal struct Particle3D
{
    public bool Alive;

    /// <summary>Emitted by <see cref="GpuParticles3D.EmitParticle"/>: lives its own <see cref="Life"/> even while its slot is not emitting.</summary>
    public bool Manual;

    public float Age;

    /// <summary>Seconds it lives; 0: until its slot restarts (no lifetime randomness).</summary>
    public float Life;

    public Vector3 Position;

    /// <summary>The position before the last step (<see cref="GpuParticles3D.Interpolate"/>).</summary>
    public Vector3 PreviousPosition;

    public Vector3 Velocity;
    public float Angle;
    public float PreviousAngle;
    public float Accel;
    public float Scale;
    public float Damping;
    public float Spin;

    /// <summary>The share of each 1/60 s the velocity turns toward the turbulence flow.</summary>
    public float Turbulence;

    /// <summary>Straight sRGB start colour: the material's colour × its initial ramp, hue-turned (HDR allowed).</summary>
    public Vector4 Color;
}

/// <summary>What a <see cref="ParticleSimulation3D"/> step needs from its emitter (copied from the node's exports).</summary>
internal struct ParticleEmitterSettings
{
    public int Amount;
    public float Lifetime;
    public float AmountRatio;
    public bool OneShot;
    public float Explosiveness;
    public float Randomness;
    public bool LocalCoords;
    public uint Seed;

    /// <summary>The emitter's world matrix (row-vector, <see cref="Node3D.ModelMatrix"/>).</summary>
    public Matrix4x4 Emitter;

    public ParticleProcessMaterial? Material;
}

/// <summary>
/// The 3D particle simulation of <see cref="GpuParticles3D"/> (ADR 0206), on the CPU like <see cref="GpuParticles2D"/>'s:
/// <c>Amount</c> slots, slot <c>i</c> restarting every lifetime at phase <c>lifetime × (i + randomness × hash(i, cycle)) /
/// amount × (1 − explosiveness)</c> (Godot's schedule), emitting only while <see cref="Emitting"/>. A sibling of the 2D
/// simulation rather than a shared one (the design's fallback): the 2D emitter keeps its random stream and results
/// exactly. Arrays are reallocated only when the amount changes; steps allocate nothing.
/// </summary>
internal sealed class ParticleSimulation3D
{
    private Particle3D[] _particles = [];
    private double[] _next = [];
    private int[] _cycle = [];
    private int _amount = -1;
    private float _lifetime = -1f, _explosiveness = -1f, _randomness = -1f;
    private uint _scheduleSeed;
    private double _time;
    private double _oneShotEnd;
    private ParticleRandom _random = new(1);
    private int _live;

    /// <summary>Whether restarting slots emit (Godot's <c>emitting</c>).</summary>
    public bool Emitting { get; set; } = true;

    /// <summary>Particles alive now.</summary>
    public int LiveCount => _live;

    /// <summary>Seconds simulated since the emitter (re)started.</summary>
    public double Time => _time;

    /// <summary>Every slot (alive or not).</summary>
    public ReadOnlySpan<Particle3D> Particles => _particles;

    /// <summary>The flow's time (turbulence), advanced with the steps.</summary>
    private float NoiseTime => (float)_time;

    /// <summary>Kills every particle, starts the schedule from the start with the settings' seed and emits (Godot's <c>restart</c>).</summary>
    public void Restart(in ParticleEmitterSettings settings)
    {
        Array.Clear(_particles);
        _live = 0;
        _time = 0;
        _random = new ParticleRandom(settings.Seed);
        Emitting = true;
        Configure(settings, force: true);
    }

    /// <summary>
    /// Advances <paramref name="dt"/> seconds. Returns true when a one-shot emitter's last particle has just ended (it stops
    /// emitting; the node raises <c>Finished</c>).
    /// </summary>
    public bool Step(in ParticleEmitterSettings settings, float dt)
    {
        if (settings.Amount <= 0 || settings.Lifetime <= 0f || dt <= 0f)
            return false;
        Configure(settings, force: false);
        var m = settings.Material;
        var previous = _time;
        _time += dt;
        var active = (int)Math.Ceiling(settings.Amount * Math.Clamp(settings.AmountRatio, 0f, 1f));
        var gravity = m is null ? Vector3.Zero : GravityOf(m, settings);
        var particles = _particles.AsSpan();
        for (var i = 0; i < particles.Length; i++)
        {
            ref var p = ref particles[i];
            p.PreviousPosition = p.Position;
            p.PreviousAngle = p.Angle;
            if (_next[i] <= _time)
            {
                // One or more restarts fell inside (previous, now]: the slot starts its latest cycle.
                var k = _cycle[i];
                var at = _next[i];
                for (var n = Phase(i, k + 1); n <= _time; n = Phase(i, k + 1))
                {
                    k++;
                    at = n;
                }

                _cycle[i] = k + 1;
                _next[i] = Phase(i, k + 1);
                if (Emitting && i < active && m is not null && (!settings.OneShot || k == 0))
                {
                    Emit(ref p, m, settings, (float)(_time - at), gravity);
                    continue;
                }

                if (!p.Manual)
                {
                    Kill(ref p);
                    continue;
                }
            }

            if (p.Alive)
                Advance(ref p, m, dt, gravity);
        }

        // A one-shot is done once its last particle (its slot's first phase + lifetime) is over.
        if (settings.OneShot && Emitting && previous < _oneShotEnd && _time >= _oneShotEnd)
        {
            Emitting = false;
            return true;
        }

        return false;
    }

    /// <summary>
    /// Starts one particle now (Godot's <c>emit_particle</c>): a free slot, else the oldest particle's; random like a
    /// scheduled one, then <paramref name="flags"/> replace its position (the transform's origin; world space unless the
    /// emitter uses local coordinates), scale (the length of the transform's X axis), velocity and colour.
    /// </summary>
    public void EmitManual(in ParticleEmitterSettings settings, in Transform3D transform, Vector3 velocity, Vector4 color,
        GpuParticles3D.EmitFlags flags)
    {
        if (settings.Amount <= 0 || settings.Lifetime <= 0f || settings.Material is not { } m)
            return;
        Configure(settings, force: false);
        var particles = _particles.AsSpan();
        var slot = -1;
        var oldest = -1f;
        for (var i = 0; i < particles.Length; i++)
        {
            if (!particles[i].Alive)
            {
                slot = i;
                break;
            }

            if (particles[i].Age > oldest)
            {
                oldest = particles[i].Age;
                slot = i;
            }
        }

        ref var p = ref particles[slot];
        Emit(ref p, m, settings, 0f, default);
        p.Manual = true;
        if (p.Life <= 0f)
            p.Life = settings.Lifetime;
        if ((flags & GpuParticles3D.EmitFlags.Position) != 0)
            p.Position = p.PreviousPosition = transform.Origin;
        if ((flags & GpuParticles3D.EmitFlags.RotationScale) != 0)
            p.Scale = transform.Basis.X.Length();
        if ((flags & GpuParticles3D.EmitFlags.Velocity) != 0)
            p.Velocity = velocity;
        if ((flags & GpuParticles3D.EmitFlags.Color) != 0)
            p.Color = color;
    }

    /// <summary>Gravity for this step: the material's (its 2D default read as Godot's 3D one), in the particles' space.</summary>
    internal static Vector3 GravityOf(ParticleProcessMaterial m, in ParticleEmitterSettings settings)
    {
        var gravity = m.Gravity == ParticleProcessMaterial.DefaultGravity2D ? ParticleProcessMaterial.DefaultGravity3D : m.Gravity;
        // Local particles still fall in world space (Godot moves gravity into the emitter's space).
        if (settings.LocalCoords && gravity != Vector3.Zero && Matrix4x4.Invert(settings.Emitter, out var inverse))
            gravity = Vector3.TransformNormal(gravity, inverse);
        return gravity;
    }

    // (Re)builds the schedule when the amount, lifetime, explosiveness, randomness or seed changed (or forced).
    private void Configure(in ParticleEmitterSettings s, bool force)
    {
        if (s.Amount <= 0 || s.Lifetime <= 0f)
            return;
        if (!force && _amount == s.Amount && _lifetime == s.Lifetime && _explosiveness == s.Explosiveness &&
            _randomness == s.Randomness && _scheduleSeed == s.Seed)
            return;
        if (_particles.Length != s.Amount)
        {
            _particles = new Particle3D[s.Amount];
            _next = new double[s.Amount];
            _cycle = new int[s.Amount];
            _live = 0;
        }

        if (_amount < 0)
            _random = new ParticleRandom(s.Seed);
        _amount = s.Amount;
        _lifetime = s.Lifetime;
        _explosiveness = s.Explosiveness;
        _randomness = s.Randomness;
        _scheduleSeed = s.Seed;
        _oneShotEnd = 0;
        for (var i = 0; i < _amount; i++)
        {
            // A running emitter keeps its clock: each slot resumes at its first restart from now (a new one at cycle 0).
            var k = _time <= 0 ? 0 : Math.Max(0, (int)Math.Floor((_time - Phase(i, 0)) / _lifetime) + 1);
            _cycle[i] = k;
            _next[i] = Phase(i, k);
            _oneShotEnd = Math.Max(_oneShotEnd, Phase(i, 0) + _lifetime);
        }
    }

    /// <summary>When slot <paramref name="index"/> starts cycle <paramref name="cycle"/> (seconds on the emitter's clock).</summary>
    internal double Phase(int index, int cycle)
    {
        var slot = (double)index;
        if (_randomness > 0f)
            slot += Math.Clamp(_randomness, 0f, 1f) * ParticleRandom.Hash01(_scheduleSeed, (uint)index, (uint)cycle);
        return cycle * (double)_lifetime + _lifetime * slot / _amount * (1.0 - Math.Clamp(_explosiveness, 0f, 1f));
    }

    private void Emit(ref Particle3D p, ParticleProcessMaterial m, in ParticleEmitterSettings s, float age, Vector3 gravity)
    {
        var origin = SampleEmissionShape(m, ref _random);
        var direction = SpreadDirection(m.Direction, m.Spread, m.Flatness, ref _random);
        var speed = Lerp(m.InitialVelocityMin, m.InitialVelocityMax, _random.NextFloat());
        var velocity = direction * speed;
        var accel = Lerp(m.LinearAccelMin, m.LinearAccelMax, _random.NextFloat());
        var angle = float.DegreesToRadians(Lerp(m.AngleMin, m.AngleMax, _random.NextFloat()));
        var scale = Lerp(m.ScaleMin, m.ScaleMax, _random.NextFloat());
        var damping = Lerp(m.DampingMin, m.DampingMax, _random.NextFloat());
        var spin = float.DegreesToRadians(Lerp(m.AngularVelocityMin, m.AngularVelocityMax, _random.NextFloat()));
        var lifeRandom = _random.NextFloat();
        var life = m.LifetimeRandomness > 0f ? s.Lifetime * (1f - Math.Clamp(m.LifetimeRandomness, 0f, 1f) * lifeRandom) : 0f;
        var color = m.Color;
        var initial = _random.NextFloat();
        if (m.ColorInitialRamp is { } ramp)
            color *= ramp.Sample(initial);
        var hue = Lerp(m.HueVariationMin, m.HueVariationMax, _random.NextFloat());
        if (hue != 0f)
            color = RotateHue(color, hue);
        var turbulence = Lerp(m.TurbulenceInfluenceMin, m.TurbulenceInfluenceMax, _random.NextFloat());
        if (!s.LocalCoords)
        {
            origin = Vector3.Transform(origin, s.Emitter);
            velocity = Vector3.TransformNormal(velocity, s.Emitter);
        }

        if (!p.Alive)
            _live++;
        p = new Particle3D
        {
            Alive = true,
            Position = origin,
            PreviousPosition = origin,
            Velocity = velocity,
            Accel = accel,
            Angle = angle,
            PreviousAngle = angle,
            Scale = scale,
            Damping = damping,
            Spin = spin,
            Life = life,
            Turbulence = turbulence,
            Color = color,
        };
        if (age > 0f)
        {
            Advance(ref p, m, age, gravity);
            p.PreviousPosition = p.Position;
            p.PreviousAngle = p.Angle;
        }
    }

    private void Advance(ref Particle3D p, ParticleProcessMaterial? m, float dt, Vector3 gravity)
    {
        p.Age += dt;
        if (p.Life > 0f && p.Age >= p.Life)
        {
            Kill(ref p); // lifetime randomness or a manual particle: ends before its slot restarts
            return;
        }

        if (m is not null)
        {
            var accel = gravity;
            if (p.Accel != 0f && p.Velocity.LengthSquared() > 1e-8f)
                accel += Vector3.Normalize(p.Velocity) * p.Accel;
            p.Velocity += accel * dt;
            if (p.Damping > 0f)
            {
                var speed = p.Velocity.Length();
                if (speed > 1e-6f)
                    p.Velocity *= Math.Max(0f, speed - p.Damping * dt) / speed;
            }

            if (m.TurbulenceEnabled && p.Turbulence > 0f)
                p.Velocity = Turbulence(p.Velocity, p.Position, p.Turbulence, m, NoiseTime, dt);
        }

        p.Angle += p.Spin * dt;
        p.Position += p.Velocity * dt;
    }

    private void Kill(ref Particle3D p)
    {
        if (p.Alive)
            _live--;
        p.Alive = false;
        p.Manual = false;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>A start position inside the material's emission shape, in the emitter's space.</summary>
    internal static Vector3 SampleEmissionShape(ParticleProcessMaterial m, ref ParticleRandom random)
    {
        switch (m.EmissionShape)
        {
            case ParticleProcessMaterial.EmissionShapeEnum.Box:
                {
                    var e = m.EmissionBoxExtents;
                    var x = random.NextFloat() * 2f - 1f;
                    var y = random.NextFloat() * 2f - 1f;
                    var z = random.NextFloat() * 2f - 1f;
                    return new Vector3(x * e.X, y * e.Y, z * e.Z);
                }

            case ParticleProcessMaterial.EmissionShapeEnum.Sphere:
            case ParticleProcessMaterial.EmissionShapeEnum.SphereSurface:
                {
                    // Uniform on the unit sphere (z uniform in [-1, 1], longitude uniform), then uniform in volume (∛u).
                    var z = random.NextFloat() * 2f - 1f;
                    var a = random.NextFloat() * MathF.Tau;
                    var u = random.NextFloat();
                    var r = MathF.Sqrt(Math.Max(0f, 1f - z * z));
                    var (sin, cos) = MathF.SinCos(a);
                    var onSphere = new Vector3(r * cos, r * sin, z);
                    var radius = m.EmissionShape == ParticleProcessMaterial.EmissionShapeEnum.Sphere ? MathF.Cbrt(u) : 1f;
                    return onSphere * (radius * m.EmissionSphereRadius);
                }

            case ParticleProcessMaterial.EmissionShapeEnum.Ring:
                {
                    var a = random.NextFloat() * MathF.Tau;
                    var outer = m.EmissionRingRadius;
                    var inner = Math.Clamp(m.EmissionRingInnerRadius, 0f, Math.Max(outer, 0f));
                    var radius = MathF.Sqrt(random.NextFloat() * (outer * outer - inner * inner) + inner * inner); // uniform in area
                    var h = (random.NextFloat() - 0.5f) * m.EmissionRingHeight;
                    var axis = m.EmissionRingAxis.LengthSquared() > 1e-12f ? Vector3.Normalize(m.EmissionRingAxis) : Vector3.UnitZ;
                    var (u, w) = Perpendiculars(axis);
                    var (sin, cos) = MathF.SinCos(a);
                    return (u * cos + w * sin) * radius + axis * h;
                }

            default:
                return Vector3.Zero;
        }
    }

    /// <summary>
    /// A unit direction within <paramref name="spreadDegrees"/> of <paramref name="direction"/> (Godot's 3D spread: an
    /// angle in the direction's horizontal plane and one, scaled by 1 − <paramref name="flatness"/>, in its vertical one).
    /// </summary>
    internal static Vector3 SpreadDirection(Vector3 direction, float spreadDegrees, float flatness, ref ParticleRandom random)
    {
        var spread = float.DegreesToRadians(spreadDegrees);
        var a1 = (random.NextFloat() * 2f - 1f) * spread;
        var a2 = (random.NextFloat() * 2f - 1f) * spread * (1f - Math.Clamp(flatness, 0f, 1f));
        var (s1, c1) = MathF.SinCos(a1);
        var (s2, c2) = MathF.SinCos(a2);
        // Godot's particle_process_material: the two angles' directions combined, then turned onto the direction.
        var zFactor = c2 / MathF.Max(1e-4f, MathF.Sqrt(MathF.Abs(c2)));
        var local = new Vector3(s1 * zFactor, s2, c1 * zFactor);
        var forward = direction.LengthSquared() > 0f ? Vector3.Normalize(direction) : Vector3.UnitZ;
        var binormal = Vector3.Cross(Vector3.UnitY, forward);
        binormal = binormal.LengthSquared() < 1e-8f ? Vector3.UnitZ : Vector3.Normalize(binormal);
        var normal = Vector3.Cross(forward, binormal);
        var turned = binormal * local.X + normal * local.Y + forward * local.Z;
        return turned.LengthSquared() > 0f ? Vector3.Normalize(turned) : forward;
    }

    // Two unit vectors perpendicular to `axis` and to each other.
    private static (Vector3 U, Vector3 W) Perpendiculars(Vector3 axis)
    {
        var helper = MathF.Abs(axis.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
        var u = Vector3.Normalize(Vector3.Cross(axis, helper));
        return (u, Vector3.Cross(axis, u));
    }

    /// <summary>
    /// <paramref name="color"/>'s hue turned by <paramref name="turns"/> of a full turn (the YIQ rotation Godot's
    /// <c>hue_variation</c> uses), keeping its luma and alpha.
    /// </summary>
    internal static Vector4 RotateHue(Vector4 color, float turns)
    {
        var (s, c) = MathF.SinCos(turns * MathF.Tau);
        var r = color.X;
        var g = color.Y;
        var b = color.Z;
        return new Vector4(
            (0.299f + 0.701f * c + 0.168f * s) * r + (0.587f - 0.587f * c + 0.330f * s) * g + (0.114f - 0.114f * c - 0.497f * s) * b,
            (0.299f - 0.299f * c - 0.328f * s) * r + (0.587f + 0.413f * c + 0.035f * s) * g + (0.114f - 0.114f * c + 0.292f * s) * b,
            (0.299f - 0.299f * c + 1.250f * s) * r + (0.587f - 0.587f * c - 1.050f * s) * g + (0.114f + 0.886f * c - 0.203f * s) * b,
            color.W);
    }

    // Turns the velocity toward the flow's direction at the particle (keeping its speed), by `influence` per 1/60 s.
    private static Vector3 Turbulence(Vector3 velocity, Vector3 position, float influence, ParticleProcessMaterial m, float time, float dt)
    {
        var speed = velocity.Length();
        if (speed < 1e-6f)
            return velocity;
        var scale = Math.Max(m.TurbulenceNoiseScale, 1e-3f);
        var at = position / scale + m.TurbulenceNoiseSpeed * (time / scale);
        var flow = ParticleNoise.Direction(at, time * m.TurbulenceNoiseSpeedRandom, m.TurbulenceNoiseStrength);
        if (flow == Vector3.Zero)
            return velocity;
        var t = 1f - MathF.Pow(1f - Math.Clamp(influence, 0f, 1f), dt * 60f);
        var steered = Vector3.Lerp(velocity / speed, flow, t);
        return steered.LengthSquared() > 1e-12f ? Vector3.Normalize(steered) * speed : velocity;
    }
}

/// <summary>A small deterministic PCG32 stream (allocation-free, reseedable) and integer hashes for the particle schedule.</summary>
internal struct ParticleRandom
{
    private const ulong Multiplier = 6364136223846793005UL;
    private const ulong Increment = 1442695040888963407UL;
    private ulong _state;

    public ParticleRandom(uint seed)
    {
        _state = 0;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    public uint NextUInt()
    {
        var old = _state;
        _state = old * Multiplier + Increment;
        var xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        var rotation = (int)(old >> 59);
        return System.Numerics.BitOperations.RotateRight(xorShifted, rotation);
    }

    /// <summary>A float in [0, 1).</summary>
    public float NextFloat() => (NextUInt() >> 8) * (1f / 16777216f);

    /// <summary>A well-mixed 32-bit hash (lowbias32).</summary>
    public static uint Hash(uint x)
    {
        x ^= x >> 16;
        x *= 0x7feb352dU;
        x ^= x >> 15;
        x *= 0x846ca68bU;
        x ^= x >> 16;
        return x;
    }

    /// <summary>A value in [0, 1) from three integers.</summary>
    public static float Hash01(uint a, uint b, uint c) => (Hash(a ^ Hash(b ^ Hash(c + 0x9e3779b9U))) >> 8) * (1f / 16777216f);
}

/// <summary>Smooth 3D value noise for the turbulence flow (ADR 0206): three channels, trilinear with smoothstep.</summary>
internal static class ParticleNoise
{
    /// <summary>The flow direction at <paramref name="p"/> (unit length, or zero), changing with <paramref name="w"/>.</summary>
    public static Vector3 Direction(Vector3 p, float w, float strength)
    {
        var shift = new Vector3(w * 1.37f, w * 0.71f, w * -1.13f);
        var v = new Vector3(
            Value(p + shift, 0x68e31da4U),
            Value(p - shift + new Vector3(31.4f, 17.1f, 9.2f), 0xb5297a4dU),
            Value(p + shift.YZX() + new Vector3(-12.7f, 44.3f, 27.9f), 0x1b56c4e9U));
        v = Vector3.Clamp(v * Math.Max(strength, 0f), new Vector3(-1f), Vector3.One);
        return v.LengthSquared() > 1e-10f ? Vector3.Normalize(v) : Vector3.Zero;
    }

    /// <summary>Value noise in [-1, 1].</summary>
    public static float Value(Vector3 p, uint seed)
    {
        var fx = MathF.Floor(p.X);
        var fy = MathF.Floor(p.Y);
        var fz = MathF.Floor(p.Z);
        var x0 = (int)fx;
        var y0 = (int)fy;
        var z0 = (int)fz;
        var tx = Smooth(p.X - fx);
        var ty = Smooth(p.Y - fy);
        var tz = Smooth(p.Z - fz);
        static float L(float a, float b, float t) => a + (b - a) * t;
        var c000 = Lattice(x0, y0, z0, seed);
        var c100 = Lattice(x0 + 1, y0, z0, seed);
        var c010 = Lattice(x0, y0 + 1, z0, seed);
        var c110 = Lattice(x0 + 1, y0 + 1, z0, seed);
        var c001 = Lattice(x0, y0, z0 + 1, seed);
        var c101 = Lattice(x0 + 1, y0, z0 + 1, seed);
        var c011 = Lattice(x0, y0 + 1, z0 + 1, seed);
        var c111 = Lattice(x0 + 1, y0 + 1, z0 + 1, seed);
        return L(L(L(c000, c100, tx), L(c010, c110, tx), ty), L(L(c001, c101, tx), L(c011, c111, tx), ty), tz);
    }

    private static float Smooth(float t) => t * t * (3f - 2f * t);

    private static float Lattice(int x, int y, int z, uint seed) =>
        ParticleRandom.Hash01(seed ^ (uint)x * 0x8da6b343U, (uint)y * 0xd8163841U, (uint)z * 0xcb1ab31fU) * 2f - 1f;

    private static Vector3 YZX(this Vector3 v) => new(v.Y, v.Z, v.X);
}
