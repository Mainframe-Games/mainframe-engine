namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The engine's default loading screen (ADR 0183, <see cref="LoadingScreen.DefaultSource"/>) at a fixed state: the game's
/// name, 62 % and a stage label, laid out from a 1080p reference as new projects scale their UI. RCSS animations run on the
/// UI's clock (the fixed step), so frame N is always the same. Self-check: the bar's fill covers 62 % of its track. The
/// screen fades out after <see cref="FadeFrame"/>; loading frames do not count for <c>--frames</c>, so the run ends once
/// it is gone.
/// </summary>
public sealed class LoadingScreenScene(HostOptions host) : RenderTestGame(host)
{
    public const float ShownProgress = 0.62f;
    public const uint CheckFrame = 30, FadeFrame = 41;

    private LoadingScreen _screen = null!;

    protected override void LoadScene()
    {
        _screen = new LoadingScreen
        {
            Title = "Render Test",
            Stage = "Growing trees",
            Progress = ShownProgress,
            ScaleMode = UiScaleMode.ReferenceResolution,
            ReferenceResolution = new System.Numerics.Vector2(1920, 1080),
        };
        Tree.Root.AddChild(_screen);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        var frame = gameTime.FrameCount;
        if (frame == CheckFrame)
        {
            if (Math.Abs(_screen.ShownProgress - ShownProgress) > 1e-3f)
                Fail($"The bar shows {_screen.ShownProgress}, expected {ShownProgress}.");
            if (_screen.Document is not { IsLoaded: true } document)
            {
                Fail("The loading screen's document did not load.");
                return;
            }

            var track = document.GetElementById("track")!.Bounds;
            var fill = document.GetElementById("fill")!.Bounds;
            var share = fill.Width / Math.Max(1f, track.Width);
            if (Math.Abs(share - ShownProgress) > 0.01f)
                Fail($"The fill covers {share:P1} of the track, expected {ShownProgress:P0}.");
            if (!Tree.IsLoading)
                Fail("The tree does not report loading while the screen is up.");
        }

        if (frame == FadeFrame)
            _screen.FadeOut();
    }

    protected override void DisposeScene()
    {
    }
}
