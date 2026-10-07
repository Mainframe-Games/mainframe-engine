using System.Diagnostics;
using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>
/// The plugin helper: the real <c>mfplughost</c> (handshake, ping, timeouts, crash and restart, error replies, Ogg
/// renders that decode and loop through the engine) — skipped where this platform has no helper binary yet — and the
/// renderer against <see cref="FakePluginHost"/>.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class PluginHostTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("mf-plughost-").FullName;
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        AssetDatabase.Current = _previous;
        Directory.Delete(_root, recursive: true);
    }

    private static PluginHostClient RealHost()
    {
        var path = PluginHostClient.Locate();
        Assert.SkipWhen(path is null, $"No {PluginHostClient.ExecutableName} for this platform yet (Windows/Linux binaries come from natives.yml).");
        return new PluginHostClient(path!);
    }

    private static Song Loop(int bars)
    {
        var song = NewSong();
        song.Loop = new SongLoop { Enabled = true, Start = 0, End = 960 * 4 * bars };
        var lead = AddInstrumentTrack(song, "lead", InstrumentDescriptor.Zzfx("zzfx(...[,0,220,.01,.3,.3,2])"));
        AddClip(lead, 0, 960 * 4 * bars, (60, 0, 480), (67, 960, 480), (72, 960 * 3, 960));
        return song;
    }

    [Fact]
    public void TheHelperShakesHandsAndAnswersPings()
    {
        using var host = RealHost();
        var info = host.Start();
        Assert.Equal(PluginHostProtocol.Version, info.ProtocolVersion);
        Assert.True(info.Capabilities.HasFlag(PluginHostCapabilities.Encode));
        Assert.False(string.IsNullOrEmpty(info.HelperVersion));
        Assert.True(host.IsRunning);
        host.Ping();
        Assert.Same(info, host.Start()); // already running: no new helper
    }

    [Fact]
    public void ATimeoutStopsTheHelperAndTheNextRequestRestartsIt()
    {
        using var host = RealHost();
        var stops = new List<PluginHostStop>();
        var restarts = new List<PluginHostInfo>();
        host.Stopped += stops.Add;
        host.Restarted += restarts.Add;
        var first = host.Start();

        Assert.Throws<PluginHostTimeoutException>(() => host.Sleep(TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(200)));
        Assert.False(host.IsRunning);
        Assert.Equal("sleep", Assert.Single(stops).Request);

        host.Ping();
        Assert.True(host.IsRunning);
        Assert.NotEqual(first.ProcessId, Assert.Single(restarts).ProcessId);
    }

    [Fact]
    public void ACrashDuringARequestIsReportedAndRecovered()
    {
        using var host = RealHost();
        var stops = new List<PluginHostStop>();
        var restarts = 0;
        host.Stopped += stops.Add;
        host.Restarted += _ => restarts++;
        var pid = host.Start().ProcessId;

        using var killer = Task.Run(async () =>
        {
            await Task.Delay(200, Ct);
            using var process = Process.GetProcessById(pid);
            process.Kill();
        }, Ct);
        var error = Assert.Throws<PluginHostException>(() => host.Sleep(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)));
        Assert.IsNotType<PluginHostTimeoutException>(error);
        Assert.Contains("Plugin host", Assert.Single(stops).Reason, StringComparison.Ordinal);

        // A crash between requests is noticed by the next one, which restarts the helper.
        host.Ping();
        host.KillForTesting();
        host.Ping();
        Assert.Equal(2, stops.Count);
        Assert.Equal(2, restarts);
    }

    [Fact]
    public void AnErrorReplyKeepsTheHelperRunningAndDisposeStopsIt()
    {
        var bad = Path.Combine(_root, "bad.wav");
        File.WriteAllText(bad, "not a wav");
        var host = RealHost();
        int pid;
        try
        {
            pid = host.Start().ProcessId;
            var error = Assert.Throws<PluginHostException>(() => host.Encode(bad, Path.Combine(_root, "bad.ogg"), 6, Ct));
            Assert.Contains("RIFF", error.Message, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(_root, "bad.ogg")));
            Assert.Equal(pid, host.Info!.ProcessId);
            host.Ping();
        }
        finally
        {
            host.Dispose();
        }

        Assert.False(host.IsRunning);
        Assert.ThrowsAny<ArgumentException>(() => Process.GetProcessById(pid)); // shut down, not orphaned
        Assert.Throws<ObjectDisposedException>(() => host.Ping());
    }

    [Fact]
    public void RenderToOggDecodesThroughTheEngineAndLoops()
    {
        using var host = RealHost();
        var db = new AssetDatabase(_root);
        AssetDatabase.Current = db;
        var song = Loop(bars: 1); // a two-second loop at 120 BPM
        Assert.Null(song.Render.Output); // new songs render to Content/Music/<name>.ogg
        var result = SongRenderer.Render(song, "Theme", db, encoder: host, cancellation: Ct);

        Assert.Equal("Content/Music/Theme.ogg", result.OutputPath);
        Assert.Equal(2L * Rate, result.Frames);
        var full = Path.Combine(_root, "Content", "Music", "Theme.ogg");
        Assert.Equal("OggS", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(full), 0, 4));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(full)!, "*.tmp"));
        Assert.True(new FileInfo(full).Length < result.Frames * 2 * 4 / 8); // far smaller than the float WAV
        var meta = db.ReadOrCreateMeta(result.OutputPath, create: false)!;
        Assert.True(meta.Settings!["loop"].GetBoolean());

        var memory = new AudioStream { File = result.OutputPath, LoadMode = AudioLoadMode.Memory };
        Assert.True(memory.Preload(), memory.LoadError);
        Assert.Equal((2, Rate), (memory.Channels, memory.SampleRate));
        Assert.Equal(result.Frames * 2, memory.DecodedSamples.Length); // exact length: the loop stays seamless
        Assert.True(Energy(memory.DecodedSamples.Span[..(Rate / 50)]) > 0.001f); // the folded-in tail

        var stream = AudioStream.Load(result.OutputPath);
        Assert.True(stream.Loop);
        using var server = AudioServer.Create(new AudioOptions { Device = AudioDeviceMode.NullManual, BusLayoutPath = null });
        var handle = server.PlayOneShot(stream, bus: "Music");
        var chunk = new float[4800 * 2];
        var late = 0f;
        for (var i = 0; i < 50; i++) // five seconds: past the end twice
        {
            server.WaitForStreams(4800, TimeSpan.FromSeconds(5));
            server.RenderNullDevice(4800, chunk);
            if (i >= 40)
                late += Energy(chunk);
        }

        Assert.True(server.IsPlaying(handle));
        Assert.True(late > 0.1f);
    }

    [Fact]
    public void WithoutAHelperAnOggOutputIsWrittenAsWav()
    {
        var db = new AssetDatabase(_root);
        var result = SongRenderer.Render(Loop(bars: 1), "Theme", db, cancellation: Ct);
        Assert.Equal("Content/Music/Theme.wav", result.OutputPath);
        Assert.Equal("Content/Music/Theme.ogg", SongRenderer.OutputPathFor((string?)null, "Theme", canEncode: true));
        Assert.Equal("Content/Music/Theme.wav", SongRenderer.OutputPathFor((string?)null, "Theme", canEncode: false));
        Assert.Equal("Music/a.wav", SongRenderer.OutputPathFor("Music/a.mp3", "Theme", canEncode: true));
    }

    [Fact]
    public void AFailedEncodeLeavesThePreviousRenderIntact()
    {
        var db = new AssetDatabase(_root);
        var fake = new FakePluginHost();
        var song = Loop(bars: 1);
        var first = SongRenderer.Render(song, "Theme", db, encoder: fake, cancellation: Ct);
        Assert.Equal("Content/Music/Theme.ogg", first.OutputPath);
        var full = Path.Combine(_root, "Content", "Music", "Theme.ogg");
        var bytes = File.ReadAllBytes(full);

        var stops = new List<PluginHostStop>();
        var restarts = 0;
        fake.Stopped += stops.Add;
        fake.Restarted += _ => restarts++;
        fake.CrashOnNextRequest = true;
        song.Render.Quality = 2;
        Assert.Throws<PluginHostException>(() => SongRenderer.Render(song, "Theme", db, encoder: fake, cancellation: Ct));
        Assert.Equal(bytes, File.ReadAllBytes(full));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(full)!, "*.tmp"));
        Assert.Equal("encode", Assert.Single(stops).Request);

        fake.TimeoutOnNextRequest = true;
        Assert.Throws<PluginHostTimeoutException>(() => SongRenderer.Render(song, "Theme", db, encoder: fake, cancellation: Ct));
        Assert.Equal(2, stops.Count);

        var quality = -1;
        fake.EncodeHandler = (wav, ogg, q) =>
        {
            quality = q;
            Assert.Equal(32, BitConverter.ToUInt16(File.ReadAllBytes(wav), 34)); // the helper always gets float samples
            File.Copy(wav, ogg, overwrite: true);
            return default;
        };
        SongRenderer.Render(song, "Theme", db, encoder: fake, cancellation: Ct);
        Assert.Equal(2, quality);
        Assert.Equal(2, restarts);
        Assert.Equal(3, fake.Starts);
    }
}
