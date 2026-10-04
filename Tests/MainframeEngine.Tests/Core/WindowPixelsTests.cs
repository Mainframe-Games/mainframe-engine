using Silk.NET.Maths;

namespace MainframeEngine.Tests.Core;

/// <summary>
/// The size math behind <see cref="EngineOptions.ContentScale"/>: a fixed-scale window is sized in OS points for the
/// display it lands on so its framebuffer is exactly the layout size × the scale (the windowed path is covered by the
/// render test <c>FixedContentScaleCapturesAtTheExactPixelSize</c>).
/// </summary>
public class WindowPixelsTests
{
    [Theory]
    [InlineData(320, 240, 2f, 640, 480)]
    [InlineData(320, 240, 1f, 320, 240)]
    [InlineData(400, 300, 1.5f, 600, 450)]
    [InlineData(1280, 720, 1f, 1280, 720)]
    public void FramebufferIsLayoutSizeTimesContentScale(int width, int height, float scale, int pixelsX, int pixelsY) =>
        Assert.Equal(new Vector2D<int>(pixelsX, pixelsY), WindowPixels.PixelsFor(new Vector2D<int>(width, height), scale));

    [Theory]
    [InlineData(640, 480, 2f, 320, 240)]  // Retina: half the points
    [InlineData(640, 480, 1f, 640, 480)]  // 1× monitor: points are pixels
    [InlineData(1000, 800, 1.25f, 800, 640)] // fractional display scale that divides the request
    [InlineData(1, 1, 2f, 1, 1)]          // never below 1×1
    public void WindowPointsReachTheRequestedPixelsOnTheDisplay(int pixelsX, int pixelsY, float displayScale, int pointsX, int pointsY) =>
        Assert.Equal(new Vector2D<int>(pointsX, pointsY),
            WindowPixels.WindowPointsFor(new Vector2D<int>(pixelsX, pixelsY), new Vector2D<float>(displayScale, displayScale)));

    [Fact]
    public void UnknownDisplayScaleCountsAsOne() =>
        Assert.Equal(new Vector2D<int>(640, 480), WindowPixels.WindowPointsFor(new Vector2D<int>(640, 480), new Vector2D<float>(0f, 0f)));
}
