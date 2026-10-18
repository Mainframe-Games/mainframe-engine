using BenchmarkDotNet.Attributes;
using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// Adaptive music (docs/design/future/adaptive-music.md, Performance budget): the game thread's work for one playing
/// instance per frame (8 layers × 2 curves, a parameter moving every frame), and the audio thread mixing one 10 ms block
/// of 16 lanes (8 layers cross-fading into 8 more, half of them through the layer filter). Stems are memory-loaded so
/// the numbers do not depend on the streaming thread keeping up with a benchmark's pace.
/// </summary>
[MemoryDiagnoser]
public class AdaptiveMusicBenchmarks : IDisposable
{
    private const int Rate = 48000;
    private const int Layers = 8;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "mf-music-bench-" + Guid.NewGuid().ToString("N"));
    private readonly GameTime _time = new() { DeltaTime = 1f / 60f, FrameCount = 1 };
    private AudioServer _frameServer = null!;
    private AudioServer _mixServer = null!;
    private AdaptiveMusicInstance _frameInstance = null!;
    private AdaptiveMusicInstance _mixInstance = null!;
    private float _phase;

    [GlobalSetup]
    public void Setup()
    {
        Directory.CreateDirectory(_folder);
        var music = AdaptiveMusic.FromManifest(BuildManifest());
        music.PreloadSections.AddRange(["a", "b"]);
        music.Preload();

        _frameServer = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        _frameInstance = new AdaptiveMusicInstance(_frameServer, music);
        _frameInstance.Play();
        for (var i = 0; i < 60; i++)
            InstanceFrame8Layers();

        // A cross-fade that never ends: both sections' 8 lanes play in every block.
        _mixServer = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        _mixInstance = new AdaptiveMusicInstance(_mixServer, music);
        _mixInstance.Play();
        _mixInstance.SetParameterImmediate("intensity", 0.6f);
        _mixInstance.GoToSection("b", MusicQuantize.Immediate, fadeOutBeats: 1e6f);
        for (var i = 0; i < 30; i++)
        {
            _mixServer.Process(_time);
            _mixServer.RenderNullDevice(800);
        }
    }

    /// <summary>One game frame of a playing instance: parameter change, curves, director, commands, plus draining them.</summary>
    [Benchmark]
    public void InstanceFrame8Layers()
    {
        _phase += 0.05f;
        _frameInstance.SetParameter("intensity", 0.5f + 0.5f * MathF.Sin(_phase));
        _frameServer.Process(_time);
        _frameServer.RenderNullDevice(1);
    }

    /// <summary>The audio thread mixing one device block (480 frames = 10 ms at 48 kHz) of 16 music lanes.</summary>
    [Benchmark]
    public void MixBlock480Frames16Lanes() => _mixServer.RenderNullDevice(480);

    [GlobalCleanup]
    public void Dispose()
    {
        _frameInstance.Dispose();
        _mixInstance.Dispose();
        _frameServer.Dispose();
        _mixServer.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // temp files; the OS cleans up
        }
    }

    private MusicManifest BuildManifest()
    {
        var manifest = new MusicManifest { SampleRate = Rate, Tempo = 120, Start = "a" };
        manifest.Parameters.Add(new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Max = 1 });
        foreach (var id in new[] { "a", "b" })
            manifest.Sections.Add(new MusicManifestSection { Id = id, Bars = 1, Loop = true });
        for (var l = 0; l < Layers; l++)
        {
            var layer = "l" + l;
            manifest.Layers.Add(new MusicManifestLayer { Id = layer, Filter = l % 2 == 0 ? MusicLayerFilter.LowPass : MusicLayerFilter.None });
            manifest.Curves.Add(new MusicCurveInfo { Layer = layer, Parameter = "intensity", Points = [new(0, -80), new(0.1f * l, -6), new(1, 0)] });
            manifest.Curves.Add(l % 2 == 0
                ? new MusicCurveInfo { Layer = layer, Target = MusicCurveTarget.Cutoff, Parameter = "intensity", Points = [new(0, 400), new(1, 16000)] }
                : new MusicCurveInfo { Layer = layer, Parameter = "intensity", Shape = MusicCurveShape.Smooth, Points = [new(0, -12), new(1, 0)] });
            foreach (var section in manifest.Sections)
                section.Stems[layer] = new MusicStemSet { Body = Stem(220 * (l + 1)) };
        }

        return manifest;
    }

    private MusicStemRef Stem(float frequency)
    {
        var frames = Rate * 2; // one bar at 120 BPM
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            samples[2 * i] = samples[2 * i + 1] = 0.05f * MathF.Sin(2 * MathF.PI * frequency * i / Rate);
        var path = Path.Combine(_folder, $"{frequency}.wav");
        WavWriter.Write(path, samples, 2, Rate, WavSampleFormat.IeeeFloat);
        return new MusicStemRef { Path = path };
    }
}
