using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>Godot's <c>Gradient</c> sampling and <c>GradientTexture2D</c> fills (ADR 0117).</summary>
public sealed class GradientTests
{
    private static Gradient Torch() => new()
    {
        Offsets = [0f, 0.55f, 1f],
        Colors = [Vector4.One, new Vector4(1, 1, 1, 0.45f), new Vector4(1, 1, 1, 0f)],
    };

    [Fact]
    public void SamplingLerpsBetweenNeighboursAndClampsOutside()
    {
        var g = Torch();
        Assert.Equal(Vector4.One, g.Sample(-1));
        Assert.Equal(new Vector4(1, 1, 1, 0.45f), g.Sample(0.55f));
        Assert.Equal(0.725f, g.Sample(0.275f).W, 5);
        Assert.Equal(0f, g.Sample(2).W);
        g.InterpolationMode = GradientInterpolationMode.Constant;
        Assert.Equal(1f, g.Sample(0.5f).W);
    }

    [Fact]
    public void UnsortedPointsSampleAsSorted()
    {
        var g = new Gradient { Offsets = [1f, 0f], Colors = [Vector4.One, new Vector4(0, 0, 0, 1)] };
        Assert.Equal(0.25f, g.Sample(0.25f).X, 5);
    }

    [Fact]
    public void ARadialFillMeasuresFromTheCentreOverSizeMinusOne()
    {
        var texture = new GradientTexture2D
        {
            Width = 5,
            Height = 5,
            Fill = GradientTexture2D.FillEnum.Radial,
            FillFrom = new Vector2(0.5f, 0.5f),
            FillTo = new Vector2(0.5f, 0f),
            Gradient = Torch(),
        };
        var (rgba, w, h) = texture.DecodePixels();
        Assert.Equal((5, 5), (w, h));
        Assert.Equal((5, 5), (texture.Width, ((Texture2D)texture).Height));
        Assert.Equal(255, rgba[(2 * 5 + 2) * 4 + 3]);      // centre: offset 0
        // (3, 2): pos (0.75, 0.5), offset 0.25/0.5 = 0.5 → alpha lerp(1, 0.45, 0.5/0.55) = 0.5 → 128.
        Assert.Equal(128, rgba[(2 * 5 + 3) * 4 + 3]);
        Assert.Equal(0, rgba[(2 * 5 + 4) * 4 + 3]);        // edge: offset 1
        Assert.Equal(0, rgba[3]);                           // corner, clamped past 1
    }

    [Fact]
    public void ALinearFillFollowsTheAxisAndChangesRebuildPixels()
    {
        var texture = new GradientTexture2D { Width = 3, Height = 1, Gradient = new Gradient() };
        var version = texture.Version;
        Assert.Equal([0, 128, 255], Enumerable.Range(0, 3).Select(x => (int)texture.DecodePixels().Rgba[x * 4]));
        texture.Repeat = GradientTexture2D.RepeatEnum.Mirror;
        texture.FillTo = new Vector2(0.5f, 0);
        Assert.True(texture.Version > version);
        Assert.Equal([0, 255, 0], Enumerable.Range(0, 3).Select(x => (int)texture.DecodePixels().Rgba[x * 4]));
    }

    [Fact]
    public void AGradientTextureSavesWithTheSceneAsASubResource()
    {
        var node = new GradientHolder
        {
            Name = "GradientHolder",
            Texture = new GradientTexture2D { Width = 8, Height = 4, Fill = GradientTexture2D.FillEnum.Square, Gradient = Torch() },
        };
        var copy = PackedScene.Parse(SceneSaver.ToJson(node)).Instantiate<GradientHolder>();
        var texture = Assert.IsType<GradientTexture2D>(copy.Texture);
        Assert.Equal((8, 4, GradientTexture2D.FillEnum.Square), (texture.Width, texture.Height, texture.Fill));
        Assert.Equal([0f, 0.55f, 1f], texture.Gradient!.Offsets);
        node.Free();
        copy.Free();
    }
}

public sealed class GradientHolder : Node2D
{
    [Export] public Texture2D? Texture { get; set; }
}
