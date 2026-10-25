using Color = System.Drawing.Color;
using System.Numerics;
using MainframeEngine;
using static Demo.DemoBuild;

namespace Demo;

/// <summary>
/// <see cref="GpuParticles3D"/> (ADR 0206) at night: a campfire's additive HDR flames and velocity-aligned embers that
/// glow, its lit smoke drifting in the breeze (blended, sorted back to front), and a rain box of streaks over the clearing,
/// with distance fog. <see cref="ParticlesPanel"/> toggles the rain and the glow and fires a one-shot burst of sparks.
/// </summary>
public static class ParticlesScene
{
    /// <summary>The soft round sprite of the flames and the smoke.</summary>
    public const string SpritePath = "Assets/Particles/soft.png";

    public static Node Build()
    {
        var root = new Node3D { Name = "particles" };
        var camera = Add(root, root, new Camera3D { Name = "Camera3D", Current = true, Position = new Vector3(0, 1.4f, 4.2f) });
        camera.LookAt(new Vector3(0, 0.9f, 0));
        camera.RotationDegrees = camera.RotationDegrees; // pin the Euler angles the saver writes
        Add(root, root, new WorldEnvironment
        {
            Name = "Environment",
            Sky = new Sky
            {
                Mode = SkyEnvironmentType.Procedural,
                SkyColor = new Vector3(0.01f, 0.015f, 0.04f),
                HorizonColor = new Vector3(0.05f, 0.06f, 0.1f),
                GroundColor = new Vector3(0.02f, 0.02f, 0.02f),
                SunDirection = Vector3.Normalize(new Vector3(0.3f, -1f, 0.5f)), // below the horizon: night
            },
            AmbientColor = new Vector3(0.06f, 0.07f, 0.12f),
            FogEnabled = true,
            FogLightColor = new Vector3(0.04f, 0.05f, 0.08f),
            FogDensity = 0.05f,
            PostProcess = new PostProcessProfile
            {
                Tonemapper = Tonemapper.GodotAces,
                GlowEnabled = true,
                GlowNormalized = true,
                GlowStrength = 0.9f,
            },
        });
        Add(root, root, new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(40, 40) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(70, 58, 48), Specular = 0.05f },
        });
        var bark = new StandardMaterial3D { AlbedoColor = Color.FromArgb(72, 46, 30), Specular = 0.05f };
        Add(root, root, new MeshInstance3D
        {
            Name = "LogA",
            Position = new Vector3(0, 0.08f, 0),
            RotationDegrees = new Vector3(0, 35, 0),
            Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.14f, 0.16f) },
            MaterialOverride = bark,
        });
        Add(root, root, new MeshInstance3D
        {
            Name = "LogB",
            Position = new Vector3(0, 0.16f, 0),
            RotationDegrees = new Vector3(0, -40, 0),
            Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.14f, 0.16f) },
            MaterialOverride = bark,
        });
        Add(root, root, new OmniLight3D { Name = "Firelight", Position = new Vector3(0, 0.6f, 0.1f), Color = new Vector3(1f, 0.55f, 0.25f), Energy = 3f, Range = 7f });

        var sprite = ResourceLoader.Load<Texture2D>(SpritePath);
        var fire = Add(root, root, new Node3D { Name = "Campfire", Position = new Vector3(0, 0.2f, 0) });
        Add(root, fire, Flames(sprite));
        Add(root, fire, Embers());
        Add(root, fire, Sparks());
        Add(root, fire, Smoke(sprite));
        Add(root, root, Rain());

        var ui = Add(root, root, new UiLayer { Name = "Ui", Layer = 10 });
        Add(root, ui, new ParticlesPanel { Name = "Panel" });
        return root;
    }

    private static Gradient Ramp(float[] offsets, params Vector4[] colors) => new() { Offsets = offsets, Colors = colors };

    private static StandardMaterial3D Additive(Texture2D? sprite) => new()
    {
        ShadingMode = ShadingMode.Unshaded,
        BlendMode = StandardMaterial3D.BlendModeEnum.Add,
        VertexColorUseAsAlbedo = true,
        BillboardMode = BillboardMode.Particles,
        AlbedoTexture = sprite,
    };

    private static GpuParticles3D Flames(Texture2D sprite) => new()
    {
        Name = "Flames",
        Amount = 48,
        Lifetime = 0.9f,
        Preprocess = 2f,
        DrawPass1 = new QuadMesh { Size = new Vector2(1f, 1.3f) },
        MaterialOverride = Additive(sprite),
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.18f, 0.04f, 0.18f),
            Direction = Vector3.UnitY,
            Spread = 12f,
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 0.9f,
            Gravity = new Vector3(0, 0.9f, 0), // hot air rises
            AngleMin = -30f,
            AngleMax = 30f,
            AngularVelocityMin = -60f,
            AngularVelocityMax = 60f,
            ScaleMin = 0.3f,
            ScaleMax = 0.45f,
            ScaleCurve = new Curve { Offsets = [0f, 0.25f, 1f], Values = [0.6f, 1f, 0.15f] },
            Color = new Vector4(1.6f, 1.35f, 1.2f, 1f), // above 1: HDR, for the glow
            ColorRamp = Ramp([0f, 0.45f, 1f], new Vector4(1f, 0.9f, 0.55f, 0.9f), new Vector4(1f, 0.5f, 0.12f, 0.7f), new Vector4(0.6f, 0.1f, 0.02f, 0f)),
            HueVariationMin = -0.02f,
            HueVariationMax = 0.02f,
        },
    };

    private static ParticleProcessMaterial Spark(float speed, float spread) => new()
    {
        EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
        EmissionSphereRadius = 0.15f,
        Direction = Vector3.UnitY,
        Spread = spread,
        InitialVelocityMin = speed,
        InitialVelocityMax = speed * 1.8f,
        Gravity = new Vector3(0, 0.2f, 0),
        DampingMin = 0.3f,
        DampingMax = 0.6f,
        TurbulenceEnabled = true,
        TurbulenceNoiseScale = 0.6f,
        TurbulenceInfluenceMin = 0.03f,
        TurbulenceInfluenceMax = 0.08f,
        Color = new Vector4(3f, 1.4f, 0.5f, 1f),
        ColorRamp = Ramp([0f, 0.7f, 1f], Vector4.One, new Vector4(1f, 0.6f, 0.4f, 1f), new Vector4(1f, 0.3f, 0.2f, 0f)),
        ParticleFlagAlignY = true, // streaks along their velocity
    };

    private static GpuParticles3D Embers() => new()
    {
        Name = "Embers",
        Amount = 24,
        Lifetime = 1.6f,
        Preprocess = 2f,
        DrawPass1 = new QuadMesh { Size = new Vector2(0.025f, 0.09f) },
        MaterialOverride = Additive(null),
        ProcessMaterial = Spark(1f, 25f),
    };

    /// <summary>A one-shot burst (<see cref="ParticlesPanel.Burst"/>): every spark at once, flung wide.</summary>
    private static GpuParticles3D Sparks() => new()
    {
        Name = "Sparks",
        Amount = 80,
        Lifetime = 1.4f,
        OneShot = true,
        Explosiveness = 1f,
        Emitting = false,
        DrawPass1 = new QuadMesh { Size = new Vector2(0.03f, 0.12f) },
        MaterialOverride = Additive(null),
        ProcessMaterial = Spark(2.5f, 70f),
    };

    private static GpuParticles3D Smoke(Texture2D sprite) => new()
    {
        Name = "Smoke",
        Position = new Vector3(0, 0.65f, 0),
        Amount = 24,
        Lifetime = 3f,
        Preprocess = 4f,
        DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth,
        MaterialOverride = new StandardMaterial3D
        {
            Transparency = AlphaMode.Blend,
            VertexColorUseAsAlbedo = true,
            BillboardMode = BillboardMode.Particles,
            AlbedoTexture = sprite,
        },
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.12f, 0.05f, 0.12f),
            Direction = Vector3.UnitY,
            Spread = 15f,
            InitialVelocityMin = 0.35f,
            InitialVelocityMax = 0.5f,
            Gravity = new Vector3(0.18f, 0.08f, 0), // a light breeze to +X
            DampingMin = 0.02f,
            DampingMax = 0.05f,
            AngleMax = 360f,
            AngularVelocityMin = -25f,
            AngularVelocityMax = 25f,
            ScaleMin = 0.45f,
            ScaleMax = 0.6f,
            ScaleCurve = new Curve { Offsets = [0f, 1f], Values = [0.6f, 2.2f] },
            Color = new Vector4(0.8f, 0.78f, 0.76f, 1f),
            ColorRamp = Ramp([0f, 0.15f, 1f], new Vector4(1f, 1f, 1f, 0f), new Vector4(1f, 1f, 1f, 0.7f), new Vector4(1f, 1f, 1.05f, 0f)),
        },
    };

    private static GpuParticles3D Rain() => new()
    {
        Name = "Rain",
        Position = new Vector3(0, 6.5f, -3),
        Amount = 3000,
        Lifetime = 0.8f,
        Preprocess = 1f,
        DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth,
        VisibilityAabb = new Aabb(new Vector3(-7, -10, -7), new Vector3(7, 2, 7)),
        DrawPass1 = new QuadMesh { Size = new Vector2(0.012f, 0.35f) },
        MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = ShadingMode.Unshaded,
            Transparency = AlphaMode.Blend,
            VertexColorUseAsAlbedo = true,
            BillboardMode = BillboardMode.Particles,
        },
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(6, 0.5f, 6),
            Direction = new Vector3(0.1f, -1, 0),
            Spread = 2f,
            InitialVelocityMin = 8f,
            InitialVelocityMax = 10f,
            Gravity = ParticleProcessMaterial.DefaultGravity3D,
            Color = new Vector4(0.6f, 0.65f, 0.8f, 0.35f),
            ParticleFlagAlignY = true,
        },
    };
}
