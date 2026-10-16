namespace MainframeEngine.Editor.Tests;

/// <summary>The editor ships BRUTAL too (ADR 0192): its splash shows the BRUTAL logo beside the Mainframe logo, in boxes of one size.</summary>
public sealed class SplashScreenTests
{
    [Fact]
    public void TheSplashShowsBrutalsLogoInABoxTheSizeOfMainframes()
    {
        using var editor = new HeadlessEditor(configure: o => o with { ShowSplash = true });
        var splash = editor.Workspace.Splash;
        splash.Show("Loading…");
        editor.Tick(2);

        var document = splash.Document;
        var mainframe = document.GetElementById("logo");
        var brutal = document.GetElementById("brutal-logo");
        Assert.False(brutal.IsNull);
        Assert.Equal("/Content/Brand/brutal/LOGO_BRUTAL_WHT.png", brutal.GetAttribute("src")); // white on the navy
        var boxes = document.AsElement().QuerySelectorAll(".logo-box");
        Assert.Equal(2, boxes.Length);
        Assert.Equal(boxes[0].Bounds.Width, boxes[1].Bounds.Width, 0.5f);
        Assert.Equal(boxes[0].Bounds.Height, boxes[1].Bounds.Height, 0.5f);
        Assert.Equal(boxes[1].Bounds.Width, MathF.Max(brutal.Bounds.Width, brutal.Bounds.Height), 1f); // fitted: its longer side fills the box
        Assert.True(brutal.Bounds.Width <= boxes[1].Bounds.Width + 0.5f && brutal.Bounds.Height <= boxes[1].Bounds.Height + 0.5f);
        Assert.Equal(boxes[0].Bounds.Width, mainframe.Bounds.Width, 1f);
        Assert.Empty(editor.RmlMessages);
    }
}
