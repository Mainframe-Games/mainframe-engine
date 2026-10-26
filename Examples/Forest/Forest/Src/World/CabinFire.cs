using System.Numerics;
using MainframeEngine;
using DrawingColor = System.Drawing.Color;

namespace Forest;

/// <summary>
/// The fire in the cabin's hearth: a warm <see cref="OmniLight3D"/> with cube shadows that flickers (layered value noise
/// on its energy and a few centimetres of drift, so the room's shadows breathe), additive HDR flames and rising embers
/// (after the Demo's campfire, scaled to the firebox) and a glowing bed of embers under them. The node stands on the
/// firebox's floor. <see cref="OnProcess"/> only does arithmetic: nothing allocates per frame.
/// </summary>
public sealed class CabinFire : Node3D
{
    /// <summary>The firelight's mean energy.</summary>
    [Export(Range = "0,10,0.05")] public float Energy { get; set; } = 1.2f;

    /// <summary>How far the firelight reaches, metres.</summary>
    [Export(Range = "1,20,0.1")] public float Range { get; set; } = 7f;

    /// <summary>How deep the flicker dips below the mean (0: steady).</summary>
    [Export(Range = "0,1,0.01")] public float Flicker { get; set; } = 0.28f;

    /// <summary>The firelight (its energy and position are animated).</summary>
    public OmniLight3D? Light { get; private set; }

    private static readonly Vector3 LightRest = new(0f, 0.38f, 0.12f);
    private Vector3 _rest = LightRest;
    private float _drift = 0.06f;
    private double _time;

    /// <summary>A fire for the hearth: light, flames, embers and the ember bed, built now.</summary>
    public static CabinFire Create()
    {
        var fire = new CabinFire { Name = "Fire" };
        fire.Light = new OmniLight3D
        {
            Name = "Firelight",
            Position = LightRest,
            Color = new Vector3(1f, 0.52f, 0.22f),
            Energy = fire.Energy,
            Range = fire.Range,
            CastsShadows = true,
        };
        fire.AddChild(fire.Light);
        var sprite = ForestParticles.SoftSprite();
        fire.AddChild(EmberBed());
        fire.AddChild(Flames(sprite));
        fire.AddChild(Embers());
        return fire;
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (Light is not { } light)
            return;
        _time += gameTime.DeltaTime;
        var t = (float)(_time % 10000.0);
        // Three octaves of value noise: a slow swell, the flames' flutter and quick crackle dips.
        var n = 0.5f * ValleyNoise.Value(t * 1.3f, 0.5f, 901) + 0.3f * ValleyNoise.Value(t * 5.1f, 3.5f, 902) + 0.2f * ValleyNoise.Value(t * 13.7f, 7.5f, 903);
        light.Energy = Energy * (1f - Flicker + Flicker * 2f * n * n);
        light.Range = Range;
        var drift = new Vector3(ValleyNoise.Value(t * 2.3f, 11.5f, 904) - 0.5f, ValleyNoise.Value(t * 3.1f, 13.5f, 905) - 0.5f, 0f);
        light.Position = _rest + drift * _drift;
    }

    /// <summary>
    /// A candle's flame (at its wick): a small warm light without shadows that trembles with the fire's noise, and one
    /// tiny additive flame.
    /// </summary>
    public static Node3D CreateCandle()
    {
        var candle = new CabinFire { Name = "Candle", Energy = 0.35f, Range = 3.2f, Flicker = 0.12f };
        candle.Light = new OmniLight3D { Name = "CandleLight", Position = new Vector3(0f, 0.03f, 0f), Color = new Vector3(1f, 0.62f, 0.3f), Energy = 0.35f, Range = 3.2f };
        candle._rest = candle.Light.Position;
        candle._drift = 0.006f;
        candle.AddChild(candle.Light);
        candle.AddChild(new GpuParticles3D
        {
            Name = "Flame",
            Amount = 6,
            Lifetime = 0.35f,
            Preprocess = 1f,
            DrawPass1 = new QuadMesh { Size = new Vector2(0.03f, 0.06f) },
            MaterialOverride = ForestParticles.Additive(ForestParticles.SoftSprite()),
            ProcessMaterial = new ParticleProcessMaterial
            {
                Direction = Vector3.UnitY,
                Spread = 3f,
                InitialVelocityMin = 0.03f,
                InitialVelocityMax = 0.06f,
                ScaleCurve = new Curve { Offsets = [0f, 1f], Values = [1f, 0.4f] },
                Color = new Vector4(2.2f, 1.5f, 0.8f, 1f),
            },
        });
        return candle;
    }

    /// <summary>A low, flattened dome of glowing coals (emissive, unshaded orange) on the firebox floor.</summary>
    private static MeshInstance3D EmberBed() => new()
    {
        Name = "EmberBed",
        Position = new Vector3(0f, 0.02f, 0f),
        Scale = new Vector3(1f, 0.25f, 0.8f),
        Mesh = new SphereMesh { Radius = 0.26f, Height = 0.52f, RadialSegments = 12, Rings = 6 },
        MaterialOverride = new StandardMaterial3D
        {
            ResourceName = "Embers",
            ShadingMode = ShadingMode.Pbr,
            AlbedoColor = DrawingColor.FromArgb(255, 30, 18, 12),
            Roughness = 0.9f,
            EmissionColor = DrawingColor.FromArgb(255, 255, 92, 24),
            EmissionEnergy = 2.5f,
        },
        CastShadows = false,
    };

    private static GpuParticles3D Flames(Texture2D sprite) => new()
    {
        Name = "Flames",
        Position = new Vector3(0f, 0.06f, 0f),
        Amount = 36,
        Lifetime = 0.75f,
        Preprocess = 2f,
        DrawPass1 = new QuadMesh { Size = new Vector2(0.55f, 0.75f) },
        MaterialOverride = ForestParticles.Additive(sprite),
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.2f, 0.03f, 0.1f),
            Direction = Vector3.UnitY,
            Spread = 10f,
            InitialVelocityMin = 0.35f,
            InitialVelocityMax = 0.65f,
            Gravity = new Vector3(0f, 0.8f, 0f), // hot air rises
            AngleMin = -25f,
            AngleMax = 25f,
            AngularVelocityMin = -50f,
            AngularVelocityMax = 50f,
            ScaleMin = 0.3f,
            ScaleMax = 0.45f,
            ScaleCurve = new Curve { Offsets = [0f, 0.25f, 1f], Values = [0.6f, 1f, 0.15f] },
            Color = new Vector4(1.6f, 1.3f, 1.1f, 1f), // above 1: HDR, for the glow
            ColorRamp = new Gradient
            {
                Offsets = [0f, 0.45f, 1f],
                Colors = [new Vector4(1f, 0.85f, 0.5f, 0.9f), new Vector4(1f, 0.45f, 0.1f, 0.7f), new Vector4(0.6f, 0.1f, 0.02f, 0f)],
            },
            HueVariationMin = -0.02f,
            HueVariationMax = 0.02f,
        },
    };

    private static GpuParticles3D Embers() => new()
    {
        Name = "Embers",
        Position = new Vector3(0f, 0.1f, 0f),
        Amount = 14,
        Lifetime = 1.2f,
        Preprocess = 2f,
        DrawPass1 = new QuadMesh { Size = new Vector2(0.015f, 0.05f) },
        MaterialOverride = ForestParticles.Additive(null),
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.12f,
            Direction = Vector3.UnitY,
            Spread = 20f,
            InitialVelocityMin = 0.6f,
            InitialVelocityMax = 1.1f,
            Gravity = new Vector3(0f, 0.2f, 0f),
            DampingMin = 0.3f,
            DampingMax = 0.6f,
            TurbulenceEnabled = true,
            TurbulenceNoiseScale = 0.6f,
            TurbulenceInfluenceMin = 0.03f,
            TurbulenceInfluenceMax = 0.08f,
            Color = new Vector4(3f, 1.4f, 0.5f, 1f),
            ColorRamp = new Gradient
            {
                Offsets = [0f, 0.7f, 1f],
                Colors = [Vector4.One, new Vector4(1f, 0.6f, 0.4f, 1f), new Vector4(1f, 0.3f, 0.2f, 0f)],
            },
            ParticleFlagAlignY = true, // streaks along their velocity
        },
    };
}
