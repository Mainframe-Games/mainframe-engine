using System.Numerics;

namespace MainframeEngine.Tests.Rendering;

public sealed class ScreenGizmoBatchTests
{
    private static readonly Vector4 Red = new(1, 0, 0, 1);

    [Fact]
    public void FilledRectIsTwoTriangles()
    {
        var batch = new ScreenGizmoBatch();
        batch.FilledRect(new Vector2(10, 10), new Vector2(20, 30), Red);
        Assert.Equal(6, batch.VertexCount);
        Assert.All(batch.Vertices.ToArray(), v => Assert.Equal(Red, v.Color));
        Assert.Equal(new Vector2(10, 10), batch.Vertices.ToArray().Aggregate(new Vector2(float.MaxValue), (m, v) => Vector2.Min(m, v.Position)));
    }

    [Fact]
    public void ThickLineIsACoreQuadPlusTwoFeatherQuadsFadingToTransparent()
    {
        var batch = new ScreenGizmoBatch();
        batch.Line(new Vector2(0, 0), new Vector2(100, 0), Red, thickness: 4f);
        var vertices = batch.Vertices.ToArray();
        Assert.Equal(18, vertices.Length);
        var maxY = vertices.Max(v => MathF.Abs(v.Position.Y));
        Assert.Equal(2f + ScreenGizmoBatch.Feather, maxY, 3);                          // half thickness + feather
        Assert.All(vertices.Where(v => MathF.Abs(v.Position.Y) > 2.01f), v => Assert.Equal(0f, v.Color.W));
    }

    [Fact]
    public void ZeroLengthLineEmitsNothing()
    {
        var batch = new ScreenGizmoBatch();
        batch.Line(new Vector2(5, 5), new Vector2(5, 5), Red);
        Assert.Equal(0, batch.VertexCount);
    }

    [Fact]
    public void SteadyStateDoesNotAllocate()
    {
        var batch = new ScreenGizmoBatch();
        Fill(batch);
        batch.Clear();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++)
        {
            Fill(batch);
            batch.Clear();
        }

        Assert.Equal(before, GC.GetAllocatedBytesForCurrentThread());

        static void Fill(ScreenGizmoBatch b)
        {
            b.Circle(new Vector2(50), 20, Red);
            b.Arrow(Vector2.Zero, new Vector2(40, 0), Red);
            b.Glyph('X', new Vector2(5), 10, Red);
        }
    }

    [Fact]
    public void UnknownGlyphsAreIgnored()
    {
        var batch = new ScreenGizmoBatch();
        batch.Glyph('Q', Vector2.Zero, 10, Red);
        Assert.Equal(0, batch.VertexCount);
    }
}
