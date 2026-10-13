using System.Numerics;

namespace MainframeEngine.Editor.Tests;

public sealed class QaScriptTests
{
    [Fact]
    public void LayoutPointsBecomeMousePointsOnARetinaDisplay()
    {
        // --scale 2 on a 2x display: the window is 1600 points for a 1600-point layout.
        var host = new HeadlessEditorHost { PixelScale = 2f, PointerScale = 1f };
        Assert.Equal(new Vector2(1030, 667), EditorQaScript.Pointer(host, new Vector2(1030, 667)));
    }

    [Fact]
    public void LayoutPointsBecomeMousePointsOnA1xDisplay()
    {
        // --scale 2 on a 1x display: the window is 3200 points for a 1600-point layout, so a click at layout (1030, 667)
        // (New Project's Create & Open) is mouse (2060, 1334); without the conversion it hit the dialog's description.
        var host = new HeadlessEditorHost { PixelScale = 2f, PointerScale = 0.5f };
        Assert.Equal(new Vector2(2060, 1334), EditorQaScript.Pointer(host, new Vector2(1030, 667)));
    }
}
