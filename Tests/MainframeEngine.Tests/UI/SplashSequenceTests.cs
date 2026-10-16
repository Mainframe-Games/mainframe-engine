using System.Drawing;
using System.Numerics;
using Silk.NET.Input;

namespace MainframeEngine.Tests.UI;

/// <summary>
/// The splash sequence (BRUTAL B1, ADR 0192) headless on an 800×600 UI: the cards and BRUTAL's logo variant, the equal box,
/// skipping and input, the start scene's load, and freeing itself.
/// </summary>
[Collection(nameof(SerialRmlUi))]
public sealed class SplashSequenceTests
{
    private const string WideCard = "Content/Models/Spine/SpineBoy/spineboy-pro.png"; // 1024×256 (Tests/Content)

    private static SplashSequence Add(UiTestTree ui, Action<SplashProjectSettings>? configure = null, SceneLoad? load = null)
    {
        var settings = new SplashProjectSettings();
        configure?.Invoke(settings);
        var splash = new SplashSequence { Settings = settings, Load = load };
        ui.Tree.Root.AddChild(splash);
        ui.Tick(2); // ready, the document loaded and the first card laid out
        return splash;
    }

    private static string Src(SplashSequence splash) => splash.Document!.GetElementById("card")!.GetAttribute("src") ?? "";

    private static void TickIntoHold(UiTestTree ui, SplashSequence splash, int card, float seconds)
    {
        for (var guard = 0; !(splash.Timeline!.CardIndex == card && splash.Timeline.Phase == SplashPhase.Hold && splash.Timeline.PhaseTime >= seconds); guard++)
        {
            Assert.True(guard < 3000, $"Never reached {seconds} s of card {card}'s hold (card {splash.Timeline.CardIndex}, {splash.Timeline.Phase}).");
            ui.Tick();
        }
    }

    private static void TickUntilFreed(UiTestTree ui, SplashSequence splash)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!splash.IsFreed)
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"Stuck: {splash.Timeline?.Phase}, load {splash.Load?.Stage}.");
            ui.Tick();
            Thread.Sleep(1);
        }
    }

    private static List<string> Warnings(Action run, Log.Level levels = Log.Level.Warning | Log.Level.Error)
    {
        var sink = new MemoryLogSink { Levels = levels };
        Log.AddSink(sink);
        try
        {
            run();
        }
        finally
        {
            Log.RemoveSink(sink);
        }

        return [.. sink.Snapshot().Select(e => e.Message)];
    }

    [Fact]
    public void TheDefaultCardsAreMainframeThenBrutalsWhiteLogoOnBlack()
    {
        using var ui = new UiTestTree();
        var splash = Add(ui);
        Assert.Equal(SplashSequence.DefaultLayer, splash.Layer);
        Assert.Equal(int.MaxValue - 1, splash.Layer);
        Assert.Equal(["@mainframe", "@brutal"], splash.Cards.Select(c => c.Entry));
        Assert.Equal([SplashSequence.MainframeLogo, SplashSequence.BrutalLogoWhite], splash.Cards.Select(c => c.ImagePath));
        Assert.True(ui.Tree.IsLoading); // hosts count frames once the game is visible
        Assert.True(splash.Document!.Modal);
        Assert.Equal("/Content/Brand/logo-512.png", Src(splash));
        Assert.Equal("img", splash.Document.GetElementById("card")!.GetAttribute("role"));

        TickIntoHold(ui, splash, 1, 0.1f);
        Assert.Equal("/Content/Brand/brutal/LOGO_BRUTAL_WHT.png", Src(splash));
        Assert.Equal("BRUTAL", splash.Document.GetElementById("card")!.GetAttribute("alt"));
        Assert.True(splash.Document.GetElementById("card")!.IsClassSet("visible"));

        // One UiServer at a time: the light variant is checked without a second tree.
        var light = new SplashProjectSettings { Background = "#f0f0f0" };
        var cards = SplashSequence.ResolveCards(light, static p => ContentPaths.Resolve(p) is var full && File.Exists(full) ? full : null);
        Assert.Equal(SplashSequence.BrutalLogoBlack, cards[1].ImagePath);
    }

    [Fact]
    public void EveryCardFitsTheSameBoxOfFortyPercentOfTheShorterSide()
    {
        using var ui = new UiTestTree(); // 800×600: the box is 240 px
        var splash = Add(ui, s => s.Cards.Add(WideCard));
        Assert.Equal(new Vector2(800, 600), splash.LayoutSize);
        Assert.Equal(new Vector2(240, 240), splash.CardSize); // the 512×512 Mainframe logo
        TickIntoHold(ui, splash, 2, 0.1f);
        Assert.Equal(new Vector2(240, 60), splash.CardSize); // 1024×256 keeps its aspect: the longer side fills the box
        Assert.Equal(new Vector2(30, 7.5f), SplashSequence.Fit(30, new Vector2I(1024, 256)));
        Assert.Equal(new Vector2(60, 240), SplashSequence.FitBox(new Vector2(600, 1000), new Vector2I(256, 1024)));
        Assert.Equal(Vector2.Zero, SplashSequence.Fit(240, new Vector2I(0, 10)));
    }

    [Fact]
    public void AResizeMidSplashRefitsTheCard()
    {
        using var ui = new UiTestTree();
        var splash = Add(ui);
        Assert.Equal(new Vector2(240, 240), splash.CardSize);
        splash.Region = new Rectangle(0, 0, 400, 300);
        ui.Tick();
        Assert.Equal(new Vector2(400, 300), splash.LayoutSize);
        Assert.Equal(new Vector2(120, 120), splash.CardSize);
        TickIntoHold(ui, splash, 1, 0.1f);
        Assert.Equal(120f, MathF.Max(splash.CardSize.X, splash.CardSize.Y), 0.01f); // the next card uses the new size too
    }

    [Fact]
    public void AMissingOrNonPngGameCardIsSkippedWithAWarning()
    {
        using var ui = new UiTestTree();
        File.WriteAllText(Path.Combine(ui.ContentDirectory, "fake.png"), "not an image");
        SplashSequence splash = null!;
        var warnings = Warnings(() => splash = Add(ui, s => s.Cards.AddRange(["Content/Brand/missing.png", "Content/fake.png", WideCard])));
        Assert.Equal(["@mainframe", "@brutal", WideCard], splash.Cards.Select(c => c.Entry));
        Assert.Contains(warnings, w => w.Contains("Content/Brand/missing.png", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("Content/fake.png", StringComparison.Ordinal));

        var none = new SplashProjectSettings { NonProfit = true };
        none.Cards.Clear();
        Assert.Empty(SplashSequence.ResolveCards(none, _ => null));
    }

    [Fact]
    public void AKeyPressSkipsAHoldOnlyAfterOneAndAHalfSecondsAndTheGameNeverSeesIt()
    {
        using var ui = new UiTestTree();
        var splash = Add(ui, s => s.HoldSeconds = 5f);
        ui.Game.Seen.Clear();
        TickIntoHold(ui, splash, 0, 1f);
        Assert.True(ui.Key(Key.Space, pressed: true));
        ui.Key(Key.Space, pressed: false);
        Assert.Equal((0, SplashPhase.Hold), (splash.Timeline!.CardIndex, splash.Timeline.Phase)); // too early

        TickIntoHold(ui, splash, 0, 1.5f);
        ui.Button(MouseButton.Left, pressed: true, 400, 300);
        ui.Button(MouseButton.Left, pressed: false, 400, 300);
        Assert.Equal((0, SplashPhase.FadeOut), (splash.Timeline.CardIndex, splash.Timeline.Phase));

        TickIntoHold(ui, splash, 1, 1.6f);
        ui.Pad(ButtonName.A, pressed: true);
        ui.Pad(ButtonName.A, pressed: false);
        Assert.Equal((1, SplashPhase.FadeOut), (splash.Timeline.CardIndex, splash.Timeline.Phase));
        ui.Move(10, 10);
        Assert.Empty(ui.Game.Seen); // keys, clicks, pads and motion were all consumed
    }

    [Fact]
    public void AKeyHeldFromLaunchOrItsRepeatsNeverSkip()
    {
        using var ui = new UiTestTree();
        ui.Key(Key.Enter, pressed: true); // held before the splash exists
        ui.Pad(ButtonName.Start, pressed: true);
        var splash = Add(ui, s => s.HoldSeconds = 5f);
        TickIntoHold(ui, splash, 0, 1.6f);
        ui.Key(Key.Enter, pressed: true); // auto-repeat of the held key
        ui.Key(Key.Enter, pressed: true);
        ui.Pad(ButtonName.Start, pressed: true);
        Assert.Equal(SplashPhase.Hold, splash.Timeline!.Phase);

        ui.Key(Key.Enter, pressed: false);
        ui.Key(Key.Enter, pressed: true); // a fresh press
        Assert.Equal(SplashPhase.FadeOut, splash.Timeline.Phase);
    }

    [Fact]
    public void AnUnskippableSplashIgnoresInput()
    {
        using var ui = new UiTestTree();
        var splash = Add(ui, s => s.Skippable = false);
        TickIntoHold(ui, splash, 0, 1.6f);
        ui.Key(Key.Space, pressed: true);
        Assert.Equal(SplashPhase.Hold, splash.Timeline!.Phase);
        Assert.Empty(ui.Game.Seen);
    }

    [Fact]
    public void TheLastCardWaitsForTheLoadWithASpinnerThenTheSplashFreesItself()
    {
        var folder = Directory.CreateTempSubdirectory("mf-splash-load").FullName;
        try
        {
            var path = Path.Combine(folder, "Level.mscene");
            File.WriteAllBytes(path, SceneSaver.ToJson(new Node3D { Name = "Level" }));
            using var ui = new UiTestTree();
            var load = ui.Tree.ChangeSceneToFileAsync(path, new SceneLoadOptions { HoldSeconds = 2f }); // 2 s of wall clock
            var splash = Add(ui, s => s.HoldSeconds = 1.5f, load);
            var finished = 0;
            splash.Finished += () => finished++;

            for (var guard = 0; splash.Timeline!.Phase != SplashPhase.WaitingForLoad; guard++)
            {
                Assert.True(guard < 1000, $"Never waited: {splash.Timeline.Phase}, load {load.Stage}.");
                ui.Tick(); // 3.9 s of splash in well under a second of wall clock: the load is still held
            }

            Assert.Equal(1, splash.Timeline.CardIndex);
            Assert.True(splash.Document!.GetElementById("spinner")!.IsClassSet("visible"));
            Assert.True(splash.Document.GetElementById("card")!.IsClassSet("visible"));

            TickUntilFreed(ui, splash);
            Assert.Equal(1, finished);
            Assert.True(load.IsDone);
            Assert.Equal("Level", ui.Tree.CurrentScene!.Name);
            Assert.False(ui.Tree.IsLoading);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AFailedLoadEndsTheSplashAtOnce()
    {
        using var ui = new UiTestTree();
        var load = ui.Tree.ChangeSceneToFileAsync(Path.Combine(Path.GetTempPath(), "mf-missing-" + Guid.NewGuid().ToString("N") + ".mscene"));
        var splash = Add(ui, load: load);
        TickUntilFreed(ui, splash);
        Assert.Equal(SceneLoadStage.Failed, load.Stage);
        Assert.True(splash.IsFinished);
        Assert.Equal(0, splash.Timeline!.CardIndex); // it did not play the rest
    }

    [Fact]
    public void AFastLoadStillPlaysEveryCardThenTheSplashFreesItself()
    {
        var folder = Directory.CreateTempSubdirectory("mf-splash-fast").FullName;
        try
        {
            var path = Path.Combine(folder, "Fast.mscene");
            File.WriteAllBytes(path, SceneSaver.ToJson(new Node3D { Name = "Fast" }));
            using var ui = new UiTestTree();
            var load = ui.Tree.ChangeSceneToFileAsync(path);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (!load.IsCompleted)
            {
                Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"Load stuck: {load.Stage}.");
                ui.Tick();
                Thread.Sleep(1);
            }

            var splash = Add(ui, load: load); // the scene was ready before the first card
            var frames = 2;
            while (!splash.IsFreed)
            {
                ui.Tick();
                Assert.True(++frames < 1000);
            }

            Assert.True(frames >= (int)(2 * (0.3f + 2f + 0.3f) * 60f), $"The sequence took {frames} frames: a card was shortened.");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
