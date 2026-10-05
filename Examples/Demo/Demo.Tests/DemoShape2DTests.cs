using System.Numerics;

namespace Demo.Tests;

public sealed class DemoShape2DTests
{
    private static readonly Vector4 s_red = new(1, 0, 0, 1);
    private static readonly Vector4 s_blue = new(0, 0, 1, 1);

    [Fact]
    public void ARectWithOnlyAColorIsAFlatFill()
    {
        var colors = new Vector4[4];
        var shape = new DemoShape2D { Kind = DemoShapeKind.Rect, Color = s_red };
        Assert.Equal(Vector4.Zero, shape.ColorBottom);
        DemoShape2D.RectVertexColors(shape.Color, shape.ColorBottom, colors);
        Assert.All(colors, c => Assert.Equal(s_red, c));
    }

    [Fact]
    public void ABottomColorMakesATopToBottomGradient()
    {
        var colors = new Vector4[4];
        DemoShape2D.RectVertexColors(s_red, s_blue, colors);
        Assert.Equal([s_red, s_red, s_blue, s_blue], colors); // top-left, top-right, bottom-right, bottom-left (Y down)
    }
}
