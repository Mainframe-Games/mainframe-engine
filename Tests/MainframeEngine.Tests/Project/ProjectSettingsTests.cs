using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Silk.NET.Input;

namespace MainframeEngine.Tests.Project;

public sealed class ProjectSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "mf-project-tests", Guid.NewGuid().ToString("N"));

    public ProjectSettingsTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static ProjectSettings Parse(string json) => ProjectSettings.Parse(Encoding.UTF8.GetBytes(json), "test.mfproj");

    private static string Text(ProjectSettings settings) => Encoding.UTF8.GetString(settings.ToJson());

    internal static ProjectSettings Everything()
    {
        var s = new ProjectSettings
        {
            Name = "Space Game ✓",
            MainScene = "scn_0123456789ab",
            EngineVersion = "1.4.2",
            Version = "2026.9.2",
            SteamAppId = 480,
            Input = new InputMap().Bind("jump", "key:Space", "pad:A").Bind("move_left", "key:A", "axis:LeftX-"),
        };
        s.Input.GetAction("move_left")!.Deadzone = 0.25f;
        s.Assemblies.Add("SpaceGame");
        s.Window.Title = "Space!";
        s.Window.Width = 1920;
        s.Window.Height = 1080;
        s.Window.VSync = false;
        s.Window.MaxFps = 144;
        s.Window.Icon = "Content/icon.png";
        s.Physics.TicksPerSecond = 120;
        s.Physics.MaxStepsPerFrame = 8;
        s.Physics.Physics3D.Gravity = new Vector3(0, -20, 0);
        s.Physics.Physics3D.SubstepCount = 2;
        s.Physics.Physics3D.SolverIterations = 8;
        s.Physics.Physics3D.RelaxationIterations = 3;
        s.Physics.Physics3D.AllowDeactivation = false;
        s.Physics.Physics3D.MultiThreaded = false;
        s.Physics.Physics3D.Deterministic = true;
        s.Physics.Physics2D.Gravity = new Vector2(0, -500);
        s.Physics.Physics2D.PixelsPerMeter = 64;
        s.Physics.Physics2D.SubstepCount = 8;
        s.Physics.Physics2D.AllowSleep = false;
        s.Physics.Physics2D.EnableContinuous = false;
        s.Audio.Enabled = false;
        s.Audio.BusLayout = "res_00000000beef";
        s.Audio.SampleRate = 44100;
        s.Audio.BufferMilliseconds = 20;
        s.Localization.DefaultLocale = "es";
        s.Localization.SourceLocale = "en_GB";
        s.Localization.Fallbacks.AddRange(["es", "fr"]);
        s.Localization.LocaleDirectory = "Content/lang";
        s.Localization.Domain = "game";
        s.Rendering.Exposure = 1.1f;
        s.Rendering.Shadows = ShadowQuality.Low;
        s.Autoloads.Add(new AutoloadSettings { Name = "Music", Scene = "Content/Autoload/Music.mscene" });
        s.Autoloads.Add(new AutoloadSettings { Name = "Stats", Type = "GameStats", Enabled = false });
        return s;
    }

    private static void AssertSame(ProjectSettings expected, ProjectSettings actual)
    {
        Assert.Equal(expected.Name, actual.Name);
        Assert.Equal(expected.MainScene, actual.MainScene);
        Assert.Equal(expected.EngineVersion, actual.EngineVersion);
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.SteamAppId, actual.SteamAppId);
        Assert.Equal(expected.Assemblies, actual.Assemblies);
        Assert.Equivalent(expected.Window, actual.Window, strict: true);
        Assert.Equal(expected.Physics.TicksPerSecond, actual.Physics.TicksPerSecond);
        Assert.Equal(expected.Physics.MaxStepsPerFrame, actual.Physics.MaxStepsPerFrame);
        Assert.Equivalent(expected.Physics.Physics3D, actual.Physics.Physics3D, strict: true);
        Assert.Equivalent(expected.Physics.Physics2D, actual.Physics.Physics2D, strict: true);
        Assert.Equivalent(expected.Audio, actual.Audio, strict: true);
        Assert.Equivalent(expected.Localization, actual.Localization, strict: true);
        Assert.Equivalent(expected.Rendering, actual.Rendering, strict: true);
        Assert.Equal(expected.Autoloads.Select(a => (a.Name, a.Scene, a.Type, a.Enabled)), actual.Autoloads.Select(a => (a.Name, a.Scene, a.Type, a.Enabled)));
        Assert.Equal(expected.Input.Actions.Select(a => (a.Name, a.Deadzone, string.Join(' ', a.Bindings))),
            actual.Input.Actions.Select(a => (a.Name, a.Deadzone, string.Join(' ', a.Bindings))));
    }

    [Fact]
    public void DefaultsWriteOnlyTheIdentity()
    {
        var json = Text(new ProjectSettings { Name = "Tiny", EngineVersion = "0.3.0" });

        Assert.Equal("{\n  \"format\": 1,\n  \"name\": \"Tiny\",\n  \"engineVersion\": \"0.3.0\"\n}\n", json);
        AssertSame(new ProjectSettings { Name = "Tiny", EngineVersion = "0.3.0" }, Parse(json));
    }

    [Fact]
    public void EverySettingRoundTripsAndRewritesIdentically()
    {
        var original = Everything();
        var bytes = original.ToJson();
        var parsed = ProjectSettings.Parse(bytes, "x");

        AssertSame(original, parsed);
        Assert.Equal(bytes, parsed.ToJson());
        Assert.DoesNotContain("\r", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Space Game ✓\"", Encoding.UTF8.GetString(bytes), StringComparison.Ordinal); // not escaped
    }

    [Fact]
    public void TheInputSectionIsHumanReadable()
    {
        var json = JsonNode.Parse(Text(Everything()))!;

        Assert.Equal("key:Space", (string?)json["input"]!["jump"]!["bindings"]![0]);
        Assert.Equal("pad:A", (string?)json["input"]!["jump"]!["bindings"]![1]);
        Assert.Equal("axis:LeftX-", (string?)json["input"]!["move_left"]!["bindings"]![1]);
        Assert.Equal(0.25, (double)json["input"]!["move_left"]!["deadzone"]!, 3);
        Assert.Null(json["input"]!["jump"]!["deadzone"]); // the default is not written
        Assert.Equal("Low", (string?)json["rendering"]!["shadows"]);
    }

    [Fact]
    public void HandEditedFilesWithCommentsAndTrailingCommasLoad()
    {
        var s = Parse("""
            {
              // a hand edit
              "format": 1,
              "name": "Edited",
              "mainScene": "Content/Scenes/Main.mscene",
              "window": { "width": 640, "height": 480, },
              "input": { "fire": { "bindings": ["mouse:Left", "KEY:enter"] } },
            }
            """);

        Assert.Equal("Edited", s.Name);
        Assert.Equal(640, s.Window.Width);
        Assert.Equal(InputBinding.Key(Key.Enter), s.Input.GetAction("fire")!.Bindings[1]);
        Assert.Equal(EngineInfo.Version, s.EngineVersion); // missing → this engine
    }

    [Fact]
    public void UnknownSettingsAreIgnored()
    {
        var s = Parse("""{ "format": 1, "name": "N", "future": 1, "window": { "fullscreen": true } }""");

        Assert.Equal("N", s.Name);
    }

    [Theory]
    [InlineData("""{ "format": 1, "window": { "width": "wide" } }""", "window.width must be an integer")]
    [InlineData("""{ "format": 1, "window": { "width": 0 } }""", "window.width must be between 1 and 16384")]
    [InlineData("""{ "format": 1, "window": 3 }""", "window must be an object")]
    [InlineData("""{ "format": 1, "name": "" }""", "name is invalid")]
    [InlineData("""{ "format": 1, "physics": { "3d": { "gravity": [0, 1] } } }""", "physics.3d.gravity must have 3 numbers")]
    [InlineData("""{ "format": 1, "physics": { "2d": { "pixelsPerMeter": -1 } } }""", "physics.2d.pixelsPerMeter is invalid")]
    [InlineData("""{ "format": 1, "input": { "jump": { "bindings": ["key:Nope"] } } }""", "input.jump.bindings 'key:Nope' is not a binding")]
    [InlineData("""{ "format": 1, "input": { "jump": ["key:Space"] } }""", "input.jump must be an object")]
    [InlineData("""{ "format": 1, "rendering": { "shadows": "Ultra" } }""", "rendering.shadows 'Ultra' is not one of")]
    [InlineData("""{ "format": 1, "rendering": { "exposure": 0 } }""", "rendering.exposure is invalid")]
    [InlineData("""{ "format": 1, "autoloads": [ { "name": "A", "scene": "s", "type": "T" } ] }""", "needs exactly one of")]
    [InlineData("""{ "format": 1, "autoloads": [ { "name": "A", "type": "T" }, { "name": "A", "type": "U" } ] }""", "'A' is used by another autoload")]
    [InlineData("""{ "format": 1, "autoloads": [ { "name": "a/b", "type": "T" } ] }""", "must be a node name")]
    [InlineData("""{ "format": 1, "assemblies": [1] }""", "assemblies must contain only strings")]
    [InlineData("""{ "format": 99 }""", "has project format 99; this engine reads up to 1")]
    [InlineData("""{ "format": 0 }""", "invalid project format 0")]
    [InlineData("""[1, 2]""", "is not a JSON object")]
    [InlineData("""{ "format": 1, """, "is not valid JSON")]
    [InlineData("""{ "format": 1, "name": "A", "name": "B" }""", "is invalid")]
    [InlineData("""{ "format": 1, "window": { "width": 1, "width": 2 } }""", "is invalid")]
    public void InvalidFilesNameTheProblem(string json, string expected)
    {
        var e = Assert.Throws<InvalidDataException>(() => Parse(json));

        Assert.Contains(expected, e.Message, StringComparison.Ordinal);
        Assert.Contains("test.mfproj", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OlderFormatsMigrateStepByStep()
    {
        // A pretend history: format 1 had a top-level "tickRate"; format 2 moved it into "physics"; format 3 renamed
        // "title" to "name".
        var order = new List<int>();
        ProjectMigration[] migrations =
        [
            new(2, root =>
            {
                order.Add(2);
                root["name"] = root["title"]?.DeepClone();
                root.Remove("title");
            }),
            new(1, root =>
            {
                order.Add(1);
                root["physics"] = new JsonObject { ["ticksPerSecond"] = root["tickRate"]!.DeepClone() };
                root.Remove("tickRate");
            }),
        ];

        var settings = ProjectSettingsFormat.Parse("""{ "format": 1, "title": "Old", "tickRate": 30 }"""u8, "old.mfproj", 3, migrations);

        Assert.Equal([1, 2], order);
        Assert.Equal("Old", settings.Name);
        Assert.Equal(30, settings.Physics.TicksPerSecond);

        var current = ProjectSettingsFormat.Parse("""{ "format": 3, "name": "New" }"""u8, "new.mfproj", 3, migrations);
        Assert.Equal("New", current.Name);
        Assert.Equal([1, 2], order); // no step ran for a current file
    }

    [Fact]
    public void AMissingMigrationStepIsAnError()
    {
        var e = Assert.Throws<InvalidDataException>(() =>
            ProjectSettingsFormat.Parse("""{ "format": 1 }"""u8, "gap.mfproj", 3, [new ProjectMigration(1, _ => { })]));

        Assert.Contains("no migration from project format 2 to 3", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UpgradeStampsTheCurrentFormat()
    {
        var root = new JsonObject { ["format"] = 1 };
        ProjectSettingsFormat.Upgrade(root, 1, 2, [new ProjectMigration(1, r => r["upgraded"] = true)], "x");

        Assert.Equal(2, (int)root["format"]!);
        Assert.True((bool)root["upgraded"]!);
    }

    [Fact]
    public void SaveAndLoadByFileOrFolder()
    {
        var settings = Everything();
        settings.Save(_directory);

        var file = Path.Combine(_directory, ProjectSettings.FileName);
        Assert.Equal(file, settings.FilePath);
        Assert.False(File.Exists(file + ".tmp"));

        var byFolder = ProjectSettings.Load(_directory);
        var byFile = ProjectSettings.Load(file);
        AssertSame(settings, byFolder);
        AssertSame(settings, byFile);
        Assert.Equal(file, byFolder.FilePath);
        Assert.Equal(_directory, byFolder.ProjectDirectory);

        Assert.Throws<FileNotFoundException>(() => ProjectSettings.Load(Path.Combine(_directory, "missing")));
    }

    [Fact]
    public void EngineOptionsCarryTheSettings()
    {
        var s = Everything();
        var o = s.ToEngineOptions();

        Assert.Equal("Space!", o.GameName);
        Assert.Equal((1920, 1080), (o.WindowSize.X, o.WindowSize.Y));
        Assert.Equal("Content/icon.png", o.IconPath);
        Assert.False(o.VSync);
        Assert.Equal(120, o.PhysicsTicksPerSecond);
        Assert.Same(s.Physics.Physics3D, o.Physics3D);
        Assert.Same(s.Physics.Physics2D, o.Physics2D);
        Assert.Equal(480u, o.SteamAppId);
        Assert.False(o.Audio.Enabled);
        Assert.Equal("res_00000000beef", o.Audio.BusLayoutPath);
        Assert.Equal(44100, o.Audio.SampleRate);
        Assert.Equal(20, o.Audio.BufferMilliseconds);
        Assert.Equal("es", o.Locale);
        Assert.NotNull(o.Localization);
        Assert.Equal("game", o.Localization.Domain);
        Assert.Equal("Content/lang", o.Localization.LocaleDirectory);
        Assert.Equal("en_GB", o.Localization.SourceLocale);
        Assert.Equal(["es", "fr"], o.Localization.FallbackLocales);

        var defaults = new ProjectSettings().ToEngineOptions();
        Assert.Null(defaults.Locale); // the OS language when a catalog exists, else the source locale
        Assert.Equal(new MainframeEngine.Localization.LocalizationOptions(), defaults.Localization! with { FallbackLocales = [] });

        Assert.Equal("Untitled", new ProjectSettings { Name = "Untitled" }.ToEngineOptions().GameName); // title defaults to the name
    }

    [Fact]
    public void ShadowQualityLevelsScaleTheShadowBudget()
    {
        var low = ShadowQualitySettings.For(ShadowQuality.Low);
        var medium = ShadowQualitySettings.For(ShadowQuality.Medium);
        var high = ShadowQualitySettings.For(ShadowQuality.High);

        // High is exactly what a new shadow system starts with (the render tests and the Demo).
        Assert.Equal(new ShadowQualitySettings(ShadowPlanner.DefaultMaxAtlasSize, new ShadowPlanner().Filter, new ShadowPlanner().FilterRadius,
            new ShadowPlanner().CascadeLimit, new ShadowPlanner().ResolutionLimit), high);
        Assert.Equal((1024, ShadowFilter.Hard, 2, 1024), (low.MaxAtlasSize, low.Filter, low.CascadeLimit, low.ResolutionLimit));
        Assert.Equal((2048, ShadowFilter.Pcf3x3, 3, 2048), (medium.MaxAtlasSize, medium.Filter, medium.CascadeLimit, medium.ResolutionLimit));
        Assert.True(low.MaxAtlasSize < medium.MaxAtlasSize && medium.MaxAtlasSize < high.MaxAtlasSize);
        Assert.True(low.CascadeLimit < medium.CascadeLimit && medium.CascadeLimit < high.CascadeLimit);
        Assert.Equal(ShadowQuality.High, new ProjectSettings().Rendering.Shadows);
        Assert.Throws<ArgumentOutOfRangeException>(() => ShadowQualitySettings.For((ShadowQuality)42));
    }

    [Fact]
    public void TheRenderServerAppliesShadowQuality()
    {
        var render = new RenderServer(new HeadlessRenderer());
        Assert.Equal(ShadowQuality.High, render.ShadowQuality);
        render.ShadowQuality = ShadowQuality.Off;
        Assert.False(render.ShadowsEnabled);
        Assert.Null(render.Shadows);
        render.ShadowQuality = ShadowQuality.Medium;
        Assert.True(render.ShadowsEnabled);
        Assert.Throws<ArgumentOutOfRangeException>(() => render.ShadowQuality = (ShadowQuality)9);
        Assert.Equal(ShadowQuality.Medium, render.ShadowQuality);
    }

    [Theory]
    [InlineData("1.2.3", "1.2.9", false)]
    [InlineData("1.2.3", "1.3.0", true)]
    [InlineData("2.0.0", "1.0.0", true)]
    [InlineData("0.0.0-dev", "1.0.0", false)]
    [InlineData("1.0.0", "0.0.0-dev", false)]
    [InlineData("garbage", "1.0.0", false)]
    [InlineData("1.2.3+abc", "1.2.0-rc.1", false)]
    public void ReleaseComparisonIgnoresPatchAndDevBuilds(string project, string engine, bool different) =>
        Assert.Equal(different, EngineInfo.IsDifferentRelease(project, engine));

    [Fact]
    public void EngineVersionHasNoBuildMetadata()
    {
        Assert.False(string.IsNullOrWhiteSpace(EngineInfo.Version));
        Assert.DoesNotContain('+', EngineInfo.Version);
        Assert.Equal(EngineInfo.Version, new ProjectSettings().EngineVersion);
    }

    /// <summary>An <see cref="IRenderer"/> that is not an <see cref="IVulkanContext"/> (no shadow system is ever created).</summary>
    private sealed class HeadlessRenderer : IRenderer
    {
        public RenderingBackend Backend => RenderingBackend.Vulkan;
        public bool VSync { get; set; }
        public void OnResize(Silk.NET.Maths.Vector2D<int> newSize) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void SetClearColor(float r, float g, float b, float a = 1) { }
        public void Clear() { }
        public void EnableDepthTest() { }
        public void DisableDepthTest() { }
        public void RequestCapture() { }

        public bool TryTakeCapture([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FrameCapture? capture)
        {
            capture = null;
            return false;
        }

        public void Dispose() { }
    }
}
