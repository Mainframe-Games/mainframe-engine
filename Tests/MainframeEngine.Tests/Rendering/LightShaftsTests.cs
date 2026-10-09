using System.Numerics;
using MainframeEngine.Serialization;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0160: the light-shaft settings, their blur passes and the sun's screen position.</summary>
public sealed class LightShaftsTests
{
    private static PerspectiveCamera Camera() => new()
    {
        Position = new Vector3(0, 2, 10),
        Forward = -Vector3.UnitZ,
        Up = Vector3.UnitY,
        AspectRatio = 16f / 9f,
    };

    private static LightShaftsSun SunOnScreen(ICamera camera, Vector3 towardsSun) =>
        LightShaftsSun.Compute(camera.ViewMatrix, camera.ProjectionMatrix, towardsSun);

    [Fact]
    public void DefaultsAreOffWithTheProposalsValues()
    {
        var s = PostProcessSettings.Default;
        Assert.False(s.LightShaftsEnabled);
        Assert.Equal(1f, s.LightShaftsIntensity);
        Assert.Equal(0.96f, s.LightShaftsDecay);
        Assert.Equal(0.8f, s.LightShaftsDensity);
        Assert.Equal(16, s.LightShaftsSamples);
        Assert.NotEqual(PostProcessSettings.Default, s with { LightShaftsEnabled = true }); // turns the post pass on

        var env = new WorldEnvironment();
        Assert.False(env.LightShaftsEnabled);
        Assert.Equal(PostProcessSettings.Default, env.PostProcess);
    }

    [Fact]
    public void SamplesAreClampedPerPass()
    {
        Assert.Equal(16, PostProcessSettings.Default.LightShaftsTapsPerPass);
        Assert.Equal(4, (PostProcessSettings.Default with { LightShaftsSamples = 1 }).LightShaftsTapsPerPass);
        Assert.Equal(64, (PostProcessSettings.Default with { LightShaftsSamples = 1000 }).LightShaftsTapsPerPass);
    }

    [Fact]
    public void TheTwoPassesCoverTheDensityWithSamplesSquaredTaps()
    {
        var s = PostProcessSettings.Default with { LightShaftsSamples = 8, LightShaftsDensity = 0.64f, LightShaftsDecay = 0.5f };
        var (fineStep, fineDecay) = s.LightShaftsPass(0);
        var (coarseStep, coarseDecay) = s.LightShaftsPass(1);

        Assert.Equal(0.64f / 64f, fineStep, 1e-6f);
        Assert.Equal(0.64f / 8f, coarseStep, 1e-6f);
        // A coarse tap spans the fine pass: its decay is that of n fine taps.
        Assert.Equal(coarseDecay, MathF.Pow(fineDecay, 8), 1e-5f);
        // The weight left after a sixteenth of the ray is LightShaftsDecay, whatever the sample count.
        Assert.Equal(0.5f, MathF.Pow(coarseDecay, 8f / 16f), 1e-5f);
        Assert.Equal(0.5f, MathF.Pow((s with { LightShaftsSamples = 32 }).LightShaftsPass(0).TapDecay, 32f * 32f / 16f), 1e-4f);
    }

    [Fact]
    public void OutOfRangeDensityAndDecayAreClamped()
    {
        var s = PostProcessSettings.Default with { LightShaftsDensity = 3f, LightShaftsDecay = -1f };
        Assert.Equal(1f / 16f, s.LightShaftsPass(1).Step, 1e-6f);
        Assert.Equal(0f, s.LightShaftsPass(1).TapDecay);
    }

    [Fact]
    public void ASunStraightAheadIsAtTheScreenCentre()
    {
        var sun = SunOnScreen(Camera(), -Vector3.UnitZ);
        Assert.Equal(1f, sun.Fade);
        Assert.True(Vector2.Distance(new Vector2(0.5f, 0.5f), sun.ScreenUv) < 1e-4f, sun.ScreenUv.ToString());
    }

    [Fact]
    public void AHighSunIsNearTheTopAndARightSunToTheRight()
    {
        var high = SunOnScreen(Camera(), Vector3.Normalize(new Vector3(0, 0.3f, -1)));
        Assert.True(high.ScreenUv.Y < 0.5f && MathF.Abs(high.ScreenUv.X - 0.5f) < 1e-4f, high.ScreenUv.ToString()); // v grows downwards
        var right = SunOnScreen(Camera(), Vector3.Normalize(new Vector3(0.3f, 0, -1)));
        Assert.True(right.ScreenUv.X > 0.5f && MathF.Abs(right.ScreenUv.Y - 0.5f) < 1e-4f, right.ScreenUv.ToString());
        Assert.Equal(1f, right.Fade);
    }

    [Fact]
    public void TheShaftsFadeAsTheSunLeavesTheScreenAndVanishBehindTheCamera()
    {
        var camera = Camera();
        Assert.Equal(default, SunOnScreen(camera, Vector3.UnitZ)); // behind
        Assert.Equal(default, SunOnScreen(camera, Vector3.UnitX)); // at 90°
        Assert.Equal(default, SunOnScreen(camera, Vector3.Zero));

        // Sweep the sun to the right: full on screen, fading within the margin past the edge, then gone.
        var previous = 1f;
        var sawPartial = false;
        for (var degrees = 0f; degrees < 89f; degrees += 1f)
        {
            var a = float.DegreesToRadians(degrees);
            var sun = SunOnScreen(camera, new Vector3(MathF.Sin(a), 0, -MathF.Cos(a)));
            Assert.True(sun.Fade <= previous + 1e-6f, $"fade rose at {degrees}°");
            var outside = sun.ScreenUv.X - 1f;
            if (sun.Fade > 0f && sun.Fade < 1f)
            {
                sawPartial = true;
                Assert.InRange(outside, 0f, LightShaftsSun.OffscreenMargin);
            }

            if (sun.Fade >= 1f)
                Assert.True(sun.ScreenUv.X <= 1f, $"full fade off screen at {degrees}°");
            previous = sun.Fade;
        }

        Assert.True(sawPartial);
        Assert.Equal(0f, previous);
    }

    [Fact]
    public void AnOrthographicCameraNeverSeesTheSun()
    {
        var ortho = new OrthographicCamera { Position = new Vector3(0, 2, 10), Forward = -Vector3.UnitZ, Up = Vector3.UnitY };
        Assert.Equal(0f, SunOnScreen(ortho, -Vector3.UnitZ).Fade);
    }

    [Fact]
    public void TheShaftTargetsAreHalfTheScene()
    {
        var size = LightShafts.TargetExtent(new Silk.NET.Vulkan.Extent2D(2560, 1441));
        Assert.Equal((1280u, 720u), (size.Width, size.Height));
        size = LightShafts.TargetExtent(new Silk.NET.Vulkan.Extent2D(1, 1));
        Assert.Equal((1u, 1u), (size.Width, size.Height));
    }

    [Fact]
    public void WorldEnvironmentExportsRoundTripThroughScenes()
    {
        var root = new Node3D { Name = "Root" };
        var env = new WorldEnvironment
        {
            Name = "Environment",
            LightShaftsEnabled = true,
            LightShaftsIntensity = 2.5f,
            LightShaftsDecay = 0.9f,
            LightShaftsDensity = 0.6f,
            LightShaftsSamples = 24,
        };
        root.AddChild(env);
        env.Owner = root;

        var copy = (WorldEnvironment)PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetChild(0);
        Assert.Equal(env.PostProcess, copy.PostProcess);
        Assert.True(copy.PostProcess.LightShaftsEnabled);
        Assert.Equal(2.5f, copy.LightShaftsIntensity);
        Assert.Equal(0.9f, copy.LightShaftsDecay);
        Assert.Equal(0.6f, copy.LightShaftsDensity);
        Assert.Equal(24, copy.LightShaftsSamples);
    }
}
