using MainframeEngine.EditorLink;
using MainframeEngine.Editor.Music;

namespace MainframeEngine.Editor.Tests.Music.Adaptive;

/// <summary>
/// A6: the editor side of the adaptive music link. Games are watched while an adaptive song is open and report their
/// players; the music commands and <c>ReloadMusic</c> go only to games whose link has them (protocol 6); the Adaptive
/// panel's Target sends a game's player the audition values and stingers, follows its position, and Copy to Defaults
/// takes its values as one undo step.
/// </summary>
[Collection(nameof(SerialEditor))]
public sealed class AdaptiveTargetTests
{
    private static EditorLinkMusicInstance Report(string song, string section) => new()
    {
        NodePath = "/root/Level/Music",
        Music = "mus_00000000a6a6",
        Song = song,
        Section = section,
        Bar = 2,
        Beat = 3,
        Seconds = 1.25f,
        Pending = "",
        PendingSeconds = -1f,
        Parameters = [("intensity", 0.7f), ("area", 1f)],
        Layers = [("main", 0f), ("l1", -6f)],
    };

    [Fact]
    public void AWatchedGameReportsItsMusicAndTakesTheMusicCommands()
    {
        using var play = new PlayHarness();
        var instance = play.LaunchRunning();
        var game = play.Game(instance);
        game.MusicReport = [Report("sng_00000000a6a6", "explore")];
        Assert.True(instance.CanMusic);

        play.Service.WatchMusic = true;
        play.Pump(() => instance.Music.Length == 1, "the music report");
        Assert.Equal("/root/Level/Music", instance.Music[0].NodePath);
        Assert.Equal(1, instance.MusicVersion);

        Assert.True(play.Service.SetMusicParameter(instance, "/root/Level/Music", "intensity", 0.25f));
        Assert.True(play.Service.TriggerMusicStinger(instance, "/root/Level/Music", "hit"));
        Assert.True(play.Service.GoToMusicSection(instance, "/root/Level/Music", "combat"));
        Assert.Equal(1, play.Service.ReloadMusic("mus_00000000a6a6", "/projects/game"));
        play.Pump(() => game.Commands.Any(c => c.Kind == EditorCommandKind.ReloadMusic), "the reload");
        var commands = game.Commands.Where(c => c.Kind >= EditorCommandKind.WatchMusic).ToList();
        Assert.Equal(
        [
            new EditorLinkCommand(EditorCommandKind.WatchMusic),
            new EditorLinkCommand(EditorCommandKind.SetMusicParameter, "/root/Level/Music\nintensity\n0.25"),
            new EditorLinkCommand(EditorCommandKind.TriggerMusicStinger, "/root/Level/Music\nhit"),
            new EditorLinkCommand(EditorCommandKind.GoToMusicSection, "/root/Level/Music\ncombat"),
            new EditorLinkCommand(EditorCommandKind.ReloadMusic, "mus_00000000a6a6\n/projects/game"),
        ], commands);

        play.Service.WatchMusic = false;
        play.Pump(() => game.Commands.Any(c => c.Kind == EditorCommandKind.UnwatchMusic), "the unwatch");
        Assert.Empty(instance.Music);
    }

    [Fact]
    public void AVersionFiveGameIsNeverSentAMusicCommand()
    {
        using var play = new PlayHarness();
        play.Launcher.ProtocolVersion = 5; // its link would drop on a command kind it does not know
        var instance = play.LaunchRunning();
        Assert.False(instance.CanMusic);

        play.Service.WatchMusic = true;
        Assert.False(play.Service.SetMusicParameter(instance, "/root/Music", "intensity", 1f));
        Assert.False(play.Service.TriggerMusicStinger(instance, "/root/Music", "hit"));
        Assert.Equal(0, play.Service.ReloadMusic("mus_00000000a6a6", "/projects/game"));
        play.Service.ReloadScene(instance); // something it does know, to know the link delivered what was sent
        play.Pump(() => play.Game(instance).Commands.Any(c => c.Kind == EditorCommandKind.ReloadScene), "the reload scene");
        Assert.DoesNotContain(play.Game(instance).Commands, c => c.Kind >= EditorCommandKind.WatchMusic);
        Assert.Equal(PlayInstanceState.Running, instance.State);
    }

    [Fact]
    public void TheTargetDrivesTheGamesPlayerFollowsItAndCopiesItsValuesToTheDefaults()
    {
        var builder = new FakeGameBuilder();
        var launcher = new FakeGameLauncher();
        using var editor = new HeadlessEditor(configure: o => o with { GameBuilder = builder, GameLauncher = launcher });
        try
        {
            var path = SongTab.CreateFile(Path.Combine(editor.Directory, "Project", "Assets", "Music", "Theme.mfsong"));
            editor.Workspace.Commands.OpenSong(path);
            editor.Tick(3);
            var tab = Assert.IsType<SongTab>(editor.Workspace.Session.ActiveTab);
            SongAdaptiveDemo.Build(tab.Document);
            var adaptive = tab.Document.Song.Adaptive!;
            var explore = adaptive.Sections.Single(s => s.Name == "Explore");
            editor.Workspace.ShowMusicRightTab(adaptive: true);

            var service = editor.Workspace.Play.Service;
            var instance = service.Launch("/fake/MyGame.Desktop", "/fake", null, "Game", null);
            launcher.Games.Single().MusicReport = [Report(tab.Document.Song.Uid, explore.Id), Report("sng_another00000", explore.Id) with { NodePath = "/root/Other" }];
            for (var i = 0; i < 2000 && instance.Music.Length == 0; i++)
            {
                editor.Tick();
                Thread.Sleep(2);
            }

            Assert.True(service.WatchMusic); // an adaptive song is open
            Assert.Equal(2, instance.Music.Length);

            // The Target menu lists this song's player only (the other one plays another song).
            var targets = GameMusicTarget.For(service, tab.Document.Song.Uid);
            var target = Assert.Single(targets);
            Assert.Equal("Game #1 · Music", target.Label);
            tab.SetGameTarget(target);
            editor.Tick(3);
            var html = editor.Workspace.SongPanel.Document.GetElementById("song-adaptive").InnerRml;
            Assert.Contains("Game #1 · Music", html, StringComparison.Ordinal);
            Assert.Contains("Copy to Defaults", html, StringComparison.Ordinal);
            Assert.Contains("Explore · bar 2.3 · in the game", editor.Workspace.SongPanel.Document.GetElementById("ad-now").InnerRml, StringComparison.Ordinal);

            // The playhead follows the game: Explore's start plus the reported seconds (moved on by at most a second).
            var seconds = tab.Document.Song.TicksToSeconds(tab.PlayheadTicks - explore.Start);
            Assert.InRange(seconds, 1.25, 1.25 + GameMusicTarget.MaxExtrapolationSeconds);
            Assert.Equal(0.7f, tab.ParameterValue("intensity"));

            // Audition values and stingers go to the game's player.
            var game = launcher.Games.Single();
            tab.SetAudition("intensity", 0.5f);
            tab.TriggerStinger(adaptive.Sections.Single(s => s.Name == "Hit").Id);
            for (var i = 0; i < 2000 && !game.Commands.Any(c => c.Kind == EditorCommandKind.TriggerMusicStinger); i++)
            {
                editor.Tick();
                Thread.Sleep(2);
            }

            Assert.Contains(game.Commands, c => c is { Kind: EditorCommandKind.SetMusicParameter, Argument: "/root/Level/Music\nintensity\n0.5" });
            Assert.Contains(game.Commands, c => c.Kind == EditorCommandKind.TriggerMusicStinger && c.Argument.StartsWith("/root/Level/Music\n", StringComparison.Ordinal));

            // Copy to Defaults: the game's values become the defaults, one undo step.
            var entries = tab.Document.History.Actions.Count;
            Assert.True(tab.Document.SetParameterDefaults(target.Report!.Parameters));
            Assert.Equal((0.7f, 1f), (adaptive.FindParameter("intensity")!.Default, adaptive.FindParameter("area")!.Default));
            Assert.Equal(entries + 1, tab.Document.History.Actions.Count);
            Assert.False(tab.Document.SetParameterDefaults(target.Report!.Parameters)); // already: nothing to do
            tab.Document.History.Undo();
            Assert.Equal((0f, 0f), (adaptive.FindParameter("intensity")!.Default, adaptive.FindParameter("area")!.Default));

            // The game goes away: the target falls back to the editor.
            service.Stop(instance);
            for (var i = 0; i < 3000 && tab.GameTarget is not null; i++)
            {
                editor.Tick();
                Thread.Sleep(2);
            }

            Assert.Null(tab.GameTarget);
        }
        finally
        {
            foreach (var game in launcher.Games)
                game.Dispose();
        }
    }
}
