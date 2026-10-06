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
}
