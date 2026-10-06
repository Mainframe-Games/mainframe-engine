using System.Diagnostics.CodeAnalysis;
using Silk.NET.Maths;
using Spine;

namespace MainframeEngine.Tests.Nodes;

/// <summary>SpineNode logic without a GPU: a non-Vulkan renderer skips every Vulkan resource.</summary>
public sealed class SpineNodeTests
{
    private static readonly string SpineBoy = Path.Combine(AppContext.BaseDirectory, "Content", "Models", "Spine", "SpineBoy");

    private static SpineNode CreateNode()
    {
        return new SpineNode(new HeadlessRenderer(), new SpineFolder(SpineBoy));
    }

    private static GameTime Frame(float dt) => new() { DeltaTime = dt, FrameCount = 1 };

    [Fact]
    public void SpineScaleLeavesTheSkeletonAtUnitScaleAndKeepsFlip()
    {
        using var node = CreateNode();
        Assert.Equal(1f, node.Skeleton.ScaleX);

        node.SpineScale = 0.5f;
        Assert.Equal(1f, node.Skeleton.ScaleX);
        Assert.Equal(1f, node.Skeleton.ScaleY);

        node.FlipX(true);
        node.SpineScale = 0.25f;
        Assert.Equal(-1f, node.Skeleton.ScaleX);
        Assert.Equal(1f, node.Skeleton.ScaleY);

        node.FlipX(false);
        Assert.Equal(1f, node.Skeleton.ScaleX);

        node.SpineScale = -0.25f;
        Assert.Equal(-1f, node.Skeleton.ScaleY);
    }

    [Fact]
    public void SpineScaleScalesTheDrawnVertices()
    {
        using var half = CreateNode();
        half.SpineScale = 0.5f;
        using var quarter = CreateNode();
        quarter.SpineScale = 0.25f;
        half.Advance(Frame(1f / 60f));
        quarter.Advance(Frame(1f / 60f));

        for (var i = 0; i < half.SpineRenderer!.PreparedVertexCount; i += 17)
        {
            var a = half.SpineRenderer.PreparedWorldPosition(i);
            var b = quarter.SpineRenderer!.PreparedWorldPosition(i);
            Assert.Equal(a.X, b.X * 2f, 2);
            Assert.Equal(a.Y, b.Y * 2f, 2);
            Assert.Equal(a.Z, b.Z, 4); // ZSpacing is in node units, not scaled
        }
    }

    [Theory]
    [InlineData(0.005f)]
    [InlineData(0.0045f)]
    [InlineData(0.02f)]
    public void PoseDoesNotDependOnSpineScale(float scale)
    {
        // Spine's IK solver has absolute epsilons: a skeleton scaled to tiny world units solved a different pose.
        using var reference = CreateNode();
        reference.SpineScale = 1f;
        using var scaled = CreateNode();
        scaled.SpineScale = scale;
        foreach (var node in new[] { reference, scaled })
        {
            node.SetAnimation("walk");
            node.Advance(Frame(0.3f));
        }

        var count = reference.SpineRenderer!.PreparedVertexCount;
        Assert.Equal(count, scaled.SpineRenderer!.PreparedVertexCount);
        for (var i = 0; i < count; i++)
        {
            var expected = reference.SpineRenderer.PreparedWorldPosition(i);
            var actual = scaled.SpineRenderer.PreparedWorldPosition(i) / scale;
            // SpineBoy is ~500 skeleton units tall: half a unit is far below a pixel at any sensible size.
            Assert.True(Math.Abs(expected.X - actual.X) < 0.5f && Math.Abs(expected.Y - actual.Y) < 0.5f,
                $"Vertex {i}: {actual.X},{actual.Y} at SpineScale {scale} vs {expected.X},{expected.Y} at 1.");
        }
    }

    [Fact]
    public void SetAnimationReplacesTheCurrentAnimationImmediately()
    {
        using var node = CreateNode();

        node.SetAnimation("walk");
        Assert.Equal("walk", node.CurrentAnimation?.Name);

        node.SetAnimation("run");
        node.Advance(Frame(1f / 60f));
        Assert.Equal("run", node.CurrentAnimation?.Name);
    }

    [Fact]
    public void QueueAnimationKeepsTheCurrentOneUntilItEnds()
    {
        using var node = CreateNode();
        node.SetAnimation("walk");

        node.QueueAnimation("run");
        node.Advance(Frame(1f / 60f));

        Assert.Equal("walk", node.CurrentAnimation?.Name);
    }

    [Fact]
    public void UpdateDrawsThisFramesPoseNotLastFrames()
    {
        using var node = CreateNode();
        node.SetAnimation("walk");
        node.Advance(Frame(0.3f));

        // Reference: Spine's documented order on an identical skeleton.
        var reference = new Skeleton(node.Skeleton.Data) { ScaleX = node.Skeleton.ScaleX, ScaleY = node.Skeleton.ScaleY };
        reference.SetSkin(node.Skeleton.Data.DefaultSkin);
        var state = new AnimationState(new AnimationStateData(node.Skeleton.Data));
        state.SetAnimation(0, "walk", true);
        state.Update(0.3f);
        state.Apply(reference);
        reference.Update(0.3f);
        reference.UpdateWorldTransform(Spine.Physics.None);

        foreach (var name in new[] { "front-foot", "rear-foot", "head" })
        {
            var expected = reference.FindBone(name);
            var actual = node.Skeleton.FindBone(name);
            Assert.Equal(expected.AppliedPose.WorldX, actual.AppliedPose.WorldX, 3);
            Assert.Equal(expected.AppliedPose.WorldY, actual.AppliedPose.WorldY, 3);
        }
    }

    [Fact]
    public void VerticesGrowPastTheInitialCapacityInsteadOfThrowing()
    {
        using var node = CreateNode();
        node.Advance(Frame(1f / 60f));
        var needed = node.SpineRenderer!.PreparedVertexCount;
        Assert.True(needed > 64, $"SpineBoy should need more than 64 vertices (got {needed}).");

        using var small = new SpineRenderer(new HeadlessRenderer(), node.Skeleton, pma: false, new SpineTextureLoader(),
            initialVertexCapacity: 16);
        small.BuildVertices(0.01f, System.Numerics.Matrix4x4.Identity);

        Assert.Equal(needed, small.PreparedVertexCount);
        Assert.True(small.VertexCapacity >= needed);
    }

    [Fact]
    public void AtlasPixelsAreReleasedAfterLoading()
    {
        using var node = CreateNode();

        Assert.NotEmpty(node.TextureLoader!.VkImageData);
        Assert.All(node.TextureLoader!.VkImageData, page =>
        {
            Assert.Empty(page.Pixels);
            Assert.True(page.Width > 0 && page.Height > 0);
        });
    }

    /// <summary>An <see cref="IRenderer"/> that is not an <see cref="IVulkanContext"/>, so nodes create no GPU resources.</summary>
    private sealed class HeadlessRenderer : IRenderer
    {
        public RenderingBackend Backend => RenderingBackend.Vulkan;
        public bool VSync { get; set; }
        public void OnResize(Vector2D<int> newSize) { }
        public void BeginFrame() { }
        public void EndFrame() { }
        public void SetClearColor(float r, float g, float b, float a = 1) { }
        public void Clear() { }
        public void EnableDepthTest() { }
        public void DisableDepthTest() { }
        public void RequestCapture() { }

        public bool TryTakeCapture([NotNullWhen(true)] out FrameCapture? capture)
        {
            capture = null;
            return false;
        }

        public void Dispose() { }
    }
}
