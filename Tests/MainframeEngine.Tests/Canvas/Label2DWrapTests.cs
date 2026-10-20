using System.Numerics;

namespace MainframeEngine.Tests.Canvas;

/// <summary>#113: <see cref="Label2D"/> newlines, <see cref="Label2D.Autowrap"/> and alignment in its box.</summary>
public sealed class Label2DWrapTests
{
    private static readonly Font Lato = Font.FromData(File.ReadAllBytes(ContentPaths.Resolve("Assets/UI/fonts/LatoLatin-Regular.ttf")));

    private static float Width(string text) => Lato.GetStringSize(text, fontSize: 16).X;

    [Fact]
    public void NewlinesBreakLinesWithoutWrapping()
    {
        var label = new Label2D { Font = Lato, Text = "first\nsecond\r\n\nfourth" };

        Assert.Equal(["first", "second", "", "fourth"], label.GetLines());
        var height = Lato.GetHeight(16);
        Assert.Equal(4 * height + 3 * 3f, label.GetMinimumSize().Y); // Godot's default line spacing, 3
    }

    [Fact]
    public void WordWrapBreaksAtWordBoundaries()
    {
        const string text = "Drag the petal onto the mortar";
        var label = new Label2D { Font = Lato, Text = text, Autowrap = AutowrapMode.Word, Width = MathF.Ceiling(Width("Drag the petal")) };

        Assert.Equal(["Drag the petal", "onto the", "mortar"], label.GetLines());
        Assert.All(label.GetLines(), line => Assert.True(Width(line) <= label.Width));
        Assert.Equal(label.Width, label.Size.X);
    }

    [Fact]
    public void WithoutAWidthOrAModeNothingWraps()
    {
        Assert.Single(new Label2D { Font = Lato, Text = "a b c d", Autowrap = AutowrapMode.Word }.GetLines());
        Assert.Single(new Label2D { Font = Lato, Text = "a b c d", Width = 5 }.GetLines());
    }

    [Fact]
    public void WordOverflowsAndWordSmartBreaksTheLongWord()
    {
        var width = MathF.Ceiling(Width("tally"));
        var word = new Label2D { Font = Lato, Text = "tally snakeoilsnakeoil", Autowrap = AutowrapMode.Word, Width = width };
        Assert.Equal(["tally", "snakeoilsnakeoil"], word.GetLines());

        var smart = new Label2D { Font = Lato, Text = "tally snakeoilsnakeoil", Autowrap = AutowrapMode.WordSmart, Width = width };
        var lines = smart.GetLines();
        Assert.Equal("tally", lines[0]);
        Assert.True(lines.Count > 2);
        Assert.Equal("snakeoilsnakeoil", string.Concat(lines.Skip(1)));
        Assert.All(lines, line => Assert.True(Width(line) <= width));
    }

    [Fact]
    public void ArbitraryBreaksBetweenAnyCharacters()
    {
        var width = MathF.Ceiling(Width("abc"));
        var label = new Label2D { Font = Lato, Text = "abcdefgh", Autowrap = AutowrapMode.Arbitrary, Width = width };

        Assert.Equal("abcdefgh", string.Concat(label.GetLines()));
        Assert.All(label.GetLines(), line => Assert.True(Width(line) <= width));
    }

    [Fact]
    public void TheBoxIsAtLeastTheTextsAndHeightIsKept()
    {
        var label = new Label2D { Font = Lato, Text = "one\ntwo", Width = 200, Height = 100, VerticalAlignment = VerticalAlignment.Center };

        Assert.Equal(new Vector2(200, 100), label.Size);
        label.Height = 1;
        Assert.Equal(label.GetMinimumSize().Y, label.Size.Y);
        Assert.Equal(Vector2.Zero, new Label2D { Text = "no font" }.GetMinimumSize());
    }
}
