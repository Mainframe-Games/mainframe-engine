using System.Numerics;
using MainframeEngine.Serialization;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0165: the SSAO settings, the effect's stage, needs and enable rule, and its push-block packing.</summary>
public sealed class SsaoTests
{
    [Fact]
    public void DefaultsAreOffWithGodotsValues()
    {
        var s = PostProcessSettings.Default;
        Assert.False(s.SsaoEnabled);
        Assert.Equal(1f, s.SsaoRadius);
        Assert.Equal(2f, s.SsaoIntensity);
        Assert.Equal(1.5f, s.SsaoPower);
        Assert.Equal(0.5f, s.SsaoDetail);
        Assert.Equal(0.06f, s.SsaoHorizon);
        Assert.Equal(0.98f, s.SsaoSharpness);
        Assert.Equal(0f, s.SsaoLightAffect);
        Assert.Equal(0f, s.SsaoAoChannelAffect);

        var env = new WorldEnvironment();
        Assert.False(env.SsaoEnabled);
        Assert.Equal(PostProcessSettings.Default, env.PostProcess);
    }

    [Fact]
    public void SsaoDoesNotTurnThePostTonemapOn()
    {
        var ssao = new PostProcessSettings { SsaoEnabled = true, SsaoRadius = 0.5f, SsaoIntensity = 1f, SsaoLightAffect = 0.3f };
        Assert.Equal(PostProcessSettings.Default, ssao.WithoutSsao());
        Assert.False(new PostEffectSettings(ssao, AntiAliasing.None, RenderDebugView.None).PostTonemap);
        var glow = ssao with { GlowEnabled = true };
        Assert.True(new PostEffectSettings(glow, AntiAliasing.None, RenderDebugView.None).PostTonemap);
    }

    [Fact]
    public void TheEffectRunsAfterThePrepassWhenEnabled()
    {
        var ssao = new SsaoEffect();
        Assert.Equal(PostStage.AfterPrepass, ssao.Stage);
        Assert.Equal(PostEffectOrder.Ssao, ssao.Order);
        Assert.Equal(PostEffectNeeds.DepthPrepass, ssao.Needs);
        Assert.False(ssao.IsEnabled(PostEffectSettings.Default));

        var on = PostEffectSettings.Default with { World = new PostProcessSettings { SsaoEnabled = true } };
        Assert.True(ssao.IsEnabled(on));
        var stack = new PostProcessStack();
        stack.Add(ssao);
        stack.Add(new FxaaEffect());
        Assert.Equal(PostEffectNeeds.DepthPrepass, stack.GetNeeds(on));
        Assert.Equal(PostEffectNeeds.None, stack.GetNeeds(PostEffectSettings.Default));
        Assert.Equal(1, stack.CountEnabled(PostStage.AfterPrepass, on));
        Assert.Equal(0, stack.CountEnabled(PostStage.BeforeTonemap, on)); // no tonemap effects come with it
    }

    [Fact]
    public void ThePushBlockPacksTheProjectionAndTheSettings()
    {
        Assert.Equal(96, System.Runtime.InteropServices.Marshal.SizeOf<SsaoEffect.GtaoPush>());
        var projection = TemporalJitter.Apply(Matrix4x4.CreatePerspectiveFieldOfView(1.2f, 16f / 9f, 0.05f, 500f), new Vector2(0.001f, -0.002f));
        var settings = new PostProcessSettings
        {
            SsaoEnabled = true,
            SsaoRadius = 0.7f,
            SsaoIntensity = 1.25f,
            SsaoPower = 0f, // clamped
            SsaoDetail = 9f, // clamped to 5
            SsaoHorizon = 0.5f,
            SsaoSharpness = 0.98f,
        };
        var push = SsaoEffect.Parameters(settings, projection, new Extent2D(1920, 1080), 3);

        Assert.Equal(new Vector4(projection.M11, projection.M22, projection.M31, projection.M32), push.Projection);
        Assert.Equal(new Vector4(projection.M41, projection.M42, projection.M33, projection.M43), push.Projection2);
        Assert.Equal(new Vector4(projection.M34, projection.M44, 1920f, 1080f), push.Projection3);
        Assert.Equal(0.7f, push.Shape.X);
        Assert.Equal(SsaoEffect.MaxRadiusFraction * 1080f, push.Shape.Y);
        Assert.Equal(MathF.PI / 4f, push.Shape.Z, 1e-6f);
        Assert.Equal(5f, push.Shape.W);
        Assert.Equal(1.25f, push.Filter.X);
        Assert.Equal(0.01f, push.Filter.Y);
        Assert.Equal(0.02f, push.Filter.Z, 1e-6f);
        Assert.Equal(0.06f, push.Filter.W, 1e-6f);
        Assert.Equal(new Vector4(3f, SsaoEffect.Slices, SsaoEffect.StepsPerSide, -1f), push.Noise);
    }

    [Fact]
    public void TheShaderUnprojectionMatchesTheProjection()
    {
        // gtao.slang's viewZ/viewPosition from the packed coefficients, on the CPU: a view point projected with the
        // (jittered) matrix and unprojected from its pixel and depth comes back.
        var projection = TemporalJitter.Apply(Matrix4x4.CreatePerspectiveFieldOfView(1.1f, 4f / 3f, 0.1f, 300f), new Vector2(0.003f, 0.001f));
        var push = SsaoEffect.Parameters(PostProcessSettings.Default, projection, new Extent2D(640, 480), 0);
        var point = new Vector3(1.3f, -0.7f, -6.5f);
        var clip = Vector4.Transform(new Vector4(point, 1f), projection);
        var depth = clip.Z / clip.W;
        var ndc = new Vector2(clip.X, clip.Y) / clip.W;

        var z = (push.Projection2.W - depth * push.Projection3.Y) / (depth * push.Projection3.X - push.Projection2.Z);
        var w = z * push.Projection3.X + push.Projection3.Y;
        var x = (ndc.X * w - z * push.Projection.Z - push.Projection2.X) / push.Projection.X;
        var y = (ndc.Y * w - z * push.Projection.W - push.Projection2.Y) / push.Projection.Y;
        Assert.Equal(point.X, x, 1e-3f);
        Assert.Equal(point.Y, y, 1e-3f);
        Assert.Equal(point.Z, z, 1e-3f);
    }

    [Fact]
    public void SharperMeansTighterDepthTolerances()
    {
        Assert.True(SsaoEffect.DenoiseTolerance(1f) < SsaoEffect.DenoiseTolerance(0.5f));
        Assert.True(SsaoEffect.UpsampleTolerance(1f) < SsaoEffect.UpsampleTolerance(0f));
        Assert.Equal(SsaoEffect.DenoiseTolerance(1f), SsaoEffect.DenoiseTolerance(3f)); // clamped
    }

    [Fact]
    public void TheFrameBlockCarriesTheSsaoParametersOnlyWhileBound()
    {
        Assert.Equal(560, (int)System.Runtime.InteropServices.Marshal.OffsetOf<FrameData>(nameof(FrameData.AmbientOcclusion)));
        var still = FrameData.From(Matrix4x4.Identity, Matrix4x4.CreatePerspectiveFieldOfView(1f, 1f, 0.1f, 10f), Vector3.Zero, new Extent2D(8, 8), 0f, 1f);
        Assert.Equal(Vector4.Zero, still.AmbientOcclusion); // without SSAO: the shader terms are exactly 1
    }

    [Fact]
    public void WorldEnvironmentExportsRoundTripThroughScenes()
    {
        var root = new Node3D { Name = "Root" };
        var env = new WorldEnvironment
        {
            Name = "Environment",
            SsaoEnabled = true,
            SsaoRadius = 0.6f,
            SsaoIntensity = 1.2f,
            SsaoPower = 1.1f,
            SsaoDetail = 0.3f,
            SsaoHorizon = 0.1f,
            SsaoSharpness = 0.9f,
            SsaoLightAffect = 0.2f,
            SsaoAoChannelAffect = 0.5f,
        };
        root.AddChild(env);
        env.Owner = root;

        var copy = (WorldEnvironment)PackedScene.Parse(SceneSaver.ToJson(root)).Instantiate().GetChild(0);
        Assert.Equal(env.PostProcess, copy.PostProcess);
        Assert.True(copy.SsaoEnabled);
        Assert.Equal(0.6f, copy.SsaoRadius);
        Assert.Equal(1.2f, copy.SsaoIntensity);
        Assert.Equal(1.1f, copy.SsaoPower);
        Assert.Equal(0.3f, copy.SsaoDetail);
        Assert.Equal(0.1f, copy.SsaoHorizon);
        Assert.Equal(0.9f, copy.SsaoSharpness);
        Assert.Equal(0.2f, copy.SsaoLightAffect);
        Assert.Equal(0.5f, copy.SsaoAoChannelAffect);
    }
}
