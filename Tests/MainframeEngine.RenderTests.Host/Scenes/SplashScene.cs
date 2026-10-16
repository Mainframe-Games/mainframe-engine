namespace MainframeEngine.RenderTests.Host.Scenes;

/// <summary>
/// The splash sequence (BRUTAL B1, ADR 0192) on black: @mainframe, @brutal (white logo) and a wide game card
/// (spineboy-pro.png, 1024×256) with 1.5 s holds, unskippable, on the fixed 60 fps step. Each card's 126-frame slot is
/// 18 frames of fade in, 90 of hold and 18 of fade out; <see cref="CaptureFrames"/> are the middles of the holds.
/// Self-check: at each capture the expected card is held and its longer side fills the 40 % box.
/// </summary>
public sealed class SplashScene(HostOptions host) : RenderTestGame(host)
{
    public const string GameCard = "Content/Models/Spine/SpineBoy/spineboy-pro.png";
    public static readonly uint[] CaptureFrames = [63, 189, 315];

    private SplashSequence _splash = null!;

    protected override void LoadScene()
    {
        var settings = new SplashProjectSettings { HoldSeconds = 1.5f };
        settings.Cards.Add(GameCard);
        _splash = new SplashSequence { Settings = settings, HoldsLoading = false, Skippable = false };
        Tree.Root.AddChild(_splash);
    }

    protected override void UpdateScene(in GameTime gameTime)
    {
        var index = Array.IndexOf(CaptureFrames, (uint)gameTime.FrameCount);
        if (index < 0)
            return;
        if (_splash.Timeline is not { } timeline)
        {
            Fail("The splash has no timeline.");
            return;
        }

        if (timeline.Phase != SplashPhase.Hold || timeline.CardIndex != index)
            Fail($"Frame {gameTime.FrameCount}: card {timeline.CardIndex} is in {timeline.Phase}; expected card {index} held.");
        var box = SplashSequence.BoxFraction * MathF.Min(_splash.LayoutSize.X, _splash.LayoutSize.Y);
        var longest = MathF.Max(_splash.CardSize.X, _splash.CardSize.Y);
        if (MathF.Abs(longest - box) > 1f)
            Fail($"Card {index} is {_splash.CardSize.X}×{_splash.CardSize.Y} px; its longer side should fill the {box} px box.");
    }

    protected override void DisposeScene()
    {
    }
}
