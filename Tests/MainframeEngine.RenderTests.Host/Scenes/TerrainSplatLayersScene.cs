using System.Numerics;

namespace MainframeEngine.RenderTests.Host.Scenes;

using Color = System.Drawing.Color;

/// <summary>
/// Splat layers 8–11 (the third weight map, <c>splat-2.png</c>): two flat Realistic terrains, each drawn with a
/// twelve-layer <see cref="TerrainSplatMaterial3D"/> of plain tinted layers (no textures). The west terrain is painted in
/// four strips: layers 0 (red), 4 (green), 9 (blue) and 11 (yellow), so it has a third map; the east one only layers 0
/// and 4, so it keeps two maps, and its green strip must not pick up layer 8 (magenta) from the second map (a two-layer
/// weight array clamps layer 2 to layer 1 if the shader read it). Self-checks the captured frame's colour at each strip's
/// centre; no golden.
/// </summary>
public sealed class TerrainSplatLayersScene(HostOptions host) : RenderTestGame(host)
{
    private const int Size = 32;
    private Camera3D _camera = null!;
    private Terrain3D _west = null!;
    private Terrain3D _east = null!;

    private static readonly Color[] Tints = BuildTints();

    private static Color[] BuildTints()
    {
        var tints = Enumerable.Repeat(Color.FromArgb(255, 128, 128, 128), TerrainSplatMaterial3D.MaxLayers).ToArray();
        tints[0] = Color.FromArgb(255, 230, 40, 40);    // red
        tints[4] = Color.FromArgb(255, 40, 210, 40);    // green
        tints[8] = Color.FromArgb(255, 230, 40, 230);   // magenta: must not appear
        tints[9] = Color.FromArgb(255, 40, 70, 235);    // blue
        tints[11] = Color.FromArgb(255, 235, 220, 40);  // yellow
        return tints;
    }

    // The layer of each 8 m strip along local x.
    private static readonly int[] WestStrips = [0, 4, 9, 11];
    private static readonly int[] EastStrips = [0, 0, 4, 4];

    protected override void LoadScene()
    {
        var scene = new Node3D { Name = nameof(TerrainSplatLayersScene) };
        _camera = new Camera3D { Name = "Camera", Position = new Vector3(0, 46, 34) };
        _camera.LookAt(Vector3.Zero);
        scene.AddChild(_camera);
        scene.AddChild(new WorldEnvironment { Name = "Environment", Sky = new Sky { Mode = SkyEnvironmentType.Procedural } });
        var sun = new DirectionalLight3D { Name = "Sun", Energy = 1f };
        sun.LookAt(Vector3.Normalize(new Vector3(0.2f, -1f, -0.3f)));
        scene.AddChild(sun);

        _west = Terrain("West", -Size - 2, WestStrips);
        _east = Terrain("East", 2, EastStrips);
        scene.AddChild(_west);
        scene.AddChild(_east);
        if (_west.Data!.SplatImageCount != 3 || _east.Data!.SplatImageCount != 2)
            Fail($"splat maps: west {_west.Data.SplatImageCount} (expected 3), east {_east.Data!.SplatImageCount} (expected 2)");
        Tree.ChangeScene(scene);
    }

    private static Terrain3D Terrain(string name, float x, int[] strips)
    {
        var data = TerrainData.Create(TerrainProfile.Realistic, Size, 1f, 16);
        data.SetWeightsFrom((lx, _, w) => w[strips[Math.Clamp((int)(lx / 8f), 0, 3)]] = 1f);
        var layers = new TerrainLayer[TerrainSplatMaterial3D.MaxLayers];
        for (var i = 0; i < layers.Length; i++)
            layers[i] = new TerrainLayer { Name = $"Layer {i}", Tint = Tints[i] };
        return new Terrain3D
        {
            Name = name,
            Position = new Vector3(x, 0, -Size / 2f),
            Data = data,
            Material = new TerrainSplatMaterial3D { LayerTextureSize = 4, MacroStrength = 0f, AntiTiling = false, Layers = layers },
        };
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
    }

    protected override void OnFrameCaptured(FrameCapture capture)
    {
        base.OnFrameCaptured(capture);
        for (var k = 0; k < 4; k++)
        {
            Check(capture, _west, k, WestStrips[k]);
            Check(capture, _east, k, EastStrips[k]);
        }
    }

    // The strip's centre must show its layer's hue: the tint's strongest channel(s) clearly above the others.
    private void Check(FrameCapture capture, Terrain3D terrain, int strip, int layer)
    {
        var world = terrain.Position + new Vector3(strip * 8f + 4f, 0f, Size / 2f);
        var viewport = GetViewportSize();
        var p = _camera.UnprojectPosition(world) / viewport * new Vector2(capture.Width, capture.Height);
        var i = ((int)p.Y * capture.Width + (int)p.X) * 4;
        int r = capture.Pixels[i], g = capture.Pixels[i + 1], b = capture.Pixels[i + 2];
        const int margin = 30;
        var ok = layer switch
        {
            0 => r > g + margin && r > b + margin,
            4 => g > r + margin && g > b + margin,
            9 => b > r + margin && b > g + margin,
            11 => r > b + margin && g > b + margin,
            _ => false,
        };
        if (!ok)
            Fail($"{terrain.Name} strip {strip} (layer {layer}) at pixel ({(int)p.X}, {(int)p.Y}) is ({r}, {g}, {b})");
    }

    private Vector2 GetViewportSize() => _camera.GetViewport()?.Size ?? Vector2.One;

    protected override void DisposeScene()
    {
    }
}
