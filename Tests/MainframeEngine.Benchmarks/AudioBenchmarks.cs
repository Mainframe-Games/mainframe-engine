using System.Numerics;
using BenchmarkDotNet.Attributes;
using MainframeEngine.Audio;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Audio costs (M7): the game → audio command ring, attenuation curves and listener-space projection, the audio
/// server's per-frame update for 32 moving positional voices, and mixing one device block of 32 voices.
/// </summary>
[MemoryDiagnoser]
public class AudioBenchmarks : IDisposable
{
    private const int Emitters = 32;

    private readonly SpscRing<AudioCommand> _ring = new(4096);
    private readonly AudioCommand[] _batch = new AudioCommand[64];
    private readonly Vector3[] _positions = new Vector3[Emitters];
    private Transform3D _listener;
    private SceneTree _tree = null!;
    private AudioServer _server = null!;
    private readonly AudioPlayer3D[] _players = new AudioPlayer3D[Emitters];
    private readonly GameTime _time = new() { DeltaTime = 1f / 60f, FrameCount = 1 };
    private float _phase;
    private readonly float[] _curve = [1f, 0.7f, 0.4f, 0.2f, 0f];

    [GlobalSetup]
    public void Setup()
    {
        for (var i = 0; i < _batch.Length; i++)
            _batch[i] = new AudioCommand { Type = AudioCommandType.SetVoiceParams, Index = i, Generation = 1, Params = VoiceParams.Default };
        for (var i = 0; i < Emitters; i++)
            _positions[i] = new Vector3(MathF.Cos(i) * (2 + i), 0.5f * i, MathF.Sin(i) * (2 + i));
        _listener = new Transform3D(Basis.FromQuaternion(Quaternion.CreateFromAxisAngle(Vector3.UnitY, 0.3f)), new Vector3(1, 2, 3));

        // 32 looping positional voices in a tree, on the manual null device.
        _tree = new SceneTree();
        _server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null }, _tree);
        _tree.Servers.Register(_server);
        var tone = new float[48000];
        for (var i = 0; i < tone.Length; i++)
            tone[i] = 0.1f * MathF.Sin(2 * MathF.PI * 220 * i / 48000f);
        var stream = AudioStream.FromSamples(tone, 1, 48000, "bench tone");
        var scene = new Node3D();
        _tree.ChangeScene(scene);
        for (var i = 0; i < Emitters; i++)
        {
            _players[i] = new AudioPlayer3D
            {
                Stream = stream,
                Bus = "SFX",
                Loop = true,
                Autoplay = true,
                UnitSize = 1f,
                MaxDistance = 100f,
                LowPassAtMaxDistance = i % 2 == 0 ? 2000f : 0f,
                Position = _positions[i],
            };
            scene.AddChild(_players[i]);
        }

        for (var i = 0; i < 30; i++)
            ServerFrame32PositionalVoices();
    }

    /// <summary>One frame's batch: 64 commands published to the ring at once, then drained by the consumer.</summary>
    [Benchmark]
    public int CommandBatchEnqueueAndDrain64()
    {
        _ring.EnqueueBatch(_batch);
        var count = 0;
        while (_ring.TryDequeue(out _))
            count++;
        return count;
    }

    /// <summary>Every attenuation model at 64 distances.</summary>
    [Benchmark]
    public float AttenuationCurvesAllModels()
    {
        var sum = 0f;
        for (var model = AttenuationModel.Disabled; model <= AttenuationModel.Custom; model++)
        {
            for (var d = 0; d < 64; d++)
                sum += AudioMath.Attenuation(model, d * 0.75f, 2f, 40f, 1f, _curve);
        }

        return sum;
    }

    /// <summary>Listener-space projection, attenuation and pan gains for 32 emitters (the per-frame spatial math).</summary>
    [Benchmark]
    public float SpatialProjection32Emitters()
    {
        var sum = 0f;
        for (var i = 0; i < _positions.Length; i++)
        {
            AudioMath.ProjectToListener(_listener, _positions[i], 1f, out var distance, out var pan);
            var gain = AudioMath.Attenuation(AttenuationModel.Inverse, distance, 1f, 100f);
            AudioMath.PanGains(pan, out var left, out var right);
            sum += gain * (left + right);
        }

        return sum;
    }

    /// <summary>
    /// The game thread's audio work per frame with 32 moving positional voices: the tree tick (transform sync +
    /// <see cref="AudioServer.Process"/>: listener, attenuation, panning, low-pass, command batch) plus draining the
    /// batch on a one-frame null-device render.
    /// </summary>
    [Benchmark]
    public void ServerFrame32PositionalVoices()
    {
        _phase += 0.01f;
        for (var i = 0; i < _players.Length; i++)
            _players[i].Position = _positions[i] + new Vector3(MathF.Sin(_phase + i), 0, 0);
        _tree.Tick(_time);
        _server.RenderNullDevice(1);
    }

    /// <summary>The audio thread mixing one device block (480 frames = 10 ms at 48 kHz) of 32 voices through the bus graph.</summary>
    [Benchmark]
    public void MixBlock480Frames32Voices() => _server.RenderNullDevice(480);

    [GlobalCleanup]
    public void Dispose()
    {
        _tree.Shutdown();
        _server.Dispose();
    }
}
