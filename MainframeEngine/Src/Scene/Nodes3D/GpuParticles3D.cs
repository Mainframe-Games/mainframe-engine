using System.Numerics;

namespace MainframeEngine;

/// <summary>
/// A 3D particle emitter (Godot's <c>GPUParticles3D</c> with a <see cref="ParticleProcessMaterial"/>; ADR 0206), simulated
/// on the CPU like <see cref="GpuParticles2D"/> and drawn in one instanced draw per <see cref="DrawPass1"/> surface:
/// <see cref="Amount"/> particles, each restarting every <see cref="Lifetime"/> seconds at an even phase squeezed by
/// <see cref="Explosiveness"/> and jittered by <see cref="Randomness"/>, stepped at <see cref="FixedFps"/> (interpolated
/// between steps) and sped up by <see cref="SpeedScale"/>. The draw pass is a mesh (a <see cref="QuadMesh"/> when null)
/// with a <see cref="StandardMaterial3D"/>: lit or unshaded, opaque, cutout, mixed or additive
/// (<see cref="StandardMaterial3D.BlendMode"/>), the particle colour as albedo with
/// <see cref="StandardMaterial3D.VertexColorUseAsAlbedo"/>, emission for HDR glow, and its
/// <see cref="StandardMaterial3D.BillboardMode"/> (enabled, fixed-Y, or particles: by angle or along the velocity).
/// Blended passes draw after the blended meshes, sorted back to front with <see cref="DrawOrderEnum.ViewDepth"/>; they
/// are fogged and feed the glow like other geometry. Particles cast no shadows and are not in the depth prepass (TAA
/// treats them as reactive). Culled by <see cref="VisibilityAabb"/>.
/// </summary>
/// <remarks>
/// Inert without a render server (a <see cref="HeadlessHost"/> dedicated server): nothing is simulated or allocated. A
/// [Tool] node: the editor's viewport previews it. Random streams differ from Godot's GPU ones.
/// </remarks>
[Tool]
[EditorIcon("sparkles", Family = EditorIconFamily.Space3D)]
public class GpuParticles3D : VisualInstance3D
{
    /// <summary>The order particles are drawn in (Godot's <c>DrawOrder</c>).</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
        Justification = "Godot's C# name (GpuParticles3D.DrawOrderEnum), kept for ported games.")]
    public enum DrawOrderEnum
    {
        /// <summary>By slot.</summary>
        Index,

        /// <summary>The oldest first, the youngest on top.</summary>
        Lifetime,

        /// <summary>The youngest first, the oldest on top.</summary>
        ReverseLifetime,

        /// <summary>Back to front from the camera (blended passes; opaque and additive ones need no order).</summary>
        ViewDepth,
    }

    /// <summary>Which parts of <see cref="EmitParticle"/>'s arguments replace the process material's (Godot's <c>EmitFlags</c>).</summary>
    [Flags]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
        Justification = "Godot's C# name (GpuParticles3D.EmitFlags), kept for ported games.")]
    public enum EmitFlags
    {
        None = 0,
        Position = 1,
        RotationScale = 2,
        Velocity = 4,
        Color = 8,
        Custom = 16,
    }

    private readonly ParticleSimulation3D _simulation = new();
    private ParticleDrawState? _draw;
    private double _accumulator;
    private float _interpolation = 1f;
    private bool _preprocessPending = true;
    private uint _seed;
    private bool _seeded;

    public GpuParticles3D()
    {
        CastShadows = false;
    }

    [Export] public ParticleProcessMaterial? ProcessMaterial { get; set; }

    /// <summary>The mesh drawn for every particle (Godot's <c>draw_pass_1</c>); null: a 1×1 <see cref="QuadMesh"/>.</summary>
    [ExportGroup("Draw passes")]
    [Export] public Mesh? DrawPass1 { get; set; }

    /// <summary>Replaces every draw-pass surface's material (Godot's <c>material_override</c>); a <see cref="StandardMaterial3D"/>.</summary>
    [Export] public Material? MaterialOverride { get; set; }

    [Export] public DrawOrderEnum DrawOrder { get; set; } = DrawOrderEnum.ViewDepth;

    [ExportGroup("Emission")]
    [Export(Range = "1,1000000,1")] public int Amount { get; set; } = 8;

    /// <summary>Fraction (0..1) of <see cref="Amount"/> that emits.</summary>
    [Export(Range = "0,1,0.01")] public float AmountRatio { get; set; } = 1f;

    [ExportGroup("Time")]
    [Export(Range = "0.01,600,0.01")] public float Lifetime { get; set; } = 1f;

    /// <summary>Emit each particle once, then stop (Godot's <c>one_shot</c>); <see cref="Restart"/> or setting <see cref="Emitting"/> fires again.</summary>
    [Export] public bool OneShot { get; set; }

    /// <summary>Seconds simulated at once when emission starts (Godot's <c>preprocess</c>).</summary>
    [Export(Range = "0,600,0.01")] public float Preprocess { get; set; }

    /// <summary>Multiplies the simulation's speed (Godot's <c>speed_scale</c>; 0 pauses it).</summary>
    [Export(Range = "0,64,0.01")] public float SpeedScale { get; set; } = 1f;

    /// <summary>0 spreads emission over the lifetime, 1 emits every particle at once (Godot's <c>explosiveness</c>).</summary>
    [Export(Range = "0,1,0.01")] public float Explosiveness { get; set; }

    /// <summary>Jitters each emission by up to one slot (Godot's <c>randomness</c>).</summary>
    [Export(Range = "0,1,0.01")] public float Randomness { get; set; }

    /// <summary>Simulation steps per second (Godot's <c>fixed_fps</c>, default 30); 0: one step per frame (at most 1/30 s each).</summary>
    [Export(Range = "0,1000,1")] public int FixedFps { get; set; } = 30;

    /// <summary>Draws particles between their last two <see cref="FixedFps"/> steps (Godot's <c>interpolate</c>).</summary>
    [Export] public bool Interpolate { get; set; } = true;

    /// <summary>The same random streams every run (Godot's <c>use_fixed_seed</c>); otherwise a new seed per <see cref="Restart"/>.</summary>
    [Export] public bool UseFixedSeed { get; set; }

    /// <summary>The seed with <see cref="UseFixedSeed"/> (Godot's <c>seed</c>).</summary>
    [Export] public uint Seed { get; set; }

    /// <summary>Particles move with the emitter (Godot's <c>local_coords</c>); off, they stay where they were emitted in the world.</summary>
    [ExportGroup("Drawing")]
    [Export] public bool LocalCoords { get; set; }

    /// <summary>
    /// The box (emitter space) that holds every particle, for culling (Godot's <c>visibility_aabb</c>, default ±4); empty:
    /// never culled.
    /// </summary>
    [Export] public Aabb VisibilityAabb { get; set; } = new(new Vector3(-4f), new Vector3(4f));

    /// <summary>Raised when a <see cref="OneShot"/> emitter's last particle has ended (Godot's <c>finished</c>).</summary>
    [Signal] public event Action? Finished;

    /// <summary>Whether particles are being emitted (Godot's <c>emitting</c>); setting it on a one-shot fires it again.</summary>
    [Export]
    public bool Emitting
    {
        get => _simulation.Emitting;
        set
        {
            if (value == _simulation.Emitting)
                return;
            if (value && OneShot)
            {
                Restart();
                return;
            }

            _simulation.Emitting = value;
            if (value && _simulation.LiveCount == 0)
                _preprocessPending = true;
        }
    }

    /// <summary>Particles alive now.</summary>
    public int LiveCount => _simulation.LiveCount;

    /// <summary>The simulation (render server, tests).</summary>
    internal ParticleSimulation3D Simulation => _simulation;

    /// <summary>How far between the last two steps particles are drawn (0..1; 1 without interpolation).</summary>
    internal float InterpolationFraction => _interpolation;

    /// <summary>Simulates without a render server (unit tests); a headless tree leaves particles inert.</summary>
    internal bool SimulateWithoutRenderer { get; set; }

    private bool IsActive => SimulateWithoutRenderer || Tree?.Servers.Render is not null;

    /// <summary>Kills every particle and starts emitting from the start (Godot's <c>restart</c>); no allocation once sized.</summary>
    public void Restart()
    {
        _seeded = false;
        _accumulator = 0;
        _interpolation = 1f;
        _simulation.Restart(Settings());
        _preprocessPending = true;
    }

    /// <summary>
    /// Starts one particle now (Godot's <c>emit_particle</c>; the material must be set): random like the others, then
    /// <paramref name="flags"/> take its position from <paramref name="transform"/>'s origin (world space, or the emitter's
    /// with <see cref="LocalCoords"/>), its scale from the transform's X axis, its <paramref name="velocity"/> and its
    /// <paramref name="color"/> (sRGB, replacing the material's colour). It lives one <see cref="Lifetime"/> (shortened by
    /// lifetime randomness) in a free slot, else the oldest particle's. <paramref name="custom"/> is ignored.
    /// </summary>
    public void EmitParticle(Transform3D transform, Vector3 velocity, Color color, Color custom, EmitFlags flags)
    {
        _ = custom;
        if (!IsActive)
            return;
        _simulation.EmitManual(Settings(), transform, velocity, new Vector4(color.R, color.G, color.B, color.A), flags);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        base.OnProcess(gameTime);
        if (!IsActive)
            return;
        Advance(gameTime.DeltaTime * Math.Max(SpeedScale, 0f));
        if (_draw is not null && RenderServer?.Particles is { } renderer)
            renderer.Prepare(_draw, DrawPass1, MaterialOverride, Amount); // uploads join this frame (before it records)
    }

    /// <summary>Advances the simulation by <paramref name="seconds"/> (already scaled by <see cref="SpeedScale"/>).</summary>
    internal void Advance(float seconds)
    {
        if (_preprocessPending)
        {
            _preprocessPending = false;
            if (Preprocess > 0f && _simulation.Emitting)
                RunSteps(Preprocess, preprocess: true);
        }

        if (!_simulation.Emitting && _simulation.LiveCount == 0)
            return; // idle: nothing to move (the clock waits too)
        RunSteps(seconds, preprocess: false);
    }

    private void RunSteps(float seconds, bool preprocess)
    {
        if (seconds <= 0f)
            return;
        var settings = Settings();
        if (FixedFps > 0)
        {
            var step = 1.0 / FixedFps;
            _accumulator += seconds;
            if (!preprocess && _accumulator > 0.25)
                _accumulator = 0.25; // a long frame catches up a quarter second at most
            while (_accumulator >= step)
            {
                _accumulator -= step;
                if (_simulation.Step(settings, (float)step))
                    Finished?.Invoke();
            }

            _interpolation = Interpolate && !preprocess ? (float)(_accumulator / step) : 1f;
            if (preprocess)
                _accumulator = 0;
            return;
        }

        // Steps of at most 1/30 s keep preprocessing and long frames stable.
        while (seconds > 0f)
        {
            var dt = Math.Min(seconds, 1f / 30f);
            seconds -= dt;
            if (_simulation.Step(settings, dt))
                Finished?.Invoke();
        }

        _interpolation = 1f;
    }

    private ParticleEmitterSettings Settings()
    {
        if (!_seeded)
        {
            _seed = UseFixedSeed ? Seed : (uint)Random.Shared.Next();
            _seeded = true;
        }

        return new ParticleEmitterSettings
        {
            Amount = Math.Max(Amount, 0),
            Lifetime = Lifetime,
            AmountRatio = AmountRatio,
            OneShot = OneShot,
            Explosiveness = Explosiveness,
            Randomness = Randomness,
            LocalCoords = LocalCoords,
            Seed = UseFixedSeed ? Seed : _seed,
            Emitter = ModelMatrix,
            Material = ProcessMaterial,
        };
    }

    // ── Rendering ──────────────────────────────────────────────────────────────

    internal override bool DrawsTransparent => _draw is { Blended: true };

    protected override void InitializeRenderResources(RenderServer server)
    {
        if (server.Particles is { } renderer)
        {
            _draw = new ParticleDrawState();
            renderer.Prepare(_draw, DrawPass1, MaterialOverride, Amount);
        }
    }

    protected override void ReleaseRenderResources()
    {
        if (_draw is not null)
            RenderServer?.Particles?.Release(_draw);
        _draw = null;
        base.ReleaseRenderResources();
    }

    public override void Draw(in ICamera camera, in LightEnvironment lightEnvironment)
    {
        if (_draw is null || _simulation.LiveCount == 0 || RenderServer?.Particles is not { } renderer)
            return;
        renderer.Draw(_draw, this, camera);
    }
}
