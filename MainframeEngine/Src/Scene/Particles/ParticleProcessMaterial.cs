using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// How particles start and move (Godot's <c>ParticleProcessMaterial</c>), shared by <see cref="GpuParticles2D"/> and
/// <see cref="GpuParticles3D"/>: an emission shape (point, sphere, sphere surface, box; ring in 3D), a direction with a
/// spread (and <see cref="Flatness"/> in 3D), an initial speed range, gravity, a linear acceleration range along the
/// velocity, damping, a start angle, spin and scale range, a scale curve, a colour with an initial ramp, hue variation and
/// a ramp over the particle's life, lifetime randomness (ADR 0187) and, in 3D, turbulence (ADR 0206). The 2D emitter
/// ignores the 3D-only members (marked so below).
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

        /// <summary>A ring (or a tube with <see cref="EmissionRingHeight"/>) around <see cref="EmissionRingAxis"/> (3D only; Godot's value).</summary>
        Ring = 6,
    }

    /// <summary>The 2D emitter's default <see cref="Gravity"/> (pixels/s², Y down); a 3D emitter reads it as Godot's 3D default.</summary>
    public static readonly Vector3 DefaultGravity2D = new(0f, 98f, 0f);

    /// <summary>Godot's 3D default gravity (m/s², Y up), what <see cref="GpuParticles3D"/> uses while <see cref="Gravity"/> is <see cref="DefaultGravity2D"/>.</summary>
    public static readonly Vector3 DefaultGravity3D = new(0f, -9.8f, 0f);

    [ExportGroup("Emission shape")]
    [Export] public EmissionShapeEnum EmissionShape { get; set; } = EmissionShapeEnum.Point;

    /// <summary>Half size of the emission box (z ignored in 2D).</summary>
    [Export] public Vector3 EmissionBoxExtents { get; set; } = Vector3.One;

    [Export] public float EmissionSphereRadius { get; set; } = 1f;

    /// <summary>The ring's axis (3D only; zero: +Z, Godot's fallback).</summary>
    [Export] public Vector3 EmissionRingAxis { get; set; } = Vector3.UnitZ;

    /// <summary>The ring's outer radius (3D only).</summary>
    [Export] public float EmissionRingRadius { get; set; } = 1f;

    /// <summary>The ring's inner radius: particles start between it and <see cref="EmissionRingRadius"/> (3D only).</summary>
    [Export] public float EmissionRingInnerRadius { get; set; }

    /// <summary>The ring's height along its axis (3D only).</summary>
    [Export] public float EmissionRingHeight { get; set; } = 1f;

    /// <summary>Unit direction of the initial velocity (z ignored).</summary>
    [ExportGroup("Direction")]
    [Export] public Vector3 Direction { get; set; } = Vector3.UnitX;

    /// <summary>Degrees either side of <see cref="Direction"/>.</summary>
    [Export(Range = "0,180,0.1")] public float Spread { get; set; } = 45f;

    /// <summary>
    /// Flattens the 3D spread toward the plane of <see cref="Direction"/> and the emitter's X axis (Godot's <c>flatness</c>):
    /// 0 is a cone, 1 a fan (3D only).
    /// </summary>
    [Export(Range = "0,1,0.01")] public float Flatness { get; set; }

    [Export] public float InitialVelocityMin { get; set; }
    [Export] public float InitialVelocityMax { get; set; }

    /// <summary>
    /// Pixels per second² in 2D (Godot's default pulls 98 down; 2D is Y-down), metres per second² in 3D. A 3D emitter reads
    /// the 2D default as Godot's 3D one, (0, −9.8, 0) (<see cref="DefaultGravity3D"/>).
    /// </summary>
    [ExportGroup("Accelerations")]
    [Export] public Vector3 Gravity { get; set; } = DefaultGravity2D;

    /// <summary>Acceleration along the velocity, chosen per particle between these.</summary>
    [Export] public float LinearAccelMin { get; set; }
    [Export] public float LinearAccelMax { get; set; }

    /// <summary>Speed lost per second (units/s²), chosen per particle between these (Godot's <c>damping</c>; ADR 0187).</summary>
    [Export] public float DampingMin { get; set; }
    [Export] public float DampingMax { get; set; }

    /// <summary>Start rotation in degrees, chosen per particle between these.</summary>
    [ExportGroup("Angle")]
    [Export] public float AngleMin { get; set; }
    [Export] public float AngleMax { get; set; }

    /// <summary>Spin in degrees per second, chosen per particle between these (Godot's <c>angular_velocity</c>).</summary>
    [Export] public float AngularVelocityMin { get; set; }
    [Export] public float AngularVelocityMax { get; set; }

    [ExportGroup("Scale")]
    [Export] public float ScaleMin { get; set; } = 1f;
    [Export] public float ScaleMax { get; set; } = 1f;

    /// <summary>
    /// Multiplies the scale over the particle's life (Godot's <c>scale_curve</c>, a <see cref="Curve"/> here rather than a
    /// CurveTexture).
    /// </summary>
    [Export] public Curve? ScaleCurve { get; set; }

    /// <summary>
    /// Colour multiplied into every particle. Godot's <c>color_value</c> is a <c>source_color</c> uniform, converted to
    /// linear before it reaches the canvas, which then shows those values as they are: (1, 0.9, 0.5) draws as about
    /// (1, 0.79, 0.21). The particles do the same. Components above 1 make HDR (glowing) particles.
    /// </summary>
    [ExportGroup("Color")]
    [Export] public Vector4 Color { get; set; } = Vector4.One;

    /// <summary>
    /// Multiplies <see cref="Color"/> per particle by the gradient at a random offset chosen when it starts (Godot's
    /// <c>color_initial_ramp</c>, a <see cref="Gradient"/> here; 3D only).
    /// </summary>
    [Export] public Gradient? ColorInitialRamp { get; set; }

    /// <summary>
    /// Multiplies <see cref="Color"/> over the particle's life (Godot's <c>color_ramp</c>, a <see cref="Gradient"/> here
    /// rather than a GradientTexture1D): sampled at age / lifetime, converted like <see cref="Color"/>.
    /// </summary>
    [Export] public Gradient? ColorRamp { get; set; }

    /// <summary>
    /// Turns each particle's hue by a random fraction of a full turn between these (Godot's <c>hue_variation_min/max</c>,
    /// −1..1), applied to <see cref="Color"/> × the initial ramp when it starts (3D only).
    /// </summary>
    [Export(Range = "-1,1,0.01")] public float HueVariationMin { get; set; }
    [Export(Range = "-1,1,0.01")] public float HueVariationMax { get; set; }

    /// <summary>Each particle lives between (1 − this) × lifetime and the full lifetime (Godot's <c>lifetime_randomness</c>).</summary>
    [ExportGroup("Time")]
    [Export(Range = "0,1,0.01")] public float LifetimeRandomness { get; set; }

    /// <summary>
    /// Steers particles along a slowly moving 3D noise flow (Godot's <c>turbulence_enabled</c>; 3D only): each step turns
    /// the velocity toward the noise direction by the particle's influence, keeping its speed.
    /// </summary>
    [ExportGroup("Turbulence")]
    [Export] public bool TurbulenceEnabled { get; set; }

    /// <summary>Contrast of the flow pattern (Godot's <c>turbulence_noise_strength</c>).</summary>
    [Export(Range = "0,20,0.01")] public float TurbulenceNoiseStrength { get; set; } = 1f;

    /// <summary>Size of the flow's features in world units (Godot's <c>turbulence_noise_scale</c>).</summary>
    [Export(Range = "0,50,0.01")] public float TurbulenceNoiseScale { get; set; } = 9f;

    /// <summary>How fast the flow pattern drifts, in world units per second (Godot's <c>turbulence_noise_speed</c>).</summary>
    [Export] public Vector3 TurbulenceNoiseSpeed { get; set; }

    /// <summary>How fast the flow pattern changes in place (Godot's <c>turbulence_noise_speed_random</c>).</summary>
    [Export(Range = "0,4,0.01")] public float TurbulenceNoiseSpeedRandom { get; set; } = 0.2f;

    /// <summary>
    /// How much of each 1/60 s step the velocity turns toward the flow, chosen per particle between these (Godot's
    /// <c>turbulence_influence_min/max</c>; frame-rate independent here).
    /// </summary>
    [Export(Range = "0,1,0.001")] public float TurbulenceInfluenceMin { get; set; } = 0.1f;
    [Export(Range = "0,1,0.001")] public float TurbulenceInfluenceMax { get; set; } = 0.1f;

    /// <summary>
    /// Godot's <c>particle_flag_disable_z</c>: the angle turns the particle in the plane. Off (Godot's default) the angle
    /// turns it about the 3D Y axis, which in 2D only narrows it by cos(angle).
    /// </summary>
    [ExportGroup("Particle flags")]
    [Export] public bool ParticleFlagDisableZ { get; set; }

    /// <summary>
    /// Godot's <c>particle_flag_align_y</c>: the particle's Y follows its velocity (2D: with <see cref="ParticleFlagDisableZ"/>;
    /// 3D: a <see cref="BillboardMode.Particles"/> draw pass turns about the velocity to face the camera: sparks and rain streaks).
    /// </summary>
    [Export] public bool ParticleFlagAlignY { get; set; }
}
