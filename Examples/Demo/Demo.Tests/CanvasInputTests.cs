using System.Numerics;
using MainframeEngine;

namespace Demo.Tests;

public sealed class CanvasInputTests
{
    [Fact]
    public void WindowPointsBecomeCanvasUnitsThroughPixelScaleAndCanvasTransform()
    {
        var tree = new SceneTree(new ServerRegistry());
        tree.Root.CanvasTransform = new Transform2D(new Vector2(2, 0), new Vector2(0, 2), new Vector2(100, 50)); // zoom 2, offset
        var canvas = CanvasInput.WindowToCanvas(tree.Root, new Vector2(60, 40), pixelScale: 2f);              // → pixel (120, 80)
        Assert.Equal(new Vector2(10, 15), canvas);                                                            // ((120-100)/2, (80-50)/2)
    }
}
