using System.Numerics;
using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

/// <summary>ADR 0163: post-effect stages and order, the target pool and its histories, the jitter, the temporal frame data.</summary>
public sealed class PostProcessingTests
{
    private sealed class FakeEffect(string name, PostStage stage, int order, PostEffectNeeds needs = PostEffectNeeds.None,
        Func<PostEffectSettings, bool>? enabled = null, List<string>? log = null) : PostEffect(name, stage, order)
    {
        public override PostEffectNeeds Needs => needs;

        public override bool IsEnabled(in PostEffectSettings settings) => enabled?.Invoke(settings) ?? true;

        public int Created, BeganFrames, Recorded, Disposed;
        public bool WasLast;

        protected override void OnCreate(PostEffectContext context) => Created++;

        protected override void OnBeginFrame(PostEffectContext context) => BeganFrames++;

        protected override void OnRecord(PostEffectContext context)
        {
            Recorded++;
            WasLast = context.IsLastInStage;
            log?.Add(Name);
        }

        protected override void OnDispose() => Disposed++;
    }

    private static PostEffectContext Context(PostEffectSettings settings = default) => new() { Settings = settings };

    [Fact]
    public void EffectsRunByStageThenOrderThenRegistration()
    {
        var log = new List<string>();
        var stack = new PostProcessStack();
        stack.Add(new FakeEffect("fxaa", PostStage.AfterTonemap, PostEffectOrder.Fxaa, log: log));
        stack.Add(new FakeEffect("glow", PostStage.BeforeTonemap, PostEffectOrder.Glow, log: log));
        stack.Add(new FakeEffect("taa", PostStage.BeforeTonemap, PostEffectOrder.Taa, log: log));
        stack.Add(new FakeEffect("ssao", PostStage.AfterPrepass, PostEffectOrder.Ssao, log: log));
        stack.Add(new FakeEffect("glow 2", PostStage.BeforeTonemap, PostEffectOrder.Glow, log: log));

        Assert.Equal(["ssao", "taa", "glow", "glow 2", "fxaa"], stack.Effects.Select(e => e.Name));

        var context = Context();
        foreach (var stage in new[] { PostStage.AfterPrepass, PostStage.BeforeTonemap, PostStage.AfterTonemap })
            stack.Record(stage, context);
        Assert.Equal(["ssao", "taa", "glow", "glow 2", "fxaa"], log);
    }

    [Fact]
    public void OnlyEnabledEffectsRunAndTheLastOneKnowsIt()
    {
        var on = new FakeEffect("on", PostStage.AfterTonemap, 10);
        var off = new FakeEffect("off", PostStage.AfterTonemap, 20, enabled: _ => false);
        var last = new FakeEffect("last", PostStage.AfterTonemap, 30);
        var stack = new PostProcessStack();
        stack.Add(last);
        stack.Add(off);
        stack.Add(on);

        Assert.Equal(2, stack.Record(PostStage.AfterTonemap, Context()));
        Assert.Equal((1, 0, 1), (on.Recorded, off.Recorded, last.Recorded));
        Assert.False(on.WasLast);
        Assert.True(last.WasLast);
        Assert.Equal(0, stack.Record(PostStage.BeforeTonemap, Context()));
    }

    [Fact]
    public void EffectsAreCreatedOnTheirFirstEnabledFrameAndDisposedOnlyIfCreated()
    {
        var enabled = false;
        var lazy = new FakeEffect("lazy", PostStage.BeforeTonemap, 0, enabled: _ => enabled);
        var never = new FakeEffect("never", PostStage.BeforeTonemap, 1, enabled: _ => false);
        var stack = new PostProcessStack();
        stack.Add(lazy);
        stack.Add(never);

        stack.BeginFrame(Context());
        Assert.Equal((0, 0), (lazy.Created, lazy.BeganFrames));
        enabled = true;
        stack.BeginFrame(Context());
        stack.Record(PostStage.BeforeTonemap, Context());
        stack.BeginFrame(Context());
        Assert.Equal((1, 2, 1), (lazy.Created, lazy.BeganFrames, lazy.Recorded));
        Assert.True(lazy.IsCreated);
        Assert.False(never.IsCreated);

        stack.Dispose();
        Assert.Equal((1, 0), (lazy.Disposed, never.Disposed));
    }

    [Fact]
    public void NeedsAreTheUnionOfTheEnabledEffects()
    {
        var stack = new PostProcessStack();
        stack.Add(new FakeEffect("ssao", PostStage.AfterPrepass, 0, PostEffectNeeds.DepthPrepass, s => s.World.GlowEnabled));
        stack.Add(new FakeEffect("taa", PostStage.BeforeTonemap, 0, PostEffectNeeds.Velocity | PostEffectNeeds.Jitter,
            s => s.AntiAliasing == AntiAliasing.Fxaa));

        Assert.Equal(PostEffectNeeds.None, stack.GetNeeds(default));
        Assert.Equal(PostEffectNeeds.DepthPrepass, stack.GetNeeds(new PostEffectSettings(new PostProcessSettings { GlowEnabled = true }, default, default)));
        var both = stack.GetNeeds(new PostEffectSettings(new PostProcessSettings { GlowEnabled = true }, AntiAliasing.Fxaa, default));
        Assert.Equal(PostEffectNeeds.DepthPrepass | PostEffectNeeds.Velocity | PostEffectNeeds.Jitter, both);
        Assert.True((PostEffectNeeds.Velocity & PostEffectNeeds.DepthPrepass) != 0); // velocity comes from the prepass
    }

    [Fact]
    public void AStackRejectsTheSameEffectTwiceAndCanRemoveOne()
    {
        var effect = new FakeEffect("once", PostStage.BeforeTonemap, 0);
        var stack = new PostProcessStack();
        stack.Add(effect);
        Assert.Throws<InvalidOperationException>(() => stack.Add(effect));
        Assert.Same(effect, stack.Find<FakeEffect>());
        Assert.True(stack.Remove(effect));
        Assert.Empty(stack.Effects);
    }

    [Fact]
    public void BuiltInEffectsKeepTheirStagesAndEnableRules()
    {
        var autoExposure = new AutoExposure();
        var glow = new GlowEffect(autoExposure);
        var shafts = new LightShafts();
        var fxaa = new FxaaEffect();
        var velocity = new VelocityDebugView();
        var stack = new PostProcessStack();
        foreach (PostEffect effect in new PostEffect[] { velocity, fxaa, shafts, glow, autoExposure })
            stack.Add(effect);
        Assert.Equal(new PostEffect[] { autoExposure, glow, shafts, fxaa, velocity }, stack.Effects);

        // The default settings keep the engine's tonemap: no post effect runs (the goldens before ADR 0163).
        Assert.False(PostEffectSettings.Default.PostTonemap);
        Assert.Equal(0, stack.CountEnabled(PostStage.BeforeTonemap, PostEffectSettings.Default));
        Assert.Equal(0, stack.CountEnabled(PostStage.AfterTonemap, PostEffectSettings.Default));
        Assert.Equal(PostEffectNeeds.None, stack.GetNeeds(PostEffectSettings.Default));

        var shaftsOn = new PostEffectSettings(new PostProcessSettings { LightShaftsEnabled = true }, AntiAliasing.None, RenderDebugView.None);
        Assert.True(shaftsOn.PostTonemap);
        Assert.Equal(3, stack.CountEnabled(PostStage.BeforeTonemap, shaftsOn));
        var glowOnly = new PostEffectSettings(new PostProcessSettings { GlowEnabled = true }, AntiAliasing.None, RenderDebugView.None);
        Assert.Equal(2, stack.CountEnabled(PostStage.BeforeTonemap, glowOnly)); // auto exposure binds its image anyway

        Assert.True(fxaa.IsEnabled(PostEffectSettings.Default with { AntiAliasing = AntiAliasing.Fxaa }));
        var debug = PostEffectSettings.Default with { DebugView = RenderDebugView.Velocity };
        Assert.True(velocity.IsEnabled(debug));
        Assert.Equal(PostEffectNeeds.Velocity, stack.GetNeeds(debug));
    }

    // ── Target pool ─────────────────────────────────────────────────────────────

    private sealed class FakeTarget(PostTargetDesc desc, Extent2D extent) : IPostTarget
    {
        public PostTargetDesc Desc { get; } = desc;
        public Extent2D Extent { get; private set; } = extent;
        public int Resizes, Disposes;

        public bool Resize(Extent2D extent)
        {
            if (extent.Width == Extent.Width && extent.Height == Extent.Height)
                return false;
            Extent = extent;
            Resizes++;
            return true;
        }

        public void Dispose() => Disposes++;
    }

    private static PostTargetPool<FakeTarget> Pool(List<FakeTarget> created, uint width = 1920, uint height = 1080) =>
        new((desc, extent) =>
        {
            var target = new FakeTarget(desc, extent);
            created.Add(target);
            return target;
        }, new Extent2D(width, height));

    [Fact]
    public void ATargetIsCreatedOnceAtItsScaleAndFollowsTheScene()
    {
        var created = new List<FakeTarget>();
        var pool = Pool(created, 1921, 1081);
        var half = new PostTargetDesc("ssao", Format.R8Unorm, PostTargetScale.Half);
        var target = pool.Get(half);
        Assert.Same(target, pool.Get(half));
        Assert.Single(created);
        Assert.Equal((960u, 540u), (target.Extent.Width, target.Extent.Height));
        Assert.Equal((480u, 270u), Size(new PostTargetDesc("q", Format.R8Unorm, PostTargetScale.Quarter).ExtentFor(new Extent2D(1921, 1081))));
        Assert.Equal((1u, 1u), Size(new PostTargetDesc("tiny", Format.R8Unorm, PostTargetScale.Quarter).ExtentFor(new Extent2D(2, 3))));
        Assert.Throws<InvalidOperationException>(() => pool.Get(half with { Format = Format.R16Sfloat }));

        var generation = pool.Generation;
        pool.Resize(new Extent2D(1921, 1081)); // unchanged: nothing happens
        Assert.Equal(generation, pool.Generation);
        pool.Resize(new Extent2D(1280, 720));
        Assert.Equal(generation + 1, pool.Generation);
        Assert.Equal((640u, 360u), (target.Extent.Width, target.Extent.Height));

        Assert.True(pool.Release("ssao"));
        Assert.Equal(1, target.Disposes);
        Assert.False(pool.Release("ssao"));
    }

    [Fact]
    public void AHistoryPingPongsAndKnowsWhenItIsValid()
    {
        var created = new List<FakeTarget>();
        var pool = Pool(created);
        var history = pool.GetHistory(new PostTargetDesc("taa", Format.R16G16B16A16Sfloat));
        Assert.Same(history, pool.GetHistory(new PostTargetDesc("taa", Format.R16G16B16A16Sfloat)));
        Assert.Equal(["taa/0", "taa/1"], created.Select(t => t.Desc.Name));
        Assert.Equal(2, pool.Count);
        Assert.Throws<InvalidOperationException>(() => pool.Get(new PostTargetDesc("taa", Format.R16G16B16A16Sfloat)));

        history.Advance(10);
        Assert.False(history.IsValid); // the first frame has no history
        var first = history.Current;
        history.MarkWritten();
        history.Advance(10); // repeated in the same frame: no swap
        Assert.Same(first, history.Current);

        history.Advance(11);
        Assert.True(history.IsValid);
        Assert.Same(first, history.Previous);
        Assert.NotSame(first, history.Current);
        // Frame 11 writes nothing: frame 12 has no history.
        history.Advance(12);
        Assert.False(history.IsValid);
        history.MarkWritten();

        history.Advance(14); // a skipped frame
        Assert.False(history.IsValid);
        history.MarkWritten();
        history.Advance(15);
        Assert.True(history.IsValid);

        pool.Resize(new Extent2D(800, 600)); // a resize invalidates it
        history.Advance(16);
        Assert.False(history.IsValid);
        Assert.All(created, t => Assert.Equal(1, t.Resizes));

        history.MarkWritten();
        history.Advance(17);
        history.Reset(); // a camera cut
        Assert.False(history.IsValid);

        pool.Dispose();
        Assert.All(created, t => Assert.Equal(1, t.Disposes));
    }

    private static (uint, uint) Size(Extent2D e) => (e.Width, e.Height);

    // ── Jitter ──────────────────────────────────────────────────────────────────

    [Fact]
    public void HaltonGivesTheRadicalInverse()
    {
        Assert.Equal(0.5f, TemporalJitter.Halton(1, 2));
        Assert.Equal(0.25f, TemporalJitter.Halton(2, 2));
        Assert.Equal(0.75f, TemporalJitter.Halton(3, 2));
        Assert.Equal(0.125f, TemporalJitter.Halton(4, 2));
        Assert.Equal(1f / 3f, TemporalJitter.Halton(1, 3), 6);
        Assert.Equal(2f / 3f, TemporalJitter.Halton(2, 3), 6);
        Assert.Equal(1f / 9f, TemporalJitter.Halton(3, 3), 6);
        Assert.Equal(0f, TemporalJitter.Halton(0, 2));
    }

    [Fact]
    public void JitterSamplesStayInsideThePixelAndCycle()
    {
        var sum = Vector2.Zero;
        for (var i = 0; i < TemporalJitter.DefaultSampleCount; i++)
        {
            var p = TemporalJitter.PixelOffset(i);
            Assert.InRange(p.X, -0.5f, 0.5f);
            Assert.InRange(p.Y, -0.5f, 0.5f);
            sum += p;
        }

        Assert.True(sum.Length() / TemporalJitter.DefaultSampleCount < 0.1f, $"the samples are biased: mean {sum / 8}");
        Assert.Equal(TemporalJitter.SampleIndex(3), TemporalJitter.SampleIndex(11));
        Assert.Equal(0, TemporalJitter.SampleIndex(8));

        // A pixel is 2 / size in NDC, and image +y is NDC −y.
        var ndc = TemporalJitter.NdcOffset(2, 1000, 500);
        var pixel = TemporalJitter.PixelOffset(2);
        Assert.Equal(2f * pixel.X / 1000f, ndc.X, 6);
        Assert.Equal(-2f * pixel.Y / 500f, ndc.Y, 6);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AJitteredProjectionShiftsEveryPointByTheJitter(bool orthographic)
    {
        var projection = orthographic
            ? Matrix4x4.CreateOrthographic(8f, 6f, 0.1f, 100f)
            : Matrix4x4.CreatePerspectiveFieldOfView(1f, 16f / 9f, 0.1f, 500f);
        var jitter = new Vector2(0.0013f, -0.0021f);
        var jittered = TemporalJitter.Apply(projection, jitter);
        Assert.Equal(projection, TemporalJitter.Apply(projection, Vector2.Zero));
        foreach (var p in new[] { new Vector3(0.3f, -0.2f, -1f), new Vector3(-4f, 2f, -40f), new Vector3(1f, 1f, -300f) })
        {
            var a = Vector4.Transform(new Vector4(p, 1f), projection);
            var b = Vector4.Transform(new Vector4(p, 1f), jittered);
            Assert.Equal(a.W, b.W, 5);
            Assert.Equal(a.Z, b.Z, 5);
            Assert.Equal(a.X / a.W + jitter.X, b.X / b.W, 5);
            Assert.Equal(a.Y / a.W + jitter.Y, b.Y / b.W, 5);
        }
    }

    // ── Frame data ──────────────────────────────────────────────────────────────

    [Fact]
    public void TheFrameBlockGrewByTheTemporalFields()
    {
        Assert.Equal(688, FrameData.Size); // + AmbientOcclusion (ADR 0165), the probe volume (ADR 0170), the mip bias (ADR 0174)
        Assert.Equal(FrameData.Size, System.Runtime.InteropServices.Marshal.SizeOf<FrameData>());
        Assert.Equal(432, (int)System.Runtime.InteropServices.Marshal.OffsetOf<FrameData>(nameof(FrameData.PreviousViewProjection)));
        Assert.Equal(496, (int)System.Runtime.InteropServices.Marshal.OffsetOf<FrameData>(nameof(FrameData.Jitter)));
        Assert.Equal(512, (int)System.Runtime.InteropServices.Marshal.OffsetOf<FrameData>(nameof(FrameData.Temporal)));
        Assert.Equal(528, (int)System.Runtime.InteropServices.Marshal.OffsetOf<FrameData>(nameof(FrameData.PreviousWind)));
        Assert.Equal(560, (int)System.Runtime.InteropServices.Marshal.OffsetOf<FrameData>(nameof(FrameData.AmbientOcclusion)));
    }

    [Fact]
    public void FrameDataCarriesTheJitterAndLastFramesMatrices()
    {
        var view = Matrix4x4.CreateLookAt(new Vector3(0, 2, 5), Vector3.Zero, Vector3.UnitY);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(1f, 1.5f, 0.1f, 100f);
        var still = FrameData.From(view, projection, new Vector3(0, 2, 5), new Extent2D(300, 200), 3f, 1f);
        Assert.Equal(still.ViewProjection, still.PreviousViewProjection); // no history: no motion
        Assert.Equal(new Vector4(3f, 0f, 0f, 0f), still.Temporal);

        var previous = Matrix4x4.CreateLookAt(new Vector3(0.1f, 2, 5), Vector3.Zero, Vector3.UnitY) * projection;
        var wind = new FrameEnvironment(new Vector4(1, 0, 0, 2), new Vector4(0.5f, 0, 1, 0), default, default);
        var temporal = new FrameTemporal(previous, new Vector2(0.01f, -0.02f), new Vector2(0.03f, 0.04f), 2.9f, true, 5, wind);
        var data = FrameData.From(view, projection, new Vector3(0, 2, 5), new Extent2D(300, 200), 3f, 1f, default, temporal);
        Assert.Equal(TemporalJitter.Apply(projection, new Vector2(0.01f, -0.02f)), data.Projection);
        Assert.Equal(view * data.Projection, data.ViewProjection);
        Assert.Equal(previous, data.PreviousViewProjection);
        Assert.Equal(new Vector4(0.01f, -0.02f, 0.03f, 0.04f), data.Jitter);
        Assert.Equal(new Vector4(2.9f, 1f, 5f, 0f), data.Temporal);
        Assert.Equal(wind.Wind, data.PreviousWind);
        Assert.Equal(wind.WindParams, data.PreviousWindParams);
        Assert.Equal(FrameData.ClipPlanes(projection), (data.Clip.X, data.Clip.Y)); // the jitter leaves depth alone
    }

    [Fact]
    public void AViewsHistoryKeepsLastFrameAcrossRewritesInAFrame()
    {
        var history = new ViewHistory();
        var a = Matrix4x4.CreateTranslation(1, 0, 0);
        var b = Matrix4x4.CreateTranslation(2, 0, 0);
        var c = Matrix4x4.CreateTranslation(3, 0, 0);

        history.Record(1, a, new Vector2(0.1f), 1f, default);
        Assert.False(history.HistoryValid);
        Assert.Equal(a, history.PreviousViewProjection); // the first frame repeats itself

        history.Record(2, b, new Vector2(0.2f), 2f, default);
        history.Record(2, b, new Vector2(0.2f), 2f, default); // the picking pass, then the main pass
        Assert.True(history.HistoryValid);
        Assert.Equal(a, history.PreviousViewProjection);
        Assert.Equal(new Vector2(0.1f), history.PreviousJitter);
        Assert.Equal(1f, history.PreviousTime);
        var temporal = history.ToTemporal(3);
        Assert.Equal((a, new Vector2(0.2f), 3), (temporal.PreviousViewProjection, temporal.Jitter, temporal.JitterIndex));

        history.Record(4, c, default, 4f, default); // a frame without this view
        Assert.False(history.HistoryValid);
        Assert.Equal(c, history.PreviousViewProjection);
    }

    [Fact]
    public void ANodesMotionHistoryGivesLastFramesModel()
    {
        var motion = new MotionHistory();
        var a = Matrix4x4.CreateTranslation(1, 0, 0);
        var b = Matrix4x4.CreateTranslation(2, 0, 0);
        Assert.Equal(a, motion.Previous(a, 7)); // first frame: no motion
        Assert.Equal(a, motion.Previous(b, 8));
        Assert.Equal(a, motion.Previous(b, 8)); // another surface of the node, same frame
        Assert.Equal(a, motion.Previous(a, 10)); // not drawn on frame 9: no motion
    }
}
