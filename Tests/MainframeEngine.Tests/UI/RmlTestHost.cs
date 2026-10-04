using System.Numerics;
using System.Text;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Tests.UI;

/// <summary>RmlUi is process-global: every test that initialises it runs in this serial collection.</summary>
[CollectionDefinition(nameof(SerialRmlUi), DisableParallelization = true)]
public sealed class SerialRmlUi;

/// <summary>A render interface that records what RmlUi asks for, with live geometry/texture tracking for leak checks.</summary>
public sealed class RecordingRenderInterface : RmlRenderInterface
{
    private ulong _next;

    public HashSet<ulong> LiveGeometry { get; } = [];
    public HashSet<ulong> LiveTextures { get; } = [];
    public int Compiled { get; private set; }
    public int Rendered { get; private set; }
    public int Generated { get; private set; }
    public int ScissorChanges { get; private set; }
    public int TransformChanges { get; private set; }
    public int ClipMaskRenders { get; private set; }
    public List<string> LoadedTextures { get; } = [];
    public int VerticesCompiled { get; private set; }

    public void ResetCounters()
    {
        Compiled = Rendered = Generated = ScissorChanges = TransformChanges = ClipMaskRenders = 0;
    }

    protected override ulong CompileGeometry(ReadOnlySpan<RmlVertex> vertices, ReadOnlySpan<int> indices)
    {
        Compiled++;
        VerticesCompiled += vertices.Length;
        var h = ++_next;
        LiveGeometry.Add(h);
        return h;
    }

    protected override void RenderGeometry(ulong geometry, Vector2 translation, ulong texture)
    {
        Assert.Contains(geometry, LiveGeometry);
        Rendered++;
    }

    protected override void ReleaseGeometry(ulong geometry) => Assert.True(LiveGeometry.Remove(geometry));

    protected override ulong LoadTexture(string source, out int width, out int height)
    {
        LoadedTextures.Add(source);
        width = height = 4;
        var h = ++_next;
        LiveTextures.Add(h);
        return h;
    }

    protected override ulong GenerateTexture(ReadOnlySpan<byte> rgba, int width, int height)
    {
        Assert.Equal(width * height * 4, rgba.Length);
        Generated++;
        var h = ++_next;
        LiveTextures.Add(h);
        return h;
    }

    protected override void ReleaseTexture(ulong texture) => Assert.True(LiveTextures.Remove(texture));

    protected override void EnableScissorRegion(bool enable) => ScissorChanges++;

    protected override void SetScissorRegion(int x, int y, int width, int height) => ScissorChanges++;

    protected override void SetTransform(in Matrix4x4? transform) => TransformChanges++;

    protected override void RenderToClipMask(RmlClipMaskOperation operation, ulong geometry, Vector2 translation) => ClipMaskRenders++;
}

/// <summary>Records RmlUi log output; tests assert no unexpected errors or warnings.</summary>
public sealed class RecordingSystemInterface : RmlSystemInterface
{
    public List<string> Errors { get; } = [];
    public List<string> Warnings { get; } = [];
    public double Time { get; set; }
    public Func<string, string?>? Translate { get; set; }

    public override double GetElapsedTime() => Time;

    public override bool LogMessage(RmlLogType type, string message)
    {
        if (type is RmlLogType.Error or RmlLogType.Assert)
            Errors.Add(message);
        else if (type == RmlLogType.Warning)
            Warnings.Add(message);
        return true;
    }

    public override int TranslateString(ReadOnlySpan<byte> input, RmlStringSink output)
    {
        if (Translate is null)
            return 0;
        var result = Translate(Encoding.UTF8.GetString(input));
        if (result is null)
            return 0;
        output.Set(result.AsSpan());
        return 1;
    }
}

/// <summary>In-memory files first, then the test output's content folder.</summary>
public sealed class MemoryFileInterface : RmlContentFileInterface
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);

    public override Stream? Open(string path) =>
        Files.TryGetValue(path, out var text) ? new MemoryStream(Encoding.UTF8.GetBytes(text)) : base.Open(path);
}

/// <summary>RmlUi initialised with recording interfaces, the engine's UI font and one 800×600 context.</summary>
public sealed class RmlTestHost : IDisposable
{
    public const string FontPath = "Content/UI/fonts/LatoLatin-Regular.ttf";

    public RmlTestHost()
    {
        RmlCore.Initialise(System, Files);
        RmlCore.LoadFontFace(FontPath, fallbackFace: true);
        Context = new RmlContext("test", 800, 600, Renderer);
    }

    public RecordingRenderInterface Renderer { get; } = new();
    public RecordingSystemInterface System { get; } = new();
    public MemoryFileInterface Files { get; } = new();
    public RmlContext Context { get; private set; }

    public void Frame()
    {
        System.Time += 1.0 / 60.0;
        Context.Update();
        Context.Render();
    }

    /// <summary>Loads and shows an in-memory document (registered as a file so it can be reloaded).</summary>
    public RmlDocument Show(string rml, string path = "ui/test.rml")
    {
        Files.Files[path] = rml;
        var doc = Context.LoadDocument(path);
        doc.Show();
        Frame();
        return doc;
    }

    public void AssertNoRmlErrors()
    {
        Assert.True(System.Errors.Count == 0, string.Join("\n", System.Errors));
        Assert.True(System.Warnings.Count == 0, string.Join("\n", System.Warnings));
    }

    public void Dispose()
    {
        Context.Dispose();
        RmlCore.Shutdown();
        Assert.Empty(Renderer.LiveGeometry);
        Assert.Empty(Renderer.LiveTextures);
        Renderer.Dispose();
    }

    /// <summary>A simple themed page with a button, a text field and data bindings.</summary>
    public static string Page(string body, string? style = null) =>
        $$"""
        <rml>
        <head>
        <style>
        body { font-family: LatoLatin; font-size: 16px; color: #ffffff; width: 400px; height: 300px; }
        div, p, span, h1 { display: block; }
        button { display: block; width: 100px; height: 30px; background-color: #335; focus: auto; tab-index: auto; nav: auto; }
        input { display: block; width: 200px; height: 20px; }
        {{style ?? ""}}
        </style>
        </head>
        <body>{{body}}</body>
        </rml>
        """;
}
