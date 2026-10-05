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
        Assert.Equal((512, 256), texture.Size);
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
}
