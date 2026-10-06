using System.Numerics;
using Color = MainframeEngine.Color;

namespace MainframeEngine.Tests.Core;

/// <summary>The Godot-derived core math (ADR 0142): Vector2 extension members, Vector2I/Rect2I/Color, serialization.</summary>
public sealed class MathCoreTests
{
    [Fact]
    public void Vector2HelpersKeepGodotsFormulas()
    {
        Assert.Equal(Vector2.Zero, Vector2.Zero.Normalized()); // System.Numerics' Normalize gives NaN here
        Assert.Equal(new Vector2(0.6f, 0.8f), new Vector2(3, 4).Normalized());
        Assert.Equal(5f, new Vector2(1, 1).DistanceTo(new Vector2(4, 5)));
        Assert.Equal(new Vector2(0, 1), new Vector2(0, 0).DirectionTo(new Vector2(0, 7)));
        Assert.Equal(new Vector2(3, 0), Vector2.Zero.MoveToward(new Vector2(10, 0), 3));
        Assert.Equal(new Vector2(10, 0), Vector2.Zero.MoveToward(new Vector2(10, 0), 30)); // never past the target
        Assert.Equal(new Vector2(0, -1), Vector2.Up);
        Assert.Equal(new Vector2(0, 1), Vector2.Down);
        Assert.Equal(new Vector2(-1, 0), Vector2.Left);
        Assert.Equal(new Vector2(1, 0), Vector2.Right);
        Assert.True(Vector2.Right.Rotated(MathF.PI / 2).IsEqualApprox(Vector2.Down));
        Assert.True(Vector2.FromAngle(MathF.PI).IsEqualApprox(Vector2.Left));
        Assert.Equal(MathF.PI / 2, Vector2.Down.Angle(), 5);
        Assert.Equal(new Vector2(5, 10), new Vector2(0, 0).Lerp(new Vector2(10, 20), 0.5f));
        Assert.Equal(new Vector2(30, 60), new Vector2(31, 58).Snapped(new Vector2(10, 20)));
        Assert.Equal(new Vector2(-1, 1), new Vector2(-0.5f, 3).Sign());
        Assert.Equal(new Vector2(2, 3), new Vector2(2, 3).Max(new Vector2(1, 3)));
        Assert.Equal(new Vector2(0.6f, 0.8f) * 2, new Vector2(3, 4).LimitLength(2));
        Assert.Equal(new Vector2(1, -1), new Vector2(1, 1).Reflect(Vector2.Right)); // mirrored across the line, Godot's rule
        Assert.Equal(Vector2Extensions.Axis.Y, new Vector2(1, 2).MaxAxisIndex());
    }

    [Fact]
    public void Vector2IAndRect2IConvertToTheEngineTypes()
    {
        Vector2 widened = new Vector2I(3, -4);
        Assert.Equal(new Vector2(3, -4), widened);
        Assert.Equal(new Vector2I(2, -2), (Vector2I)new Vector2(2.9f, -2.9f)); // truncates toward zero, like Godot
        Rect2 rect = new Rect2I(1, 2, 3, 4);
        Assert.Equal(new Rect2(1, 2, 3, 4), rect);
        Assert.True(new Rect2I(0, 0, 4, 4).HasPoint(new Vector2I(3, 3)));
        Assert.False(new Rect2I(0, 0, 4, 4).HasPoint(new Vector2I(4, 0)));
    }

    [Fact]
    public void ColorIsTheRendererVector4AndParsesHtml()
    {
        Vector4 v = new Color(0.1f, 0.2f, 0.3f, 0.4f);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), v);
        Color back = v;
        Assert.Equal(new Color(0.1f, 0.2f, 0.3f, 0.4f), back);
        Assert.Equal(new Color(1, 0, 0), Color.FromHtml("#ff0000"));
        Assert.Equal("ff0000ff", new Color(1, 0, 0).ToHtml());
        Assert.Equal(new Color(1, 1, 1), Colors.White);
        Assert.Equal(0f, Colors.Transparent.A);
    }

    [Fact]
    public void TheNewValueTypesSerializeAsNumberArrays()
    {
        var source = new MathTypesNode { Cell = new Vector2I(-3, 7), Area = new Rect2I(1, 2, 3, 4), Tint = new Color(0.25f, 0.5f, 0.75f, 1f) };
        var copy = PackedScene.Pack(source).Instantiate<MathTypesNode>();
        Assert.Equal(source.Cell, copy.Cell);
        Assert.Equal(source.Area, copy.Area);
        Assert.Equal(source.Tint, copy.Tint);
        source.Free();
        copy.Free();
    }

    [Fact]
    public void BitmapsExpandOneChannelDataLikeTheGpuSamplesIt()
    {
        var grey = Bitmap.FromLuminance(2, 1, [10, 200]);
        Assert.Equal(new byte[] { 10, 10, 10, 255, 200, 200, 200, 255 }, grey.Data);
        var red = Bitmap.FromRed(1, 1, [77]);
        Assert.Equal(new byte[] { 77, 0, 0, 255 }, red.Data);
        var bitmap = new Bitmap(2, 2);
        bitmap.Fill(new Color(1, 0, 0, 1));
        bitmap.SetPixel(1, 1, new Color(0, 1, 0, 0.5f));
        Assert.Equal(new Color(1, 0, 0, 1), bitmap.GetPixel(0, 0));
        Assert.Equal(128, bitmap.Data[(1 * 2 + 1) * 4 + 3]);
        Assert.Throws<ArgumentOutOfRangeException>(() => bitmap.SetPixel(2, 0, Colors.White));
    }
}

[Collection(nameof(Debugging.SerialEnvironment))]
public sealed class GameHostUserDataTests
{
    [Fact]
    public void TheGamesFolderIsUnderTheUserDataBaseAndCreatedOnUse()
    {
        var previous = Environment.GetEnvironmentVariable(UserDataPaths.OverrideVariable);
        var root = Path.Combine(Path.GetTempPath(), "mf-userdata-" + Guid.NewGuid().ToString("N"));
        try
        {
            Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, root);
            var expected = UserDataPaths.GetDirectory(GameHost.Project?.Name is { Length: > 0 } name ? name : "game");
            Assert.Equal(expected, GameHost.UserDataDirectory);
            Assert.True(Directory.Exists(expected));
            Assert.Equal(Path.Combine(expected, "saves", "run-1.json"), GameHost.UserDataPath("saves/run-1.json"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(UserDataPaths.OverrideVariable, previous);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}

internal sealed class MathTypesNode : Node2D
{
    [Export] public Vector2I Cell { get; set; }
    [Export] public Rect2I Area { get; set; }
    [Export] public Color Tint { get; set; } = Colors.White;
}
