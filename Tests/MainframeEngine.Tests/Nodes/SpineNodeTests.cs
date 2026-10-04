using System.Diagnostics.CodeAnalysis;
using Silk.NET.Maths;
using Spine;

namespace MainframeEngine.Tests.Nodes;

/// <summary>SpineNode logic without a GPU: a non-Vulkan renderer skips every Vulkan resource.</summary>
[Collection(nameof(Debugging.SerialConsole))] // Node.Initialize is process-wide state
public sealed class SpineNodeTests
{
    private static readonly string SpineBoy = Path.Combine(AppContext.BaseDirectory, "Content", "Models", "Spine", "SpineBoy");

    private static SpineNode CreateNode()
    {
        var renderer = new HeadlessRenderer();
        Node.Initialize(renderer);
        return new SpineNode(renderer, new SpineFolder(SpineBoy));
    }

    private static GameTime Frame(float dt) => new() { DeltaTime = dt, FrameCount = 1 };

    [Fact]
    public void SpineScaleSetterAppliesToTheSkeletonAndKeepsFlip()
    {
        using var node = CreateNode();
        Assert.Equal(SpineNode.DefaultSpineScale, node.Skeleton.ScaleX);

        node.SpineScale = 0.5f;
        Assert.Equal(0.5f, node.Skeleton.ScaleX);
        Assert.Equal(0.5f, node.Skeleton.ScaleY);

        node.FlipX(true);
        node.SpineScale = 0.25f;
        Assert.Equal(-0.25f, node.Skeleton.ScaleX);
        Assert.Equal(0.25f, node.Skeleton.ScaleY);

        node.FlipX(false);
        Assert.Equal(0.25f, node.Skeleton.ScaleX);
    }

    [Fact]
    public void SetAnimationReplacesTheCurrentAnimationImmediately()
    {
        using var node = CreateNode();

        node.SetAnimation("walk");
        Assert.Equal("walk", node.CurrentAnimation?.Name);

        node.SetAnimation("run");
        node.OnUpdate(Frame(1f / 60f));
        Assert.Equal("run", node.CurrentAnimation?.Name);
    }

    [Fact]
    public void QueueAnimationKeepsTheCurrentOneUntilItEnds()
    {
        using var node = CreateNode();
        node.SetAnimation("walk");

        node.QueueAnimation("run");
        node.OnUpdate(Frame(1f / 60f));

        Assert.Equal("walk", node.CurrentAnimation?.Name);
    }

    [Fact]
    public void UpdateDrawsThisFramesPoseNotLastFrames()
    {
        using var node = CreateNode();
        node.SetAnimation("walk");
        node.OnUpdate(Frame(0.3f));

        // Reference: Spine's documented order on an identical skeleton.
        var reference = new Skeleton(node.Skeleton.Data) { ScaleX = node.Skeleton.ScaleX, ScaleY = node.Skeleton.ScaleY };
        reference.SetSkin(node.Skeleton.Data.DefaultSkin);
        var state = new AnimationState(new AnimationStateData(node.Skeleton.Data));
        state.SetAnimation(0, "walk", true);
        state.Update(0.3f);
        state.Apply(reference);
        reference.Update(0.3f);
        reference.UpdateWorldTransform(Skeleton.Physics.None);

        foreach (var name in new[] { "front-foot", "rear-foot", "head" })
        {
            var expected = reference.FindBone(name);
            var actual = node.Skeleton.FindBone(name);
            Assert.Equal(expected.WorldX, actual.WorldX, 3);
            Assert.Equal(expected.WorldY, actual.WorldY, 3);
        }
    }

    [Fact]
    public void VerticesGrowPastTheInitialCapacityInsteadOfThrowing()
    {
        using var node = CreateNode();
        node.OnUpdate(Frame(1f / 60f));
        var needed = node.SpineRenderer.PreparedVertexCount;
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

        Assert.NotEmpty(node.TextureLoader.VkImageData);
        Assert.All(node.TextureLoader.VkImageData, page =>
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
