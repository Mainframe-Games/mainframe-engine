using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>2D point lights (ADR 0117): the frame's light block, the texture mapping and which items each light reaches.</summary>
public sealed class PointLight2DTests : IDisposable
{
    private readonly SceneTree _tree;
    private readonly CanvasServer _server;
    private readonly Node2D _root = new() { Name = "Root" };

    public PointLight2DTests()
    {
        _tree = new SceneTree(new ServerRegistry());
        _server = new CanvasServer(_tree, () => new Vector2(1000, 1000));
        _tree.ChangeScene(_root);
    }

    public void Dispose()
    {
        _server.Dispose();
        _tree.Shutdown();
    }

    private sealed class Box : Node2D
    {
        protected override void OnDraw() => DrawRect(new Rect2(0, 0, 10, 10), Vector4.One);
    }

    private PointLight2D AddLight(Vector2 position, float scale = 1f)
    {
        var light = new PointLight2D
        {
            Name = "Light",
            Position = position,
            Texture = Texture2D.FromPixels(64, 32, new byte[64 * 32 * 4]),
            TextureScale = scale,
            Color = new Vector4(1, 0.5f, 0.25f, 0.5f),
            Energy = 2,
        };
        _root.AddChild(light);
        return light;
    }

    private CanvasFrame Frame()
    {
        _server.Process(new GameTime { DeltaTime = 1f / 60f });
        return _server.Frame;
    }

    private static Vector2 Uv(in CanvasLightData light, Vector2 vertex) =>
        new(light.MatrixX.X * vertex.X + light.MatrixX.Y * vertex.Y + light.MatrixX.Z,
            light.MatrixY.X * vertex.X + light.MatrixY.Y * vertex.Y + light.MatrixY.Z);

    [Fact]
    public void TheTextureIsCentredOnTheLightScaledAndMovedByTheCanvasTransform()
    {
        var light = AddLight(new Vector2(100, 50), scale: 2);
        light.Offset = new Vector2(8, 0);
        _root.AddChild(new Box { Name = "Box" });
        _tree.Root.CanvasTransform = Transform2D.FromTrs(new Vector2(10, 0), 0, new Vector2(2));

        var frame = Frame();
        var data = Assert.Single(frame.Lights);
        // Canvas centre = light + offset = (108, 50) → target pixels ×2 + (10, 0) = (226, 100): the texture's centre.
        Assert.Equal(new Vector2(0.5f, 0.5f), Uv(data, new Vector2(226, 100)));
        // 64×32 texture × scale 2 × canvas 2 = 256 target pixels wide.
        Assert.Equal(new Vector2(0, 0.5f), Uv(data, new Vector2(226 - 128, 100)));
        Assert.Equal(new Vector4(1, 0.5f, 0.25f, 1), data.Color); // alpha × energy
        Assert.Equal((uint)Light2DBlendMode.Add, data.Blend);
    }

    [Fact]
    public void ALightReachesItemsByLightMaskAndZRange()
    {
        var light = AddLight(Vector2.Zero);
        light.RangeItemCullMask = 0b10;
        light.RangeZMax = 5;
        _root.AddChild(new Box { Name = "Masked" });                         // light mask 1: not lit
        _root.AddChild(new Box { Name = "Lit", LightMask = 0b11, ZIndex = 1 }); // a different z breaks the batch
        _root.AddChild(new Box { Name = "TooHigh", LightMask = 0b10, ZIndex = 6 });

        var frame = Frame();
        Assert.Single(frame.Lights);
        Assert.Equal([0u, 1u, 0u], frame.Batches.Select(b => b.LightMask));
    }

    [Fact]
    public void DisabledHiddenOrTexturelessLightsAreLeftOut()
    {
        AddLight(Vector2.Zero).Enabled = false;
        AddLight(Vector2.Zero).Visible = false;
        AddLight(Vector2.Zero).Texture = null;
        var lit = AddLight(Vector2.Zero);
        _root.AddChild(new Box { Name = "Box" });

        var frame = Frame();
        Assert.Single(frame.Lights);
        Assert.Equal(1u, Assert.Single(frame.Batches).LightMask);

        lit.QueueFree();
        _tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        Assert.Empty(Frame().Lights);
    }

    [Fact]
    public void AFrameCarriesAtMostEightLights()
    {
        for (var i = 0; i < CanvasFrame.MaxLights + 2; i++)
            AddLight(new Vector2(i, 0));
        _root.AddChild(new Box { Name = "Box" });

        var frame = Frame();
        Assert.Equal(CanvasFrame.MaxLights, frame.Lights.Count);
        Assert.Equal(0xFFu, Assert.Single(frame.Batches).LightMask);
    }
}
