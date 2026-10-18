using MainframeEngine.Audio;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.Tests.Audio.Music;

/// <summary>
/// Synthetic stems for the adaptive-music runtime tests, written as float WAVs to a temporary folder: bodies are a
/// constant (DC) per layer with an impulse at frame 0, tails a decaying ramp, stingers an impulse at their pickup. At
/// 480 BPM, 4/4, 48 kHz a beat is 6 000 frames and a bar 24 000 (half a second), so tests render quickly.
/// </summary>
internal sealed class MusicTestStems : IDisposable
{
    public const int Rate = 48000;
    public const double Tempo = 480;
    public const long Beat = 6000;
    public const long Bar = 24000;

    private int _next;

    public MusicTestStems() => Directory.CreateDirectory(Folder);

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "mf-music-" + Guid.NewGuid().ToString("N"));

    /// <summary>A body of <paramref name="bars"/> bars: <paramref name="dc"/> everywhere and <paramref name="impulse"/> at frame 0.</summary>
    public MusicStemRef Body(int bars, float dc, float impulse = float.NaN, int channels = 1)
    {
        var frames = (int)(bars * Bar);
        var samples = new float[frames * channels];
        Array.Fill(samples, dc);
        if (!float.IsNaN(impulse))
        {
            for (var c = 0; c < channels; c++)
                samples[c] = impulse;
        }

        return Write(samples, channels);
    }

    /// <summary>A tail: <paramref name="start"/> falling linearly to 0 over <paramref name="frames"/>.</summary>
    public MusicStemRef Tail(int frames, float start)
    {
        var samples = new float[frames];
        for (var i = 0; i < frames; i++)
            samples[i] = start * (1f - (float)i / frames);
        var stem = Write(samples, 1);
        stem.Frames = frames;
        return stem;
    }

    /// <summary>Silence with one impulse at <paramref name="at"/>.</summary>
    public MusicStemRef Impulse(int frames, int at, float value)
    {
        var samples = new float[frames];
        samples[at] = value;
        return Write(samples, 1);
    }

    /// <summary>Any samples.</summary>
    public MusicStemRef Write(float[] interleaved, int channels, int rate = Rate)
    {
        var path = Path.Combine(Folder, $"stem{_next++}.wav");
        WavWriter.Write(path, interleaved, channels, rate, WavSampleFormat.IeeeFloat);
        return new MusicStemRef { Path = path };
    }

    /// <summary>The test tempo's manifest skeleton.</summary>
    public static MusicManifest Manifest(params MusicManifestSection[] sections) => new()
    {
        Uid = "mus_test",
        SampleRate = Rate,
        Tempo = Tempo,
        Start = sections.Length > 0 ? sections[0].Id : null,
        Layers = [new MusicManifestLayer { Id = "base", Name = "Base" }],
        Sections = [.. sections],
    };

    /// <summary>A section with its stems by layer: (layer id, body, tail).</summary>
    public static MusicManifestSection Section(string id, int bars, bool loop = true, string? then = null,
        MusicSectionKind kind = MusicSectionKind.Section, params (string Layer, MusicStemRef? Body, MusicStemRef? Tail)[] stems)
    {
        var section = new MusicManifestSection
        {
            Id = id,
            Name = id,
            Kind = kind,
            Bars = bars,
            LengthFrames = bars * Bar,
            Loop = loop,
            Then = then,
        };
        foreach (var (layer, body, tail) in stems)
            section.Stems[layer] = new MusicStemSet { Body = body, Tail = tail };
        return section;
    }

    /// <summary>The music with every section memory-loaded (exact, no streaming thread involved).</summary>
    public static AdaptiveMusic InMemory(MusicManifest manifest)
    {
        var music = AdaptiveMusic.FromManifest(manifest);
        foreach (var section in manifest.Sections)
            music.PreloadSections.Add(section.Id);
        Assert.True(music.Preload());
        Assert.Empty(music.Plan.Problems);
        return music;
    }

    public void Dispose()
    {
        AudioClipCache.Clear();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // a stream may still hold a file on the streaming thread; the temp folder is cleaned by the OS
        }
    }
}

/// <summary>A manual null-device server, one instance, and everything it played (index = music frame while nothing pauses).</summary>
internal sealed class MusicRig : IDisposable
{
    private float[] _out = new float[48000 * 2 * 8];

    public MusicRig(AdaptiveMusic music, int sampleRate = MusicTestStems.Rate)
    {
        Server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null, SampleRate = sampleRate });
        Instance = new AdaptiveMusicInstance(Server, music);
        Instance.SectionStarted += s => Signals.Add($"section {s}");
        Instance.Bar += b => Signals.Add($"bar {b}");
        Instance.Beat += (bar, beat) => Signals.Add($"beat {bar}.{beat}");
        Instance.Marker += m => Signals.Add($"marker {m}");
        Instance.Finished += () => Signals.Add("finished");
    }

    public AudioServer Server { get; }

    public AdaptiveMusicInstance Instance { get; }

    public List<string> Signals { get; } = [];

    /// <summary>Frames rendered so far.</summary>
    public long Rendered { get; private set; }

    /// <summary>Wait for streamed lanes before each render (tests with streamed bodies).</summary>
    public bool WaitForStreams { get; set; }

    /// <summary>The left channel at output frame <paramref name="frame"/>.</summary>
    public float this[long frame] => _out[2 * frame];

    public float Right(long frame) => _out[2 * frame + 1];

    /// <summary>One game frame: the server's update, then <paramref name="frames"/> frames of audio.</summary>
    public void Frame(int frames = 800)
    {
        Server.Process(AudioTestUtil.Frame);
        Render(frames);
    }

    /// <summary>Audio only (no game-thread update).</summary>
    public void Render(int frames)
    {
        if (WaitForStreams)
            Assert.True(Server.WaitForStreams(Math.Min(frames + 2048, 16384), TimeSpan.FromSeconds(10)));
        var need = (Rendered + frames) * 2;
        if (need > _out.Length)
            Array.Resize(ref _out, (int)Math.Max(need, _out.Length * 2L));
        Server.RenderNullDevice(frames, _out.AsSpan((int)(Rendered * 2), frames * 2));
        Rendered += frames;
    }

    /// <summary>Game frames until <see cref="Rendered"/> reaches <paramref name="frame"/>.</summary>
    public void RunTo(long frame, int step = 800)
    {
        while (Rendered < frame)
            Frame((int)Math.Min(step, frame - Rendered));
    }

    /// <summary>The first output frame at or after <paramref name="from"/> whose left sample is at least <paramref name="threshold"/>.</summary>
    public long FindAbove(float threshold, long from = 0)
    {
        for (var f = from; f < Rendered; f++)
        {
            if (this[f] >= threshold)
                return f;
        }

        return -1;
    }

    public void Dispose()
    {
        Instance.Dispose();
        Server.Dispose();
    }
}
