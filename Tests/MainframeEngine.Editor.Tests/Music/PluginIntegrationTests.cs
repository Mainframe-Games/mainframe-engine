using MainframeEngine.Editor.Music;
using static MainframeEngine.Editor.Tests.Music.MusicTestUtil;

namespace MainframeEngine.Editor.Tests.Music;

/// <summary>
/// The real helper with the native test bundle (<c>mf_test_plugins.vst3</c>, built by <c>Native/build.sh</c> under
/// <c>Native/build</c>): scan, an instrument rendering notes, an effect chain, offline render, and state through a
/// <c>.msong</c> save and load. Skipped when the helper or the bundle is not built for this platform.
/// </summary>
public sealed class PluginIntegrationTests : IDisposable
{
    private const string GainId = "4D46544741494E000000000000000001";
    private const string SynthId = "4D465453594E54000000000000000002";

    private readonly string _root = Directory.CreateTempSubdirectory("mf-vst3-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static (string Helper, string Bundle) Fixture()
    {
        var helper = PluginHostClient.Locate();
        Assert.SkipWhen(helper is null, $"No {PluginHostClient.ExecutableName} for this platform yet (natives.yml builds it).");
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MainframeEngine.slnx")))
            dir = dir.Parent;
        var bundle = dir is null ? null : Path.Combine(dir.FullName, "Native", "build", "PluginHost", "tests", "plugins", "mf_test_plugins.vst3");
        Assert.SkipWhen(bundle is null || !Path.Exists(bundle), "The test plugin bundle is not built (run Native/build.sh).");
        using var probe = new PluginHostClient(helper!);
        Assert.SkipWhen(!probe.Start().Capabilities.HasFlag(PluginHostCapabilities.Vst3), "This platform's committed mfplughost predates VST3 (natives.yml refreshes it).");
        return (helper!, bundle!);
    }

    private static Func<int, PluginRack?> Rack(string helper, string bundle, bool offline) =>
        rate => new PluginRack(() => new PluginHostClient(helper), rate, _ => bundle, offline);

    private static Song SynthSong(params string[] inserts)
    {
        var song = NewSong();
        var track = AddInstrumentTrack(song, "lead", new InstrumentDescriptor { Plugin = new PluginDescriptor { Format = "vst3", ClassId = SynthId, Name = "MF Test Synth" } });
        AddClip(track, 0, 960 * 4, (69, 0, 960), (72, 960 * 2, 960));
        foreach (var id in inserts)
            track.Inserts.Add(new PluginInsert { Plugin = new PluginDescriptor { Format = "vst3", ClassId = id, Name = id } });
        song.Render.SampleRate = Rate;
        song.Render.TailSeconds = 0;
        return song;
    }

    [Fact]
    public void ScanFindsTheTestClassesAndCachesThem()
    {
        var (helper, bundle) = Fixture();
        var cache = Path.Combine(_root, "plugins.json");
        var scanner = new PluginScanner(helper, cache);
        var catalog = scanner.Scan([Path.GetDirectoryName(bundle)!], cancellation: TestContext.Current.CancellationToken);
        Assert.Contains(catalog.Instruments, p => p.ClassId == SynthId && p.Name == "MF Test Synth" && p.Vendor == "Mainframe Games");
        Assert.Contains(catalog.Effects, p => p.ClassId == GainId);
        Assert.Equal(bundle, catalog.Find(GainId)!.BundlePath);
        Assert.True(File.Exists(cache));
        Assert.Equal(3, PluginScanner.LoadCache(cache).Plugins.Count);

        var broken = Path.Combine(_root, "Broken.vst3");
        Directory.CreateDirectory(broken);
        var failed = scanner.Scan([_root], cancellation: TestContext.Current.CancellationToken);
        Assert.Contains(failed.Failures, f => f.Bundle == broken);
    }

    [Fact]
    public void AnInstrumentRendersNotesOfflineAndAnEffectChainScalesThem()
    {
        var (helper, bundle) = Fixture();
        var dry = SongRenderer.RenderToBuffer(SynthSong(), out _, plugins: Rack(helper, bundle, offline: true), cancellation: TestContext.Current.CancellationToken);
        var wet = SongRenderer.RenderToBuffer(SynthSong(GainId), out _, plugins: Rack(helper, bundle, offline: true), cancellation: TestContext.Current.CancellationToken);
        var dryEnergy = Energy(dry);
        Assert.True(dryEnergy > 0.01f, $"the synth plays: {dryEnergy}");
        Assert.InRange(Energy(wet) / dryEnergy, 0.24f, 0.26f); // gain 0.5 → a quarter of the energy
    }

    [Fact]
    public void LivePlaybackRunsTheChainInOneRoundTripPerBlock()
    {
        var (helper, bundle) = Fixture();
        var song = SynthSong(GainId);
        using var rack = Rack(helper, bundle, offline: false)(Rate)!;
        var builder = new SongSnapshotBuilder(Rate, plugins: rack);
        var engine = new SongEngine(Rate);
        engine.SetSnapshot(builder.Build(song));
        engine.Play();
        Assert.All(rack.Instances, i => Assert.True(i.IsLoaded, i.Error));
        var output = Run(engine, 40);
        Assert.True(Energy(output) > 0);
        builder.ReleasePlugins();
        Assert.Empty(rack.Instances);
    }

    [Fact]
    public void PluginStateSurvivesSavingAndLoadingTheSong()
    {
        var (helper, bundle) = Fixture();
        var path = Path.Combine(_root, "state.msong");
        using (var document = new SongDocument(SynthSong(GainId), path))
        using (var player = new SongPlayer(null, plugins: Rack(helper, bundle, offline: false)))
        {
            player.Update(document);
            var gain = player.Plugins!.Instances.Single(i => !i.IsInstrument);
            Assert.True(gain.IsLoaded, gain.Error);
            var quarter = BitConverter.GetBytes(0.25f);
            byte[] state = [4, 0, 0, 0, .. quarter, 0, 0, 0, 0];
            player.Plugins.Host!.SetPluginState(gain.HelperId, state);
            player.CapturePluginStates(document);
            document.Save();
        }

        var loaded = SongFormat.Load(path).Song;
        var saved = loaded.Tracks[0].Inserts[0].State;
        Assert.False(string.IsNullOrEmpty(saved));
        using var again = new SongDocument(loaded, path);
        using var player2 = new SongPlayer(null, plugins: Rack(helper, bundle, offline: false));
        player2.Update(again);
        var reloaded = player2.Plugins!.Instances.Single(i => !i.IsInstrument);
        var bytes = player2.Plugins.Host!.GetPluginState(reloaded.HelperId);
        Assert.Equal(0.25f, BitConverter.ToSingle(bytes, 4));
    }
}
