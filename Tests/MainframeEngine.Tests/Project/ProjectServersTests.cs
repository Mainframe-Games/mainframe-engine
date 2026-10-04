using System.Numerics;
using System.Text;
using MainframeEngine.Serialization;
using MainframeEngine.Tests.Physics;
using MainframeEngine.Tests.Scene;

namespace MainframeEngine.Tests.Project;

/// <summary>
/// The physics and audio sections of <c>project.mfproj</c> are not just parsed: through
/// <see cref="ProjectSettings.ToEngineOptions"/> (what <see cref="GameHost"/> hands the engine, which builds its servers from
/// them) and <see cref="GameSession.Start"/> they change how the servers behave — gravity moves bodies, the tick rate and
/// step cap reach the tree, the referenced bus layout is the one the audio server mixes with.
/// </summary>
[Collection(nameof(SerialResources))] // swaps AssetDatabase.Current and the loader cache
public sealed class ProjectServersTests : IDisposable
{
    private readonly string _project = Path.Combine(Path.GetTempPath(), "mf-project-servers", Guid.NewGuid().ToString("N"));
    private readonly AssetDatabase _previous = AssetDatabase.Current;

    public ProjectServersTests()
    {
        Directory.CreateDirectory(Path.Combine(_project, AssetDatabase.ContentFolder));
        AssetDatabase.Current = new AssetDatabase(_project);
        ResourceLoader.ClearCache();
    }

    public void Dispose()
    {
        ResourceLoader.ClearCache();
        AssetDatabase.Current = _previous;
        try
        {
            Directory.Delete(_project, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void PhysicsAndAudioSettingsFromTheProjectFileReachTheServers()
    {
        var layout = AudioBusLayout.CreateDefault();
        layout.Buses.Add(new AudioBusInfo { Name = "Dialogue", Send = "SFX", VolumeDb = -6f });
        ResourceSaver.Save(layout, "Content/Settings/GameBuses.mres");
        ResourceLoader.ClearCache();

        var settings = ProjectSettings.Parse(Encoding.UTF8.GetBytes("""
            {
              "format": 1,
              "name": "Servers",
              "physics": {
                "ticksPerSecond": 120,
                "maxStepsPerFrame": 3,
                "3d": { "gravity": [0, -20, 0], "multiThreaded": false },
                "2d": { "gravity": [0, 500] }
              },
              "audio": { "busLayout": "Content/Settings/GameBuses.mres", "sampleRate": 44100, "bufferMs": 20 }
            }
            """));
        var options = settings.ToEngineOptions();

        // 3D: a body falls with the project's gravity at the project's tick rate.
        using (var physics = new PhysicsHarness3D(options.Physics3D))
        {
            physics.Tree.PhysicsTicksPerSecond = options.PhysicsTicksPerSecond;
            using var session = new GameSession(physics.Tree, settings);
            Assert.True(session.Start()); // no main scene: a warning, not a failure
            Assert.Equal(3, physics.Tree.MaxPhysicsStepsPerFrame);
            Assert.Equal(120, physics.Tree.PhysicsTicksPerSecond);

            var box = physics.AddBox(new Vector3(0, 100, 0));
            physics.RunSeconds(0.5f);
            Assert.InRange(box.LinearVelocity.Y, -10.5f, -9.5f); // v = g·t = -20 × 0.5
        }

        // 2D: the server is built with the project's settings object.
        using (var physics2D = new PhysicsServer2D(options.Physics2D))
            Assert.Equal(new Vector2(0, 500), physics2D.Settings.Gravity);

        // Audio: the referenced layout (not the built-in default) is what the server mixes with.
        var audio = options.Audio;
        Assert.Equal((44100, 20), (audio.SampleRate, audio.BufferMilliseconds));
        audio.Device = AudioDeviceMode.NullManual;
        using var server = AudioServer.Create(audio);
        Assert.Equal(-6f, server.GetBus("Dialogue")!.VolumeDb);
        Assert.Equal(AudioBusLayout.CreateDefault().Buses.Count + 1, server.Buses.Count);
    }
}

/// <summary>
/// The localization section configures <see cref="Localization.Tr"/> the way the engine does at start-up
/// (<c>Tr.Configure(options.Localization, options.Locale)</c> in the <see cref="Engine"/> constructor).
/// </summary>
[Collection(nameof(Localization.LocalizationState))]
public sealed class ProjectLocalizationTests : IDisposable
{
    private readonly Localization.LocaleFixture _locales = new();

    public void Dispose() => _locales.Dispose();

    [Fact]
    public void DefaultLocaleAndFallbacksFromTheProjectFileDriveTr()
    {
        _locales.AddCatalog("es", Localization.LocaleFixture.OneOther, """
            msgid "Hello"
            msgstr "Hola"

            """);
        var settings = new ProjectSettings { Name = "L10n" };
        settings.Localization.DefaultLocale = "pt-BR"; // no catalog of its own: the project's fallback supplies text
        settings.Localization.Fallbacks.Add("es");
        settings.Localization.LocaleDirectory = _locales.Directory;
        var options = settings.ToEngineOptions();

        MainframeEngine.Localization.Tr.Configure(options.Localization!, options.Locale);
        Assert.Equal("pt_BR", MainframeEngine.Localization.Tr.CurrentLocale);
        Assert.Equal(["pt_BR", "pt", "es", "en"], MainframeEngine.Localization.Tr.LocaleChain);
        Assert.Equal("Hola", MainframeEngine.Localization.Tr._("Hello"));

        // --locale (the player's choice) wins over the project default.
        var player = GameHostOptions.Parse(["--locale", "en"]).Apply(settings.ToEngineOptions());
        MainframeEngine.Localization.Tr.Configure(player.Localization!, player.Locale);
        Assert.Equal("Hello", MainframeEngine.Localization.Tr._("Hello"));
    }
}
