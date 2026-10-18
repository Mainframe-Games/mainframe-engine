using System.Text.Json.Nodes;
using MainframeEngine.Audio.Music;
using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music.Adaptive;

/// <summary>
/// The adaptive bake (docs/design/future/adaptive-music.md, "The bake"): stems rendered in isolation and aligned to the
/// downbeat, tails, plugin delay compensation, incremental re-bakes, the manifest through A1's reader and atomic
/// commits. No audio device, ffmpeg or plugin helper: stems are WAV, or "encoded" by <see cref="FakePluginHost"/>.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class SongBakeTests : IDisposable
{
    private const long Bar = 3840; // 960 PPQ, 4/4
    private const long BarFrames = Bar * FramesPerTick; // 96 000 frames, 2 s at 120 BPM
    private const string Manifest = "Content/Music/Theme.mmusic";
    private const string Stems = "Content/Music/Theme.stems";

    private readonly string _root = Directory.CreateTempSubdirectory("mf-bake-").FullName;
    private readonly AssetDatabase _previous = AssetDatabase.Current;
    private readonly AssetDatabase _db;

    public SongBakeTests()
    {
        _db = new AssetDatabase(_root);
        AssetDatabase.Current = _db;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        AssetDatabase.Current = _previous;
        Directory.Delete(_root, recursive: true);
    }

    private static SongBakeOptions Options(IPluginHost? encoder = null, IProgress<SongBakeProgress>? progress = null, Func<int, PluginRack?>? plugins = null) => new()
    {
        InstrumentFactory = static (track, _) => new ToneInstrument(),
        Encoder = encoder,
        Progress = progress,
        Plugins = plugins,
        MaxParallelism = 2,
    };

    // Intro (1 bar, then Explore), Explore (2 bars, loops, a tail point at its second bar) and a 1-bar Victory stinger.
    // Pad (layer base) plays in all three; Drums (perc) only in Explore; Lead (the implicit Main layer) has one note that
    // starts in Intro and crosses into Explore.
    private static Song Theme()
    {
        var song = NewSong();
        song.Tracks.Clear();
        var pad = AddInstrumentTrack(song, "pad");
        pad.Layer = "base";
        AddClip(pad, 0, 4 * Bar, (60, 0, Bar), (62, Bar, 2 * Bar), (64, 3 * Bar + Bar / 2, Bar / 2));
        var drums = AddInstrumentTrack(song, "drums");
        drums.Layer = "perc";
        AddClip(drums, Bar, 2 * Bar, (36, 0, 480), (38, Bar + 960, 480));
        var lead = AddInstrumentTrack(song, "lead");
        AddClip(lead, 0, 4 * Bar, (72, Bar / 2, Bar));
        song.Adaptive = new SongAdaptive
        {
            Start = "intro",
            TailSeconds = 1,
            Sections =
            [
                new SongSection { Id = "intro", Name = "Intro", Kind = MusicSectionKind.Section, Start = 0, End = Bar, Then = "explore" },
                new SongSection { Id = "explore", Name = "Explore", Kind = MusicSectionKind.Section, Start = Bar, End = 3 * Bar, Loop = true },
                new SongSection { Id = "victory", Name = "Victory", Kind = MusicSectionKind.Stinger, Start = 3 * Bar, End = 4 * Bar },
            ],
            Markers = [new SongMarker { Id = "m1", Name = "drop", Tick = 2 * Bar, Exit = true, Tail = true }],
            Layers = [new SongLayer { Id = "base", Name = "Base" }, new SongLayer { Id = "perc", Name = "Perc" }],
            Parameters = [new MusicParameterInfo { Name = "intensity", Kind = MusicParameterKind.Continuous, Min = 0, Max = 1 }],
            Curves =
            [
                new MusicCurveInfo { Layer = "perc", Target = MusicCurveTarget.Volume, Parameter = "intensity", Points = [new(0, -80), new(1, 0)] },
            ],
        };
        return song;
    }

    private SongBakeResult Bake(Song song, SongBakeOptions? options = null) =>
        SongBaker.Bake(song, "Theme", _db, options ?? Options(), Ct);

    private MusicManifest Load() => MusicManifestFormat.Load(Path.Combine(_root, Manifest)).Manifest;

    private string Abs(string projectPath) => Path.Combine(_root, projectPath);

    // Frames in a 16-bit stereo WAV the bake wrote.
    private long WavFrames(string projectPath) => (new FileInfo(Abs(projectPath)).Length - 44) / 4;

    // Every file under the project with its bytes, for "nothing changed" checks.
    private Dictionary<string, byte[]> Snapshot() => Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories)
        .ToDictionary(f => Path.GetRelativePath(_root, f), File.ReadAllBytes);

    private static void AssertSameFiles(Dictionary<string, byte[]> before, Dictionary<string, byte[]> after)
    {
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before)
            Assert.True(bytes.AsSpan().SequenceEqual(after[path]), $"{path} changed");
    }

    [Fact]
    public void EveryBodyIsExactlyTheSectionLengthAndEmptyLayersAreNull()
    {
        var result = Bake(Theme());

        Assert.Equal(Manifest, result.ManifestPath);
        Assert.Equal(["intro/main", "intro/base", "explore/base", "explore/perc", "explore@m1/base", "explore@m1/perc", "victory/mix"],
            result.Rendered);
        Assert.Empty(result.Reused);
        var manifest = Load();
        Assert.Equal(["main", "base", "perc"], manifest.Layers.Select(l => l.Id));
        var intro = manifest.Sections[0];
        var explore = manifest.Sections[1];
        Assert.Equal(BarFrames, intro.LengthFrames);
        Assert.Equal(2 * BarFrames, explore.LengthFrames);

        // Bodies: exactly the section's frames, so the layers stay aligned through any number of loops.
        foreach (var section in new[] { intro, explore })
        {
            foreach (var set in section.Stems.Values.OfType<MusicStemSet>())
            {
                Assert.StartsWith(Stems + "/" + section.Id + ".", set.Body!.Path, StringComparison.Ordinal);
                Assert.Equal(section.LengthFrames, WavFrames(set.Body.Path));
                Assert.StartsWith("aud_", set.Body.Uid, StringComparison.Ordinal);
                Assert.Equal(set.Body.Path, _db.GetPath(set.Body.Uid));
                Assert.Contains("\"Stream\"", File.ReadAllText(Abs(set.Body.Path) + ".meta"), StringComparison.Ordinal);
                if (set.Tail is { } tail)
                {
                    Assert.Equal(tail.Frames, WavFrames(tail.Path));
                    Assert.Contains("\"Memory\"", File.ReadAllText(Abs(tail.Path) + ".meta"), StringComparison.Ordinal);
                }
            }
        }

        // Layers with nothing in a section bake nothing: null plays as silence.
        Assert.Null(intro.Stems["perc"]);
        Assert.Null(explore.Stems["main"]); // the lead note starts in Intro: it belongs there
        Assert.NotNull(explore.Stems["perc"]);

        // The stinger is one mixed stem with its tail appended.
        var victory = manifest.Sections[2];
        Assert.Equal([MusicManifestSection.MixStem], victory.Stems.Keys);
        var mix = victory.Stems[MusicManifestSection.MixStem]!.Body!;
        Assert.True(mix.Frames > victory.LengthFrames);
        Assert.Equal(mix.Frames, WavFrames(mix.Path));
        Assert.Null(victory.Stems[MusicManifestSection.MixStem]!.Tail);

        // The tail point: the pad's held note is cut at the marker and rings out; the drums are long silent there.
        var drop = Assert.Single(explore.Markers);
        Assert.Equal(BarFrames, drop.Frame);
        Assert.NotNull(drop.Tails!["base"]);
        Assert.Null(drop.Tails["perc"]);
        Assert.Null(drop.Tails["main"]);
        // Streamed: the four section bodies. In memory: three tails, the stinger and the tail point's tail.
        Assert.Equal(4, result.Streamed);
        Assert.Equal(5, result.MemoryLoaded);
    }

    [Fact]
    public void StemsAreRenderedInIsolationAndSumToTheSectionsMix()
    {
        var song = Theme();
        var options = Options();
        var pad = SongBaker.RenderStem(song, "explore", "base", options, Ct);
        var drums = SongBaker.RenderStem(song, "explore", "perc", options, Ct);
        var main = SongBaker.RenderStem(song, "explore", SongLayer.MainId, options, Ct);
        var mix = SongBaker.RenderStem(song, "explore", null, options, Ct);

        Assert.Equal(2 * BarFrames, pad.BodyFrames);
        // A clean start: the pad's Intro note ends on the boundary, and its release is not in Explore.
        Assert.Equal(0.1f, pad.Body[0], 6);
        Assert.Equal(0.05f, pad.Body[1], 6);
        // The lead note that started in Intro does not sound in Explore.
        Assert.All(main.Body, s => Assert.Equal(0f, s));
        Assert.Null(main.Tail);

        for (var i = 0; i < mix.Body.Length; i++)
            Assert.True(Math.Abs(pad.Body[i] + drums.Body[i] + main.Body[i] - mix.Body[i]) <= 1e-6f, $"sample {i}");
    }

    [Fact]
    public void NotesHeldAtTheEndAreCutAndTheirTailContinuesTheBody()
    {
        var song = Theme();
        var intro = SongBaker.RenderStem(song, "intro", SongLayer.MainId, Options(), Ct);

        // The lead starts half a bar in and is still held at the end: cut there, its release rings on in the tail.
        Assert.Equal(0f, intro.Body[2 * (BarFrames / 2 - 1)]);
        Assert.Equal(0.1f, intro.Body[2 * (BarFrames / 2)], 6);
        Assert.Equal(0.1f, intro.Body[^2], 6);
        var tail = Assert.IsType<float[]>(intro.Tail);
        Assert.Equal(0.1f, tail[0], 4); // no gap: the release starts on the first tail frame
        Assert.True(tail[2] < tail[0] && tail[2] > 0.099f);

        // Trimmed at −90 dBFS plus a 10 ms fade (well inside the 1 s rendered), ending in silence.
        var decay = (long)Math.Ceiling(Math.Log(SongBaker.SilenceThreshold / 0.1) / Math.Log(ToneInstrument.Decay));
        Assert.InRange(intro.TailFrames, decay, decay + Rate / 100 + 1);
        Assert.Equal(0f, tail[^2]);
    }

    [Fact]
    public void PluginLatencyIsDroppedSoFrameZeroIsTheDownbeat()
    {
        using var host = new FakePluginHost();
        var synth = new FakePlugin("S1", "Synth", Instrument: true);
        var delay = new FakePlugin("D1", "Delay", Latency: 100);
        host.Plugins[synth.ClassId] = synth;
        host.Plugins[delay.ClassId] = delay;
        var song = NewSong();
        song.Tracks.Clear();
        var slow = AddInstrumentTrack(song, "slow", new InstrumentDescriptor { Plugin = synth.Descriptor });
        slow.Inserts.Add(new PluginInsert { Plugin = delay.Descriptor });
        slow.Layer = "slow";
        AddClip(slow, 0, Bar, (60, 0, 480));
        var fast = AddInstrumentTrack(song, "fast", new InstrumentDescriptor { Plugin = synth.Descriptor });
        fast.Layer = "fast";
        AddClip(fast, 0, Bar, (60, 0, 480));
        song.Adaptive = new SongAdaptive
        {
            Start = "a",
            TailSeconds = 0.1,
            Sections = [new SongSection { Id = "a", Name = "A", Start = 0, End = Bar, Loop = true }],
            Layers = [new SongLayer { Id = "slow", Name = "Slow" }, new SongLayer { Id = "fast", Name = "Fast" }],
        };
        var options = new SongBakeOptions { Plugins = rate => new PluginRack(() => host, rate, id => "/fake/" + id + ".vst3", offline: true) };

        foreach (var layer in new[] { "slow", "fast" })
        {
            var stem = SongBaker.RenderStem(song, "a", layer, options, Ct);
            Assert.Equal(1f, stem.Body[0], 5); // the impulse of the note on the downbeat
            Assert.All(stem.Body.AsSpan(2, 400).ToArray(), s => Assert.Equal(0f, s));
        }

        // Without a plugin rack, a stem that needs plugins fails the bake (it never bakes silence instead).
        var error = Assert.Throws<SongBakeException>(() => Bake(song, new SongBakeOptions()));
        Assert.Contains("A/Slow", error.Details);
        Assert.False(File.Exists(Abs(Manifest)));

        // The bake renders plugin stems through the same offline rack.
        var result = Bake(song, new SongBakeOptions { Plugins = options.Plugins });
        Assert.Equal(["a/slow", "a/fast"], result.Rendered);
    }

    [Fact]
    public void ReBakesRenderOnlyWhatChangedAndKeepTheUid()
    {
        var song = Theme();
        var first = Bake(song);
        var uid = Load().Uid;
        Assert.StartsWith("mus_", uid, StringComparison.Ordinal);
        var files = Snapshot();

        // Nothing changed: every stem is reused and no stem file is written.
        var again = Bake(song);
        Assert.Empty(again.Rendered);
        Assert.Equal(first.Rendered, again.Reused);
        Assert.Empty(again.Deleted);
        foreach (var (path, bytes) in files.Where(f => f.Key.Contains(".stems", StringComparison.Ordinal)))
            Assert.Equal(bytes, File.ReadAllBytes(Abs(path)));

        // Curve, parameter, transition and marker-name edits re-render nothing; only the manifest changes.
        song.Adaptive!.Curves[0].Points[0] = new MusicCurvePoint(0, -40);
        song.Adaptive.Parameters[0].SeekSpeed = 2;
        var curve = Bake(song);
        Assert.Empty(curve.Rendered);
        Assert.Equal(-40, Load().Curves[0].Points[0].Y);

        // One drum note after the tail point: one body re-renders, and only the stem it replaces is deleted.
        var oldPerc = Load().Sections[1].Stems["perc"]!;
        Assert.Null(oldPerc.Tail); // the drums decay long before the section ends
        ((MidiClip)song.Tracks[1].Clips[0]).Notes[1].Velocity = 90;
        var edit = Bake(song);
        Assert.Equal(["explore/perc"], edit.Rendered);
        Assert.Equal([oldPerc.Body!.Path], edit.Deleted);
        Assert.False(File.Exists(Abs(oldPerc.Body.Path)));
        Assert.False(File.Exists(Abs(oldPerc.Body.Path) + ".meta"));
        Assert.Null(_db.GetPath(oldPerc.Body.Uid));
        var manifest = Load();
        Assert.Equal(uid, manifest.Uid);
        Assert.NotEqual(oldPerc.Body.Path, manifest.Sections[1].Stems["perc"]!.Body!.Path);
        foreach (var path in files.Keys.Where(p => p.Contains(".stems", StringComparison.Ordinal) && !p.StartsWith(oldPerc.Body.Path, StringComparison.Ordinal)))
            Assert.True(File.Exists(Abs(path)), path);

        // A tempo change renders everything again.
        song.Tempo = 100;
        Assert.Equal(first.Rendered, Bake(song).Rendered);
    }

    [Fact]
    public void TheManifestRoundTripsThroughTheEngineReader()
    {
        var song = Theme();
        Bake(song);
        var bytes = File.ReadAllBytes(Abs(Manifest));
        var read = MusicManifestFormat.Parse(bytes, "Theme.mmusic");
        Assert.Null(read.Notice);
        Assert.Equal(bytes, MusicManifestFormat.Write(read.Manifest));

        var manifest = read.Manifest;
        Assert.Equal(song.Uid, manifest.Song);
        Assert.Equal(Rate, manifest.SampleRate);
        Assert.Equal("intro", manifest.Start);
        Assert.Equal(Manifest, _db.GetPath(manifest.Uid));
        var bake = manifest.Bake!;
        Assert.Equal(7, bake["inputs"]!.AsObject().Count);
        Assert.Equal(64, bake["inputs"]!["explore/perc"]!.GetValue<string>().Length);

        var plan = MusicPlan.Compile(manifest);
        Assert.Empty(plan.Problems);
        Assert.Equal(manifest.Sections.Count, plan.Sections.Length);
    }

    [Fact]
    public void AFailedOrCancelledBakeLeavesThePreviousOneUntouched()
    {
        var encodes = 0;
        using var host = new FakePluginHost
        {
            EncodeHandler = (wav, ogg, _) =>
            {
                if (Interlocked.Increment(ref encodes) == 99)
                    throw new PluginHostException("encoder crashed");
                File.Copy(wav, ogg, overwrite: true);
                return new PluginHostEncodeResult((new FileInfo(wav).Length - 44) / 8, Rate, 2);
            },
        };
        var song = Theme();
        var first = Bake(song, Options(host));
        Assert.True(first.Encoded);
        Assert.EndsWith(".ogg", Load().Sections[0].Stems["base"]!.Body!.Path, StringComparison.Ordinal);
        var before = Snapshot();

        // Every stem changes; the encoder fails on the third: nothing the previous bake wrote changes, nothing is left over.
        song.Tempo = 130;
        encodes = 96;
        Assert.Throws<PluginHostException>(() => Bake(song, Options(host)));
        AssertSameFiles(before, Snapshot());

        // A stem that does not decode to its exact length fails the bake the same way.
        host.EncodeHandler = (wav, ogg, _) =>
        {
            File.Copy(wav, ogg, overwrite: true);
            return new PluginHostEncodeResult((new FileInfo(wav).Length - 44) / 8 + 1, Rate, 2);
        };
        var error = Assert.Throws<SongBakeException>(() => Bake(song, Options(host)));
        Assert.Contains("instead of", error.Message, StringComparison.Ordinal);
        AssertSameFiles(before, Snapshot());

        // Cancelled after the first stem.
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cancel.Cancel());
        Assert.ThrowsAny<OperationCanceledException>(() =>
            SongBaker.Bake(song, "Theme", _db, Options(progress: progress), cancel.Token));
        AssertSameFiles(before, Snapshot());
    }

    [Fact]
    public void ValidationErrorsRefuseTheBake()
    {
        var song = Theme();
        song.Adaptive!.Sections[1].End = 3 * Bar - 100; // not whole bars
        var error = Assert.Throws<SongBakeException>(() => Bake(song));
        Assert.NotEmpty(error.Details);
        Assert.False(File.Exists(Abs(Manifest)));

        song.Adaptive = null;
        Assert.Throws<SongBakeException>(() => Bake(song));
    }

    [Fact]
    public void AnAdaptiveOutputPathNamesTheManifestAndItsStemsFolder()
    {
        Assert.Equal("Content/Music/Theme.mmusic", SongBaker.ManifestPathFor((string?)null, "Theme"));
        Assert.Equal("Content/Audio/Boss.mmusic", SongBaker.ManifestPathFor("Content\\Audio\\Boss.json", "Theme"));
        Assert.Equal("Content/Audio/Boss.stems", SongBaker.StemsFolderFor("Content/Audio/Boss.mmusic"));

        var song = Theme();
        song.Adaptive!.Output = "Content/Audio/Boss.mmusic";
        Assert.Equal("Content/Audio/Boss.mmusic", Bake(song).ManifestPath);
        Assert.True(Directory.Exists(Abs("Content/Audio/Boss.stems")));
    }

    [Fact]
    public void TheBakeSongFlagBakesIntoTheSongsProject()
    {
        File.WriteAllText(Path.Combine(_root, ProjectSettings.FileName), "{}");
        var songPath = Abs("Content/Music/Theme.msong");
        Directory.CreateDirectory(Path.GetDirectoryName(songPath)!);
        var song = Theme();
        foreach (var track in song.Tracks)
            track.Instrument = InstrumentDescriptor.Zzfx("zzfx(...[,0,220,.01,.3,.3,2])");
        SongFormat.Save(song, songPath);
        using var output = new StringWriter();
        using var error = new StringWriter();

        Assert.Equal(0, SongBakeCommand.Run([SongBakeCommand.Flag, songPath], output, error));
        Assert.True(File.Exists(Abs(Manifest)), error.ToString());
        Assert.Contains(Manifest, output.ToString(), StringComparison.Ordinal);
        var extension = PluginHostClient.IsAvailable ? ".ogg" : ".wav"; // .ogg through the plugin helper where it exists
        Assert.EndsWith(extension, Load().Sections[0].Stems["base"]!.Body!.Path, StringComparison.Ordinal);

        Assert.Equal(1, SongBakeCommand.Run([SongBakeCommand.Flag, Abs("missing.msong")], output, error));
        Assert.Equal(1, SongBakeCommand.Run([SongBakeCommand.Flag], output, error));
        SongFormat.Save(NewSong(), songPath); // not adaptive
        Assert.Equal(1, SongBakeCommand.Run([SongBakeCommand.Flag, songPath], output, error));
    }

    [Fact]
    public async Task TheJobBakesOnAWorkerWithProgress()
    {
        var song = Theme();
        foreach (var track in song.Tracks)
            track.Instrument = InstrumentDescriptor.Zzfx("zzfx(...[,0,220,.01,.3,.3,2])");
        using var job = SongBakeJob.Start(song, Abs("Content/Music/Theme.msong"), _db);
        var result = await job.Task.WaitAsync(TimeSpan.FromMinutes(2), Ct);
        Assert.Equal(1.0, job.Progress);
        Assert.EndsWith("7 of 7", job.Status, StringComparison.Ordinal);
        Assert.Equal(Manifest, result!.ManifestPath);
    }

    private sealed class SyncProgress(Action<SongBakeProgress> report) : IProgress<SongBakeProgress>
    {
        public void Report(SongBakeProgress value) => report(value);
    }
}

/// <summary>
/// A deterministic instrument for bake tests: while a note is held it adds velocity/1270 to the left channel and half of
/// that to the right; on release the level decays by <see cref="Decay"/> per frame (its "tail").
/// </summary>
internal sealed class ToneInstrument : IInstrument
{
    public const float Decay = 0.9995f;

    private readonly Dictionary<int, float> _held = [];
    private float _release;

    public int LatencyFrames => 0;

    public void Process(TrackSnapshot track, ReadOnlySpan<NoteEvent> events, Span<float> stereo, in EffectContext context)
    {
        var next = 0;
        for (var f = 0; f < stereo.Length / 2; f++)
        {
            for (; next < events.Length && events[next].Offset <= f; next++)
            {
                var e = events[next];
                if (!e.IsNote)
                    continue;
                if (e.On)
                    _held[e.Pitch] = e.Velocity / 1270f;
                else if (_held.Remove(e.Pitch, out var level))
                    _release += level;
            }

            var value = _release;
            foreach (var level in _held.Values)
                value += level;
            _release *= Decay;
            stereo[2 * f] += value;
            stereo[2 * f + 1] += value * 0.5f;
        }
    }

    public void AllNotesOff()
    {
        _held.Clear();
        _release = 0;
    }
}
