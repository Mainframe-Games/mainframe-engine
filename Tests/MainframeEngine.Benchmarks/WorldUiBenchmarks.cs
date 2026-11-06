using System.Numerics;
using BenchmarkDotNet.Attributes;

namespace MainframeEngine.Benchmarks;

/// <summary>
/// G11 world-space UI costs on the CPU: one frame of 100 markers (projection, fade, edge clamp, sort, then RmlUi updating
/// the host document's 100 data-bound rows, budget 0.3 ms), and rebuilding the glyph mesh of a two-line
/// <see cref="Label3D"/> after its text changed. No GPU.
/// </summary>
[MemoryDiagnoser]
public class WorldUiBenchmarks : IDisposable
{
    private ServerRegistry _servers = null!;
    private SceneTree _tree = null!;
    private Camera3D _camera = null!;
    private Label3D _label = null!;
    private uint _frame;
    private int _text;

    [GlobalSetup]
    public void Setup()
    {
        _servers = new ServerRegistry();
        _servers.Register(new UiServer(options: new UiServerOptions { HotReload = false, HeadlessViewport = new Vector2(1280, 720) }));
        _tree = new SceneTree(_servers);
        _tree.Root.SetSize(new Vector2(1280, 720));
        _camera = new Camera3D { Position = new Vector3(0, 2, 20), Current = true };
        _tree.Root.AddChild(_camera);
        _camera.LookAt(Vector3.Zero);
        var layer = new UiLayer { Name = "Hud", ScaleMode = UiScaleMode.Pixels };
        layer.AddChild(new UiMarkerHost { Name = "Markers" });
        _tree.Root.AddChild(layer);
        for (var i = 0; i < 100; i++)
            _tree.Root.AddChild(new UiMarker3D
            {
                Name = $"M{i}",
                Kind = i % 10 == 0 ? "waypoint" : "nameplate",
                Text = $"Npc {i}",
                Value = i / 100f,
                Position = new Vector3(i % 10 * 3 - 15, 0, i / 10 * -3),
                ScaleWithDistance = true,
                Offscreen = MarkerOffscreen.ClampToEdge,
            });

        _label = new Label3D { Text = "Gate\nopen", FontSize = 32 };
        _tree.Root.AddChild(_label);
        for (var i = 0; i < 10; i++)
            Markers100Frame();
        Label3DRebuild();
    }

    /// <summary>One tree tick with the camera moving: every marker moves on screen and its row is re-read by RmlUi.</summary>
    [Benchmark]
    public void Markers100Frame()
    {
        _camera.Position += new Vector3(0.001f, 0, 0);
        _tree.Tick(new GameTime { DeltaTime = 1f / 60f, FrameCount = ++_frame });
    }

    /// <summary>A two-line label whose text changed: line breaking and glyph quads into a new mesh (glyphs cached).</summary>
    [Benchmark]
    public Mesh? Label3DRebuild()
    {
        _label.Text = (++_text & 1) == 0 ? "Gate\nopen" : "Gate\nshut";
        return _label.GetRenderMesh();
    }

    [GlobalCleanup]
    public void Dispose()
    {
        _tree?.Shutdown();
        _servers?.Dispose();
        GC.SuppressFinalize(this);
    }
}
