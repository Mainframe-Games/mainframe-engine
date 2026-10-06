using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

public sealed class GpuParticles2DTests
{
    private static GpuParticles2D Make(SceneTree tree, float ratio = 1f)
    {
        var p = new GpuParticles2D
        {
            Texture = Texture2D.FromPixels(2, 2, new byte[16]),
            Amount = 100,
            Lifetime = 1f,
            AmountRatio = ratio,
            Emitting = false,
            ProcessMaterial = new ParticleProcessMaterial
            {
                EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
                EmissionBoxExtents = new Vector3(50, 50, 0),
                Direction = Vector3.UnitY,
                Spread = 0,
                InitialVelocityMin = 100,
                InitialVelocityMax = 100,
                Gravity = Vector3.Zero,
            },
        };
        tree.Root.AddChild(p);
        return p;
    }

    private static void Run(SceneTree tree, float seconds)
    {
        for (var t = 0f; t < seconds; t += 1f / 60f)
            tree.Tick(new GameTime { DeltaTime = 1f / 60f });
    }

    [Fact]
    public void EmitsEvenlyOverTheLifetimeWithinTheBoxAndMovesAlongTheDirection()
    {
        var tree = new SceneTree();
        var p = Make(tree);
        p.Emitting = true;
        Run(tree, 0.5f);
        Assert.InRange(p.LiveCount, 45, 55); // half the lifetime: half the particles started
        Run(tree, 1f);
        Assert.Equal(100, p.LiveCount);
        p.Emitting = false;
        Run(tree, 1.1f);
        Assert.Equal(0, p.LiveCount); // no restarts once emission stops
    }

    [Fact]
    public void AmountRatioLimitsTheParticlesAndPreprocessFillsAtOnce()
    {
        var tree = new SceneTree();
        var p = Make(tree, ratio: 0.25f);
        p.Preprocess = 1f;
        p.Emitting = true;
        Assert.InRange(p.LiveCount, 24, 26);
        p.RunDraw();
        Assert.False(p.DrawList.Commands.IsEmpty);
    }

    // One particle with a 60° start angle on a 2×10 texture, drawn through the canvas server; returns the quad's corners and colour.
    private static (Vector2[] Corners, Vector4 Color) DrawOne(bool disableZ)
    {
        var tree = new SceneTree(new ServerRegistry());
        using var server = new CanvasServer(tree, () => new Vector2(1000, 1000));
        var root = new Node2D { Name = "Root" };
        tree.ChangeScene(root);
        root.AddChild(new GpuParticles2D
        {
            Name = "P",
            Texture = Texture2D.FromPixels(2, 10, new byte[80]),
            Amount = 1,
            Lifetime = 10f,
            LocalCoords = true,
            ProcessMaterial = new ParticleProcessMaterial
            {
                Gravity = Vector3.Zero,
                AngleMin = 60,
                AngleMax = 60,
                Color = new Vector4(1f, 0.5f, 0f, 1f),
                ParticleFlagDisableZ = disableZ,
            },
        });
        tree.Tick(new GameTime { DeltaTime = 1f / 60f });
        server.Process(new GameTime { DeltaTime = 1f / 60f });
        var frame = server.Frame;
        var batch = Assert.Single(frame.Batches);
        var corners = Enumerable.Range(0, 6).Select(k => frame.Vertices[(int)frame.Indices[batch.FirstIndex + k]].Position).Distinct().ToArray();
        var color = frame.Vertices[(int)frame.Indices[batch.FirstIndex]].Color;
        tree.Shutdown();
        return (corners, color);
    }

    [Fact]
    public void WithoutDisableZTheAngleOnlyNarrowsTheParticleAndColourIsLinearisedLikeGodot()
    {
        var (corners, color) = DrawOne(disableZ: false);
        var width = corners.Max(c => c.X) - corners.Min(c => c.X);
        var height = corners.Max(c => c.Y) - corners.Min(c => c.Y);
        Assert.Equal(1f, width, 3);    // 2 px × cos 60°
        Assert.Equal(10f, height, 3);  // unrotated
        Assert.Equal(new Vector4(1f, ColorSpace.SrgbToLinear(0.5f), 0f, 1f), color);
    }

    [Fact]
    public void WithDisableZTheAngleTurnsTheParticleWithGodotsSign()
    {
        var (corners, _) = DrawOne(disableZ: true);
        // Godot's basis: X = (cos, −sin), Y = (sin, cos): the texture's down axis (0, 1) → (sin 60°, cos 60°).
        var top = corners.OrderBy(c => c.Y).First();
        var bottom = corners.OrderBy(c => c.Y).Last();
        Assert.True(bottom.X > top.X, $"the bottom end leans right ({top} → {bottom})");
    }
}
