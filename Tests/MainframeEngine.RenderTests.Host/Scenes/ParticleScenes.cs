using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// The <see cref="GpuParticles3D"/> presets of the particle scenes (ADR 0206): a campfire's additive flames and embers and
/// its lit, blended smoke, and a rain box of velocity-aligned streaks. Fixed seeds and the host's fixed 60 Hz step make
/// every run draw the same particles.
/// </summary>
public static class ParticleFx
{
    /// <summary>A soft round sprite: white, alpha falling off smoothly to the edge (64×64).</summary>
    public static Texture2D SoftDisc()
    {
        const int size = 64;
        var pixels = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var dx = (x + 0.5f) / size * 2f - 1f;
                var dy = (y + 0.5f) / size * 2f - 1f;
                var r = MathF.Min(1f, MathF.Sqrt(dx * dx + dy * dy));
                var a = 1f - r * r * (3f - 2f * r); // smoothstep falloff
                var i = (y * size + x) * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
                pixels[i + 3] = (byte)MathF.Round(a * a * 255f);
            }

        return Texture2D.FromPixels(size, size, pixels, TextureImportSettings.Default with { Mipmaps = true });
    }

    private static Gradient Ramp(float[] offsets, params Vector4[] colors) => new() { Offsets = offsets, Colors = colors };

    /// <summary>Additive, unshaded HDR flames rising from a small box (they glow with the glow post on).</summary>
    public static GpuParticles3D Flames(Texture2D sprite, int amount = 48, uint seed = 1) => new()
    {
        Name = "Flames",
        Amount = amount,
        Lifetime = 0.9f,
        Preprocess = 2f,
        UseFixedSeed = true,
        Seed = seed,
        DrawPass1 = new QuadMesh { Size = new Vector2(1f, 1.3f) },
        MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = ShadingMode.Unshaded,
            Transparency = AlphaMode.Blend,
            BlendMode = StandardMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true,
            BillboardMode = BillboardMode.Particles,
            AlbedoTexture = sprite,
        },
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = new Vector3(0.18f, 0.04f, 0.18f),
            Direction = Vector3.UnitY,
            Spread = 12f,
            InitialVelocityMin = 0.5f,
            InitialVelocityMax = 0.9f,
            Gravity = new Vector3(0f, 0.9f, 0f), // hot air rises
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

    /// <summary>Additive sparks streaking along their velocity, wandering on the turbulence.</summary>
    public static GpuParticles3D Embers(int amount = 24, uint seed = 2) => new()
    {
        Name = "Embers",
        Amount = amount,
        Lifetime = 1.6f,
        Preprocess = 2f,
        UseFixedSeed = true,
        Seed = seed,
        DrawPass1 = new QuadMesh { Size = new Vector2(0.025f, 0.09f) },
        MaterialOverride = new StandardMaterial3D
        {
            ShadingMode = ShadingMode.Unshaded,
            BlendMode = StandardMaterial3D.BlendModeEnum.Add,
            VertexColorUseAsAlbedo = true,
            BillboardMode = BillboardMode.Particles,
        },
        ProcessMaterial = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere,
            EmissionSphereRadius = 0.15f,
            Direction = Vector3.UnitY,
            Spread = 25f,
            InitialVelocityMin = 1f,
            InitialVelocityMax = 1.8f,
            Gravity = new Vector3(0f, 0.2f, 0f),
            DampingMin = 0.3f,
            DampingMax = 0.6f,
            TurbulenceEnabled = true,
            TurbulenceNoiseScale = 0.6f,
            TurbulenceInfluenceMin = 0.03f,
            TurbulenceInfluenceMax = 0.08f,
            Color = new Vector4(3f, 1.4f, 0.5f, 1f),
            ColorRamp = Ramp([0f, 0.7f, 1f], Vector4.One, new Vector4(1f, 0.6f, 0.4f, 1f), new Vector4(1f, 0.3f, 0.2f, 0f)),
            ParticleFlagAlignY = true,
        },
    };

    /// <summary>Blended smoke lit by the scene's lights: rising, drifting, turning, growing and fading, sorted back to front.</summary>
    public static GpuParticles3D Smoke(Texture2D sprite, int amount = 24, uint seed = 3) => new()
    {
        Name = "Smoke",
        Amount = amount,
        Lifetime = 3f,
        Preprocess = 4f,
        UseFixedSeed = true,
        Seed = seed,
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
            Gravity = new Vector3(0.18f, 0.08f, 0f), // a light breeze to +X
            DampingMin = 0.02f,
            DampingMax = 0.05f,
            AngleMin = 0f,
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

    /// <summary>
    /// Rain: <paramref name="amount"/> thin streaks falling from a box (half size <paramref name="extents"/>), aligned with
    /// their velocity and turned to face the camera, blended and sorted back to front.
    /// </summary>
    public static GpuParticles3D Rain(int amount, Vector3 extents, uint seed = 4) => new()
    {
        Name = "Rain",
        Amount = amount,
        Lifetime = 0.8f,
        Preprocess = 1f,
        UseFixedSeed = true,
        Seed = seed,
        DrawOrder = GpuParticles3D.DrawOrderEnum.ViewDepth,
        VisibilityAabb = new Aabb(-extents - new Vector3(1f, 9f, 1f), extents + Vector3.One),
        DrawPass1 = new QuadMesh { Size = new Vector2(0.015f, 0.4f) },
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
            EmissionBoxExtents = extents,
            Direction = new Vector3(0.1f, -1f, 0f),
            Spread = 2f,
            InitialVelocityMin = 8f,
            InitialVelocityMax = 10f,
            Gravity = ParticleProcessMaterial.DefaultGravity3D,
            Color = new Vector4(0.8f, 0.85f, 1f, 0.4f),
            ParticleFlagAlignY = true,
        },
    };
}

/// <summary>
/// A campfire at night (ADR 0206): additive HDR flames and embers that glow, and lit, blended smoke sorted back to front,
/// over two logs on dark ground lit by the fire's orange omni light, with the glow post on and a light distance fog.
/// <c>--count 1</c> turns the glow off (the bloom check); <c>--count 2</c> runs the depth prepass and TAA.
/// </summary>
public sealed class ParticleCampfireScene(HostOptions host) : RenderTestGame(host)
{
    protected override void LoadScene()
    {
        if (Host.Count == 2)
            Vulkan.AntiAliasing = AntiAliasing.Taa;
        var scene = new Node3D { Name = nameof(ParticleCampfireScene) };
        var camera = new Camera3D { Name = "Camera", Position = new Vector3(0f, 1.1f, 3f) };
        camera.LookAt(new Vector3(0f, 0.75f, 0f));
        scene.AddChild(camera);
        scene.AddChild(new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(20f, 20f) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 70, 58, 48), Specular = 0.05f },
        });
        var bark = new StandardMaterial3D { AlbedoColor = Color.FromArgb(255, 72, 46, 30), Specular = 0.05f };
        scene.AddChild(new MeshInstance3D
        {
            Name = "LogA",
            Position = new Vector3(0f, 0.08f, 0f),
            RotationDegrees = new Vector3(0f, 35f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.14f, 0.16f) },
            MaterialOverride = bark,
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "LogB",
            Position = new Vector3(0f, 0.16f, 0f),
            RotationDegrees = new Vector3(0f, -40f, 0f),
            Mesh = new BoxMesh { Size = new Vector3(0.9f, 0.14f, 0.16f) },
            MaterialOverride = bark,
        });
        scene.AddChild(new OmniLight3D { Name = "Firelight", Position = new Vector3(0f, 0.6f, 0.1f), Color = new Vector3(1f, 0.55f, 0.25f), Energy = 3f, Range = 6f });

        var sprite = ParticleFx.SoftDisc();
        var fire = new Node3D { Name = "Campfire", Position = new Vector3(0f, 0.2f, 0f) };
        scene.AddChild(fire);
        fire.AddChild(ParticleFx.Flames(sprite));
        fire.AddChild(ParticleFx.Embers());
        var smoke = ParticleFx.Smoke(sprite);
        smoke.Position = new Vector3(0f, 0.65f, 0f);
        fire.AddChild(smoke);

        scene.AddChild(new WorldEnvironment
        {
            Name = "Environment",
            AmbientColor = new Vector3(0.05f, 0.06f, 0.1f),
            FogEnabled = true,
            FogLightColor = new Vector3(0.04f, 0.05f, 0.08f),
            FogDensity = 0.06f,
            PostProcess = new PostProcessProfile
            {
                Tonemapper = Tonemapper.GodotAces,
                GlowEnabled = Host.Count != 1,
                GlowNormalized = true,
                GlowStrength = 0.9f,
            },
        });
        Renderer.SetClearColor(0.015f, 0.02f, 0.04f);
        Tree.ChangeScene(scene);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void DisposeScene()
    {
    }
}

/// <summary>
/// A rain box (ADR 0206): 4,000 velocity-aligned streaks (blended, sorted back to front) falling from a 12 × 12 m box
/// over wet ground under an overcast sky, with distance fog. <c>--count n</c> sets the amount.
/// </summary>
public sealed class ParticleRainScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0f, 1.6f, 4f);
        Camera.LookAt(new Vector3(0f, 1f, -2f));
        scene.AddChild(new MeshInstance3D
        {
            Name = "Ground",
            Mesh = new PlaneMesh { Size = new Vector2(60f, 60f) },
            MaterialOverride = Plain(78, 84, 92, specular: 0.6f),
        });
        scene.AddChild(new MeshInstance3D
        {
            Name = "Post",
            Position = new Vector3(-1.2f, 1f, -1f),
            Mesh = new BoxMesh { Size = new Vector3(0.3f, 2f, 0.3f) },
            MaterialOverride = Plain(120, 70, 50),
        });
        scene.AddChild(Sun(new Vector3(0.3f, -0.8f, -0.5f), energy: 0.35f));
        Environment.FogEnabled = true;
        Environment.FogLightColor = new Vector3(0.45f, 0.5f, 0.56f);
        Environment.FogDensity = 0.05f;
        var rain = ParticleFx.Rain(Host.Count > 0 ? Host.Count : 4000, new Vector3(6f, 0.5f, 6f));
        rain.Position = new Vector3(0f, 6.5f, -1f);
        scene.AddChild(rain);
    }
}

/// <summary>
/// The particle budget of issue #120 (ADR 0206): 20 campfire-sized emitters × 200 particles (alternately additive flames
/// and blended, sorted smoke) in a 5 × 4 grid, plus a 4,000-particle rain box over them. For <c>--perf</c> and
/// <c>--alloc</c>: everything is in view and emitting.
/// </summary>
public sealed class ParticleStressScene(HostOptions host) : ShadowSceneBase(host)
{
    protected override void Build(Node3D scene)
    {
        Camera.Position = new Vector3(0f, 6f, 12f);
        Camera.LookAt(new Vector3(0f, 0.5f, 0f));
        scene.AddChild(new MeshInstance3D { Name = "Ground", Mesh = new PlaneMesh { Size = new Vector2(60f, 60f) }, MaterialOverride = Plain(70, 70, 74) });
        scene.AddChild(Sun(new Vector3(0.3f, -0.8f, -0.5f), energy: 0.5f));
        var sprite = ParticleFx.SoftDisc();
        for (var i = 0; i < 20; i++)
        {
            var at = new Vector3((i % 5 - 2) * 3f, 0.3f, (i / 5 - 1.5f) * 3f);
            var emitter = i % 2 == 0 ? ParticleFx.Flames(sprite, amount: 200, seed: (uint)i) : ParticleFx.Smoke(sprite, amount: 200, seed: (uint)i);
            emitter.Name = $"Emitter{i}";
            emitter.Lifetime = 2f;
            emitter.Position = at;
            scene.AddChild(emitter);
        }

        var rain = ParticleFx.Rain(4000, new Vector3(8f, 0.5f, 8f));
        rain.Position = new Vector3(0f, 8f, 0f);
        scene.AddChild(rain);
    }
}
