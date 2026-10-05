namespace MainframeEngine.Tests.Scene;

public sealed class SceneTreeHostTests
{
    [Fact]
    public void QuitAsksTheEngineWithTheCode()
    {
        var tree = new SceneTree();
        var codes = new List<int>();
        tree.QuitRequested += codes.Add;
        tree.Quit();
        tree.Quit(3);
        Assert.Equal([0, 3], codes);
    }

    [Fact]
    public void CaptureFrameNeedsCaptureAndDeliversOnce()
    {
        var tree = new SceneTree();
        Assert.Throws<InvalidOperationException>(() => tree.CaptureFrame(_ => { }));

        tree.CanCaptureFrame = true;
        var requests = 0;
        tree.CaptureRequested += () => requests++;
        var seen = new List<FrameCapture>();
        tree.CaptureFrame(seen.Add);
        tree.CaptureFrame(seen.Add);
        Assert.Equal(2, requests);

        var capture = new FrameCapture(1, 1, [1, 2, 3, 4]);
        tree.DeliverCapture(capture);
        tree.DeliverCapture(capture); // callbacks run once
        Assert.Equal([capture, capture], seen);
    }

    [Fact]
    public void FrameCaptureFlagEnablesCapture()
    {
        Assert.True(GameHostOptions.Parse(["--frame-capture"]).Apply(new EngineOptions { GameName = "G" }).EnableFrameCapture);
        Assert.False(GameHostOptions.Parse([]).Apply(new EngineOptions { GameName = "G" }).EnableFrameCapture);
    }
}
