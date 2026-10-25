using System.Numerics;

namespace MainframeEngine.Tests.Scene;

/// <summary><see cref="GpuParticles3D"/>, its simulation and the 3D <see cref="ParticleProcessMaterial"/> members (ADR 0206).</summary>
public sealed class GpuParticles3DTests
{
    private const float Step = 1f / 60f;

    private static ParticleProcessMaterial Material(ParticleProcessMaterial.EmissionShapeEnum shape = ParticleProcessMaterial.EmissionShapeEnum.Point) => new()
    {
        EmissionShape = shape,
        Direction = Vector3.UnitY,
        Spread = 0f,
        InitialVelocityMin = 1f,
        InitialVelocityMax = 1f,
        Gravity = Vector3.Zero,
    };

    private static GpuParticles3D Emitter(SceneTree tree, int amount = 100, float lifetime = 1f)
    {
        var p = new GpuParticles3D
        {
            Amount = amount,
            Lifetime = lifetime,
            FixedFps = 0,
            UseFixedSeed = true,
            Seed = 7,
            ProcessMaterial = Material(ParticleProcessMaterial.EmissionShapeEnum.Box),
            SimulateWithoutRenderer = true,
        };
        tree.Root.AddChild(p);
        return p;
    }

    private static void Run(SceneTree tree, float seconds)
    {
        for (var t = 0f; t < seconds - 1e-4f; t += Step)
            tree.Tick(new GameTime { DeltaTime = Step });
    }

    private static ParticleEmitterSettings Settings(ParticleProcessMaterial material, int amount = 64, float lifetime = 1f, uint seed = 1) => new()
    {
        Amount = amount,
        Lifetime = lifetime,
        AmountRatio = 1f,
        Seed = seed,
        Emitter = Matrix4x4.Identity,
        Material = material,
    };

    // ── Emission shapes, spread, gravity, hue ──────────────────────────────────

    [Fact]
    public void EmissionShapesStayInsideTheirBounds()
    {
        var random = new ParticleRandom(3);
        var box = new ParticleProcessMaterial { EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box, EmissionBoxExtents = new Vector3(1f, 2f, 3f) };
        var sphere = new ParticleProcessMaterial { EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Sphere, EmissionSphereRadius = 2f };
        var surface = new ParticleProcessMaterial { EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.SphereSurface, EmissionSphereRadius = 2f };
        var ring = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
            EmissionRingAxis = Vector3.UnitY,
            EmissionRingRadius = 2f,
            EmissionRingInnerRadius = 1f,
            EmissionRingHeight = 0.5f,
        };
        Assert.Equal(Vector3.Zero, ParticleSimulation3D.SampleEmissionShape(new ParticleProcessMaterial(), ref random));

        var inside = 0;
        for (var i = 0; i < 2000; i++)
        {
            var b = ParticleSimulation3D.SampleEmissionShape(box, ref random);
            Assert.True(MathF.Abs(b.X) <= 1f && MathF.Abs(b.Y) <= 2f && MathF.Abs(b.Z) <= 3f, $"box sample {b}");

            var s = ParticleSimulation3D.SampleEmissionShape(sphere, ref random);
            Assert.True(s.Length() <= 2f + 1e-4f, $"sphere sample {s}");
            if (s.Length() < 1f)
                inside++;

            Assert.Equal(2f, ParticleSimulation3D.SampleEmissionShape(surface, ref random).Length(), 3);

            var r = ParticleSimulation3D.SampleEmissionShape(ring, ref random);
            var radial = MathF.Sqrt(r.X * r.X + r.Z * r.Z);
            Assert.InRange(radial, 1f - 1e-4f, 2f + 1e-4f);
            Assert.InRange(r.Y, -0.25f, 0.25f);
        }

        // Uniform in volume: an eighth of the samples lie within half the radius... here a quarter of the volume (r < 1 of 2).
        Assert.InRange(inside / 2000f, 0.08f, 0.17f);
    }

    [Fact]
    public void SpreadKeepsDirectionsInsideTheConeAndFlatnessFlattensThem()
    {
        var random = new ParticleRandom(5);
        Assert.Equal(Vector3.UnitX, ParticleSimulation3D.SpreadDirection(Vector3.UnitX, 0f, 0f, ref random));
        for (var i = 0; i < 1000; i++)
        {
            var d = ParticleSimulation3D.SpreadDirection(Vector3.UnitY, 30f, 0f, ref random);
            Assert.Equal(1f, d.Length(), 4);
            var angle = float.RadiansToDegrees(MathF.Acos(Math.Clamp(Vector3.Dot(d, Vector3.UnitY), -1f, 1f)));
            Assert.True(angle <= 43f, $"{angle}° from the direction"); // two 30° angles combined: ≤ ~42.4°

            var flat = ParticleSimulation3D.SpreadDirection(Vector3.UnitX, 60f, 1f, ref random);
            Assert.True(MathF.Abs(flat.Y) < 1e-5f, $"flatness 1 keeps directions in the XZ plane: {flat}");
        }
    }

    [Fact]
    public void TheTwoDimensionalDefaultGravityIsGodotsThreeDimensionalOne()
    {
        var settings = Settings(new ParticleProcessMaterial());
        Assert.Equal(new Vector3(0f, -9.8f, 0f), ParticleSimulation3D.GravityOf(new ParticleProcessMaterial(), settings));
        Assert.Equal(new Vector3(1f, 2f, 3f), ParticleSimulation3D.GravityOf(new ParticleProcessMaterial { Gravity = new Vector3(1f, 2f, 3f) }, settings));

        // Local particles fall in world space: gravity moves into the (here half-turned, doubled) emitter's space.
        var local = settings with
        {
            LocalCoords = true,
            Emitter = Matrix4x4.CreateScale(2f) * Matrix4x4.CreateRotationZ(MathF.PI),
        };
        var g = ParticleSimulation3D.GravityOf(new ParticleProcessMaterial(), local);
        Assert.Equal(0f, g.X, 4);
        Assert.Equal(4.9f, g.Y, 4);
    }

    [Fact]
    public void HueRotationKeepsLumaAndAFullTurnIsTheIdentity()
    {
        var c = new Vector4(0.8f, 0.3f, 0.1f, 0.5f);
        var same = ParticleSimulation3D.RotateHue(c, 0f);
        Assert.Equal(c.X, same.X, 3);
        Assert.Equal(c.Y, same.Y, 3);
        Assert.Equal(c.Z, same.Z, 3);
        var turned = ParticleSimulation3D.RotateHue(c, 1f / 3f);
        static float Luma(Vector4 v) => 0.299f * v.X + 0.587f * v.Y + 0.114f * v.Z;
        Assert.Equal(Luma(c), Luma(turned), 2);
        Assert.True(turned.Z > c.Z, $"a third of a turn moves red toward blue: {turned}");
        Assert.Equal(0.5f, turned.W);
        var full = ParticleSimulation3D.RotateHue(c, 1f);
        Assert.Equal(c.X, full.X, 3);
    }

    // ── Schedule ───────────────────────────────────────────────────────────────

    [Fact]
    public void EmitsEvenlyOverTheLifetimeAndStopsRestartingWhenEmissionStops()
    {
        var tree = new SceneTree();
        var p = Emitter(tree);
        Run(tree, 0.5f);
        Assert.InRange(p.LiveCount, 45, 55); // half the lifetime: half the slots started
        Run(tree, 1f);
        Assert.Equal(100, p.LiveCount);
        p.Emitting = false;
        Run(tree, 1.1f);
        Assert.Equal(0, p.LiveCount);
        tree.Shutdown();
    }

    [Fact]
    public void AnExplosiveOneShotEmitsEverythingThenFinishesAndRestartsReusingItsBuffers()
    {
        var tree = new SceneTree();
        var p = Emitter(tree);
        p.Explosiveness = 1f;
        p.OneShot = true;
        p.Restart();
        var finished = 0;
        p.Finished += () => finished++;
        Run(tree, 0.1f);
        Assert.Equal(100, p.LiveCount);
        Run(tree, 1f);
        Assert.Equal(0, p.LiveCount);
        Assert.False(p.Emitting);
        Assert.Equal(1, finished);

        var particles = p.Simulation.Particles.ToArray().Length;
        var allocated = AllocationGate.SmallestWindow(() => p.Restart());
        Assert.Equal(0, allocated);
        Run(tree, 0.1f);
        Assert.Equal(particles, p.LiveCount);
        tree.Shutdown();
    }

    [Fact]
    public void RandomnessJittersEachSlotWithinItsOwnSlice()
    {
        var sim = new ParticleSimulation3D();
        var settings = Settings(Material(), amount: 10, lifetime: 2f) with { Randomness = 1f };
        sim.Restart(settings);
        var distinct = new HashSet<double>();
        for (var i = 0; i < 10; i++)
            for (var cycle = 0; cycle < 3; cycle++)
            {
                var phase = sim.Phase(i, cycle) - cycle * 2.0;
                Assert.InRange(phase, i * 0.2, (i + 1) * 0.2);
                distinct.Add(Math.Round(phase - i * 0.2, 6));
            }

        Assert.True(distinct.Count > 20, "every cycle draws its own jitter");
    }

    [Fact]
    public void FixedSeedsRepeatAndOtherSeedsDiffer()
    {
        static Vector3[] Positions(uint seed)
        {
            var sim = new ParticleSimulation3D();
            var m = Material(ParticleProcessMaterial.EmissionShapeEnum.Sphere);
            m.Spread = 45f;
            var settings = Settings(m, seed: seed);
            sim.Restart(settings);
            for (var i = 0; i < 90; i++)
                sim.Step(settings, Step);
            return sim.Particles.ToArray().Select(p => p.Position).ToArray();
        }

        Assert.Equal(Positions(11), Positions(11));
        Assert.NotEqual(Positions(11), Positions(12));
    }

    [Fact]
    public void LifetimeRandomnessDampingAndTurbulence()
    {
        var m = Material(ParticleProcessMaterial.EmissionShapeEnum.Box);
        m.LifetimeRandomness = 0.8f;
        var sim = new ParticleSimulation3D();
        var settings = Settings(m, amount: 100) with { Explosiveness = 1f, OneShot = true };
        sim.Restart(settings);
        for (var i = 0; i < 36; i++)
            sim.Step(settings, Step);
        Assert.InRange(sim.LiveCount, 20, 80); // lives spread over 0.2–1 s

        // Damping stops particles; turbulence turns them but keeps their speed.
        var damped = Material();
        damped.DampingMin = damped.DampingMax = 100f;
        var turbulent = Material();
        turbulent.TurbulenceEnabled = true;
        turbulent.TurbulenceNoiseScale = 0.5f;
        turbulent.TurbulenceInfluenceMin = turbulent.TurbulenceInfluenceMax = 0.5f;
        var a = new ParticleSimulation3D();
        var b = new ParticleSimulation3D();
        var sa = Settings(damped, amount: 8) with { Explosiveness = 1f };
        var sb = Settings(turbulent, amount: 8) with { Explosiveness = 1f };
        a.Restart(sa);
        b.Restart(sb);
        for (var i = 0; i < 20; i++)
        {
            a.Step(sa, Step);
            b.Step(sb, Step);
        }

        foreach (var p in a.Particles)
            Assert.Equal(Vector3.Zero, p.Velocity);
        var turned = 0;
        foreach (var p in b.Particles)
        {
            Assert.Equal(1f, p.Velocity.Length(), 3);
            if (Vector3.Dot(Vector3.Normalize(p.Velocity), Vector3.UnitY) < 0.99f)
                turned++;
        }

        Assert.True(turned > 0, "turbulence turns the velocity");
    }

    [Fact]
    public void FixedFpsStepsAtItsOwnRateAndInterpolatesBetweenSteps()
    {
        var tree = new SceneTree();
        var p = Emitter(tree, amount: 1, lifetime: 10f);
        p.FixedFps = 30;
        p.Restart();
        tree.Tick(new GameTime { DeltaTime = Step }); // half a step: nothing simulated yet
        Assert.Equal(0, p.LiveCount);
        Assert.Equal(0.5f, p.InterpolationFraction, 4);
        tree.Tick(new GameTime { DeltaTime = Step });
        Assert.Equal(1, p.LiveCount);
        Assert.Equal(0f, p.InterpolationFraction, 4);
        p.SpeedScale = 0f;
        var before = p.Simulation.Time;
        Run(tree, 0.5f);
        Assert.Equal(before, p.Simulation.Time); // speed scale 0 pauses
        tree.Shutdown();
    }

    [Fact]
    public void EmitParticleStartsOneWhereAndHowItIsTold()
    {
        var tree = new SceneTree();
        var p = Emitter(tree, amount: 4, lifetime: 0.5f);
        p.Emitting = false;
        var at = new Transform3D(Basis.Identity, new Vector3(5f, 1f, 2f));
        p.EmitParticle(at, new Vector3(0f, 0f, -3f), new Color(1f, 0f, 0f, 1f), default,
            GpuParticles3D.EmitFlags.Position | GpuParticles3D.EmitFlags.Velocity | GpuParticles3D.EmitFlags.Color);
        Assert.Equal(1, p.LiveCount);
        var particle = p.Simulation.Particles.ToArray().Single(q => q.Alive);
        Assert.Equal(new Vector3(5f, 1f, 2f), particle.Position);
        Assert.Equal(new Vector3(0f, 0f, -3f), particle.Velocity);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), particle.Color);
        Run(tree, 0.25f);
        Assert.Equal(1, p.LiveCount); // not ended by its slot's schedule while emission is off
        Run(tree, 0.3f);
        Assert.Equal(0, p.LiveCount); // one lifetime
        tree.Shutdown();
    }

    // ── Instances: order, curves, colour ───────────────────────────────────────

    [Fact]
    public void InstancesSortBackToFrontAndSampleTheCurvesAtAge()
    {
        var m = new ParticleProcessMaterial
        {
            ScaleCurve = new Curve { Offsets = [0f, 1f], Values = [1f, 3f] },
            ColorRamp = new Gradient { Offsets = [0f, 1f], Colors = [Vector4.One, new Vector4(1f, 1f, 1f, 0f)] },
            Color = new Vector4(1f, 0.5f, 0f, 1f),
        };
        Particle3D At(float z, float age) => new()
        {
            Alive = true,
            Position = new Vector3(0f, 0f, z),
            PreviousPosition = new Vector3(0f, 0f, z),
            Age = age,
            Scale = 2f,
            Color = m.Color,
        };
        Particle3D[] particles = [At(-1f, 0.5f), default, At(-5f, 0f), At(-3f, 1f)];
        var order = new int[4];
        var keys = new float[4];
        var output = new ParticleInstance[4];

        var count = ParticleInstances.Write(particles, m, 1f, 1f, GpuParticles3D.DrawOrderEnum.ViewDepth, Matrix4x4.Identity,
            Vector3.Zero, -Vector3.UnitZ, order, keys, output);

        Assert.Equal(3, count);
        Assert.Equal([-5f, -3f, -1f], output[..3].Select(i => i.PositionScale.Z)); // farthest first
        Assert.Equal(2f, output[0].PositionScale.W, 4);  // age 0: curve 1
        Assert.Equal(6f, output[1].PositionScale.W, 4);  // age 1: curve 3
        Assert.Equal(4f, output[2].PositionScale.W, 4);  // age 0.5: curve 2
        Assert.Equal(1f, output[2].Color.X, 4);
        Assert.Equal(ColorSpace.SrgbToLinear(0.5f), output[2].Color.Y, 4);
        Assert.Equal(0.5f, output[2].Color.W, 4); // the ramp's alpha at half its life

        count = ParticleInstances.Write(particles, m, 1f, 1f, GpuParticles3D.DrawOrderEnum.Lifetime, Matrix4x4.Identity,
            Vector3.Zero, -Vector3.UnitZ, order, keys, output);
        Assert.Equal([-3f, -1f, -5f], output[..count].Select(i => i.PositionScale.Z)); // the oldest first

        // Interpolated a quarter of the way from the previous step.
        particles[0].PreviousPosition = new Vector3(0f, 4f, -1f);
        ParticleInstances.Write(particles, m, 1f, 0.25f, GpuParticles3D.DrawOrderEnum.Index, Matrix4x4.Identity,
            Vector3.Zero, -Vector3.UnitZ, order, keys, output);
        Assert.Equal(3f, output[0].PositionScale.Y, 4);
    }

    [Fact]
    public void StepsAndInstanceWritesAllocateNothing()
    {
        var m = Material(ParticleProcessMaterial.EmissionShapeEnum.Sphere);
        m.Spread = 30f;
        m.ScaleCurve = new Curve();
        m.ColorRamp = new Gradient();
        m.ColorInitialRamp = new Gradient();
        m.HueVariationMax = 0.1f;
        m.TurbulenceEnabled = true;
        var sim = new ParticleSimulation3D();
        var settings = Settings(m, amount: 500) with { Randomness = 0.5f };
        sim.Restart(settings);
        var order = new int[500];
        var keys = new float[500];
        var output = new ParticleInstance[500];
        void Frame()
        {
            for (var i = 0; i < 30; i++)
            {
                sim.Step(settings, Step);
                ParticleInstances.Write(sim.Particles, m, 1f, 0.5f, GpuParticles3D.DrawOrderEnum.ViewDepth, Matrix4x4.Identity,
                    new Vector3(0f, 0f, 10f), -Vector3.UnitZ, order, keys, output);
            }
        }

        Frame(); // warm up
        Assert.Equal(0, AllocationGate.SmallestWindow(Frame));
    }

    // ── Headless, serialization ────────────────────────────────────────────────

    [Fact]
    public void HeadlessEmittersStayInertAndAllocateNothing()
    {
        var settings = new ProjectSettings { Name = "HeadlessParticles" };
        settings.Audio.Enabled = false;
        using var host = new HeadlessHost(settings, new GameHostOptions { FixedFps = 60 });
        var p = new GpuParticles3D { Amount = 1000, Preprocess = 2f, ProcessMaterial = Material(ParticleProcessMaterial.EmissionShapeEnum.Box) };
        host.Tree.Root.AddChild(p);
        for (var i = 0; i < 10; i++)
            host.Step(Step);

        var allocated = AllocationGate.SmallestWindow(() =>
        {
            for (var i = 0; i < 60; i++)
                host.Tree.Tick(new GameTime { DeltaTime = Step });
        });
        Assert.Equal(0, p.LiveCount);
        Assert.Empty(p.Simulation.Particles.ToArray());
        Assert.False(p.HasRenderResources);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void EmittersAndTheirMaterialsSurviveASceneRoundTrip()
    {
        var root = new Node3D { Name = "Root" };
        var emitter = new GpuParticles3D
        {
            Name = "Fire",
            Amount = 64,
            Lifetime = 1.5f,
            OneShot = true,
            Explosiveness = 0.3f,
            Randomness = 0.4f,
            SpeedScale = 2f,
            FixedFps = 60,
            Interpolate = false,
            LocalCoords = true,
            UseFixedSeed = true,
            Seed = 42,
            DrawOrder = GpuParticles3D.DrawOrderEnum.Lifetime,
            VisibilityAabb = new Aabb(new Vector3(-1f, 0f, -2f), new Vector3(1f, 5f, 2f)),
            DrawPass1 = new QuadMesh { Size = new Vector2(0.5f, 2f) },
            MaterialOverride = new StandardMaterial3D
            {
                BlendMode = StandardMaterial3D.BlendModeEnum.Add,
                VertexColorUseAsAlbedo = true,
                BillboardMode = BillboardMode.Particles,
                ShadingMode = ShadingMode.Unshaded,
            },
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Ring,
                EmissionRingAxis = Vector3.UnitY,
                EmissionRingRadius = 3f,
                EmissionRingInnerRadius = 1f,
                EmissionRingHeight = 0.25f,
                Flatness = 0.5f,
                Gravity = new Vector3(0f, -2f, 0f),
                HueVariationMin = -0.1f,
                HueVariationMax = 0.2f,
                ColorInitialRamp = new Gradient { Offsets = [0f, 1f], Colors = [Vector4.One, new Vector4(1f, 0f, 0f, 1f)] },
                TurbulenceEnabled = true,
                TurbulenceNoiseScale = 2f,
                TurbulenceNoiseSpeed = new Vector3(0f, 1f, 0f),
                TurbulenceInfluenceMin = 0.2f,
                TurbulenceInfluenceMax = 0.4f,
                ParticleFlagAlignY = true,
            },
        };
        root.AddChild(emitter);
        emitter.Owner = root;

        var copy = PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetNode<GpuParticles3D>("Fire");

        Assert.Equal(64, copy.Amount);
        Assert.True(copy.OneShot && copy.LocalCoords && copy.UseFixedSeed && !copy.Interpolate);
        Assert.Equal(42u, copy.Seed);
        Assert.Equal(60, copy.FixedFps);
        Assert.Equal(2f, copy.SpeedScale);
        Assert.Equal(0.4f, copy.Randomness);
        Assert.Equal(GpuParticles3D.DrawOrderEnum.Lifetime, copy.DrawOrder);
        Assert.Equal(emitter.VisibilityAabb, copy.VisibilityAabb);
        Assert.Equal(new Vector2(0.5f, 2f), Assert.IsType<QuadMesh>(copy.DrawPass1).Size);
        var material = Assert.IsType<StandardMaterial3D>(copy.MaterialOverride);
        Assert.Equal(StandardMaterial3D.BlendModeEnum.Add, material.BlendMode);
        Assert.True(material.VertexColorUseAsAlbedo);
        Assert.Equal(BillboardMode.Particles, material.BillboardMode);
        var process = copy.ProcessMaterial!;
        Assert.Equal(ParticleProcessMaterial.EmissionShapeEnum.Ring, process.EmissionShape);
        Assert.Equal(3f, process.EmissionRingRadius);
        Assert.Equal(1f, process.EmissionRingInnerRadius);
        Assert.Equal(0.25f, process.EmissionRingHeight);
        Assert.Equal(0.5f, process.Flatness);
        Assert.Equal(0.2f, process.HueVariationMax);
        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), process.ColorInitialRamp!.Colors[1]);
        Assert.True(process.TurbulenceEnabled && process.ParticleFlagAlignY);
        Assert.Equal(new Vector3(0f, 1f, 0f), process.TurbulenceNoiseSpeed);
        Assert.Equal(0.4f, process.TurbulenceInfluenceMax);
    }

    [Fact]
    public void AProcessMaterialMresLoadsAndOlderOnesKeepTheirDefaults()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mf-particles-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "smoke.mres");
            ResourceSaver.Save(new ParticleProcessMaterial { EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.SphereSurface, Flatness = 0.25f }, path);
            ResourceLoader.ClearCache();
            var loaded = ResourceLoader.Load<ParticleProcessMaterial>(path);
            Assert.Equal(ParticleProcessMaterial.EmissionShapeEnum.SphereSurface, loaded.EmissionShape);
            Assert.Equal(0.25f, loaded.Flatness);

            // A 2D-era material (no 3D members in the file) loads with the 3D defaults.
            var old = Path.Combine(dir, "dust.mres");
            File.WriteAllText(old, """
                {
                  "format": 2,
                  "uid": "res_0a1b2c3d4e5f",
                  "type": "ParticleProcessMaterial",
                  "props": {
                    "EmissionShape": "Box",
                    "Spread": 10,
                    "DampingMin": 2
                  }
                }
                """);
            ResourceLoader.ClearCache();
            var reloaded = ResourceLoader.Load<ParticleProcessMaterial>(old);
            Assert.Equal(ParticleProcessMaterial.EmissionShapeEnum.Box, reloaded.EmissionShape);
            Assert.Equal(2f, reloaded.DampingMin);
            Assert.Equal(1f, reloaded.EmissionRingRadius);
            Assert.False(reloaded.TurbulenceEnabled);
            Assert.Equal(ParticleProcessMaterial.DefaultGravity2D, reloaded.Gravity);
        }
        finally
        {
            ResourceLoader.ClearCache();
            Directory.Delete(dir, recursive: true);
        }
    }
}
