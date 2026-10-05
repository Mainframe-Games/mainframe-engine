using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.UI;

public sealed class UiEngineTextureTests
{
    [Fact]
    public void DepthSourceWithoutAViewIsNotDrawn()
    {
        var texture = new UiEngineTexture(static () => default, (uint)UiTextureConversion.DepthToGray);
        Assert.False(texture.TryGetView(out _, out _, out _));
        Assert.True(texture.IsSource);
    }

    [Fact]
    public void DepthSourceReportsItsLayoutAndSize()
    {
        var view = new UiTextureView(new ImageView(42), new Sampler(7), ImageLayout.DepthStencilReadOnlyOptimal, 512, 256);
        var texture = new UiEngineTexture(() => view, (uint)UiTextureConversion.DepthToGray);
        Assert.True(texture.TryGetView(out var image, out var sampler, out var layout));
        Assert.Equal((42ul, 7ul, ImageLayout.DepthStencilReadOnlyOptimal), (image.Handle, sampler.Handle, layout));
        Assert.True(texture.TryGetImage(out var size));
        Assert.Equal((512u, 256u), (size.Width, size.Height));
    }

    [Fact]
    public void SourceFollowsViewChangesAndDisappearance()
    {
        var current = new UiTextureView(new ImageView(1), new Sampler(2), ImageLayout.DepthStencilReadOnlyOptimal, 64, 64);
        var texture = new UiEngineTexture(() => current, (uint)UiTextureConversion.DepthToGray);
        Assert.True(texture.TryGetView(out var first, out _, out _));
        current = current with { View = new ImageView(3) };
        Assert.True(texture.TryGetView(out var second, out _, out _));
        current = default;
        Assert.False(texture.TryGetView(out _, out _, out _));
        Assert.Equal((1ul, 3ul), (first.Handle, second.Handle));
    }

    private const ImageLayout Depth = ImageLayout.DepthStencilReadOnlyOptimal;

    [Fact]
    public void RebindIsNotNeededForTheSameViewLayoutAndGeneration()
    {
        var image = new UiTextureView(new ImageView(5), new Sampler(1), Depth, 8, 8, 3);
        Assert.False(image.NeedsRebind(new ImageView(5), Depth, 3));
    }

    [Fact]
    public void RebindIsNeededWhenViewLayoutOrGenerationChanges()
    {
        var image = new UiTextureView(new ImageView(5), new Sampler(1), Depth, 8, 8, 3);
        Assert.True(image.NeedsRebind(new ImageView(6), Depth, 3));
        Assert.True(image.NeedsRebind(new ImageView(5), ImageLayout.ShaderReadOnlyOptimal, 3));
        Assert.True(image.NeedsRebind(new ImageView(5), Depth, 2));
    }

    [Fact]
    public void RebindIsNeededWhenAHandleValueIsReusedAfterAReset()
    {
        // ResolveTexture resets the bound view to default when the source has nothing to show.
        var reused = new UiTextureView(new ImageView(5), new Sampler(1), Depth, 8, 8);
        Assert.True(reused.NeedsRebind(default, default, 0));
    }

    [Fact]
    public void SourceReportsItsGeneration()
    {
        var current = new UiTextureView(new ImageView(5), new Sampler(1), Depth, 8, 8, 1);
        var texture = new UiEngineTexture(() => current, (uint)UiTextureConversion.DepthToGray);
        Assert.True(texture.TryGetImage(out var first));
        current = current with { Generation = 2 };
        Assert.True(texture.TryGetImage(out var second));
        Assert.Equal((1ul, 2ul), (first.Generation, second.Generation));
        Assert.True(second.NeedsRebind(first.View, first.Layout, first.Generation));
    }
}
