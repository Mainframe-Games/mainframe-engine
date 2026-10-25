using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// The CPU side of issue #120's particle budget (ADR 0206): 20 emitters × 200 particles plus a 4,000-particle rain box.
/// <see cref="SimulateStep"/> is one simulation step of all 8,000 (at the default 30 steps/s, every other 60 Hz frame);
/// <see cref="WriteInstancesSorted"/> is one view's instance build: every particle ordered back to front, interpolated,
/// curve- and ramp-sampled and written.
/// </summary>
[MemoryDiagnoser]
public class ParticleBenchmarks
{
    private readonly ParticleSimulation3D[] _simulations = new ParticleSimulation3D[21];
    private readonly ParticleEmitterSettings[] _settings = new ParticleEmitterSettings[21];
    private readonly ParticleInstance[] _instances = new ParticleInstance[4000];
    private readonly int[] _order = new int[4000];
    private readonly float[] _keys = new float[4000];

    [GlobalSetup]
    public void Setup()
    {
        for (var i = 0; i < _simulations.Length; i++)
        {
            var rain = i == 20;
            var material = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = rain ? new Vector3(8f, 0.5f, 8f) : new Vector3(0.2f, 0.05f, 0.2f),
                Direction = rain ? -Vector3.UnitY : Vector3.UnitY,
                Spread = rain ? 2f : 15f,
                InitialVelocityMin = rain ? 8f : 0.4f,
                InitialVelocityMax = rain ? 10f : 0.8f,
                Gravity = rain ? ParticleProcessMaterial.DefaultGravity3D : new Vector3(0.1f, 0.5f, 0f),
                DampingMin = 0.05f,
                DampingMax = 0.1f,
                AngularVelocityMin = -30f,
                AngularVelocityMax = 30f,
                ScaleCurve = new Curve { Offsets = [0f, 1f], Values = [0.5f, 2f] },
                ColorRamp = new Gradient { Offsets = [0f, 0.5f, 1f], Colors = [Vector4.One, new Vector4(1f, 0.6f, 0.2f, 0.8f), Vector4.Zero] },
            };
            _settings[i] = new ParticleEmitterSettings
            {
                Amount = rain ? 4000 : 200,
                Lifetime = rain ? 0.8f : 2f,
                AmountRatio = 1f,
                Seed = (uint)i,
                Emitter = Matrix4x4.CreateTranslation((i % 5 - 2) * 3f, 0.3f, (i / 5 - 1.5f) * 3f),
                Material = material,
            };
            _simulations[i] = new ParticleSimulation3D();
            _simulations[i].Restart(_settings[i]);
            for (var s = 0; s < 90; s++)
                _simulations[i].Step(_settings[i], 1f / 30f); // full: every slot has emitted
        }
    }

    [Benchmark]
    public int SimulateStep()
    {
        var live = 0;
        for (var i = 0; i < _simulations.Length; i++)
        {
            _simulations[i].Step(_settings[i], 1f / 30f);
            live += _simulations[i].LiveCount;
        }

        return live;
    }

    [Benchmark]
    public int WriteInstancesSorted()
    {
        var written = 0;
        var camera = new Vector3(0f, 6f, 12f);
        var forward = Vector3.Normalize(new Vector3(0f, 0.5f, 0f) - camera);
        for (var i = 0; i < _simulations.Length; i++)
        {
            written += ParticleInstances.Write(_simulations[i].Particles, _settings[i].Material, _settings[i].Lifetime, 0.5f,
                GpuParticles3D.DrawOrderEnum.ViewDepth, Matrix4x4.Identity, camera, forward, _order, _keys, _instances);
        }

        return written;
    }
}
