using Silk.NET.Vulkan;

namespace MainframeEngine.Tests.Rendering;

public sealed class OverlayRendererListTests
{
    private sealed class Fake(string name) : IOverlayRenderer
    {
        public void RecordOffscreen(CommandBuffer commandBuffer) { }
        public void RecordOverlay(CommandBuffer commandBuffer) { }
        public override string ToString() => name;
    }

    [Fact]
    public void RenderersAreOrderedByOrderThenRegistration()
    {
        var list = new OverlayRendererList();
        Fake ui = new("ui"), canvas = new("canvas"), gizmos = new("gizmos"), ui2 = new("ui2");
        list.Add(ui, OverlayOrder.Ui);
        list.Add(canvas, OverlayOrder.Canvas);
        list.Add(ui2, OverlayOrder.Ui);
        list.Add(gizmos, OverlayOrder.Gizmos);
        Assert.Equal(["canvas", "gizmos", "ui", "ui2"], Enumerable.Range(0, list.Count).Select(i => list[i].ToString()));
    }

    [Fact]
    public void AddingTwiceKeepsOneAndRemoveWorks()
    {
        var list = new OverlayRendererList();
        var a = new Fake("a");
        list.Add(a, 5);
        list.Add(a, 7);
        Assert.Equal(1, list.Count);
        Assert.True(list.Remove(a));
        Assert.Equal(0, list.Count);
    }
}
