using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor.Music;

/// <summary>
/// The Adaptive panel's Target when it is a running game's <c>AdaptiveMusicPlayer</c> (A6, editor link protocol 6): the
/// game and the player's node path. While targeted, the panel's parameter controls, Fire buttons and Next row talk to
/// the game, and the panel and the Sections lane show the game's last <see cref="EditorLinkMessageType.Music"/> report.
/// </summary>
public sealed class GameMusicTarget(PlayService service, PlayInstance game, string nodePath)
{
    /// <summary>How far past its last report a position is moved on (reports come at least every beat while it plays).</summary>
    public const double MaxExtrapolationSeconds = 1.0;

    public PlayService Service { get; } = service ?? throw new ArgumentNullException(nameof(service));

    public PlayInstance Game { get; } = game ?? throw new ArgumentNullException(nameof(game));

    public string NodePath { get; } = nodePath ?? throw new ArgumentNullException(nameof(nodePath));

    /// <summary>The menu's text: "Game #1 · Music".</summary>
    public string Label => $"{Game.Label} #{Game.Number} · {ShortPath(NodePath)}";

    /// <summary>The game is still connected and has the music commands.</summary>
    public bool IsLive => Game.IsAlive && Game.GameId is not null && Game.CanMusic;

    /// <summary>The player's state in the game's last report, or null (it stopped, or no report yet). No allocation.</summary>
    public EditorLinkMusicInstance? Report
    {
        get
        {
            foreach (var music in Game.Music)
            {
                if (string.Equals(music.NodePath, NodePath, StringComparison.Ordinal))
                    return music;
            }

            return null;
        }
    }

    /// <summary>Seconds into the section now: the report's, moved on by the time since it arrived (at most a second).</summary>
    public double SecondsNow(DateTime utcNow) =>
        Report is { } report ? report.Seconds + Math.Clamp((utcNow - Game.MusicReceivedUtc).TotalSeconds, 0, MaxExtrapolationSeconds) : 0;

    /// <summary>The value the game's parameter has (its last report), or null.</summary>
    public float? ParameterValue(string name)
    {
        if (Report is not { } report)
            return null;
        foreach (var (parameter, value) in report.Parameters)
        {
            if (string.Equals(parameter, name, StringComparison.Ordinal))
                return value;
        }

        return null;
    }

    public void SetParameter(string name, float value) => Service.SetMusicParameter(Game, NodePath, name, value);

    public void TriggerStinger(string stinger) => Service.TriggerMusicStinger(Game, NodePath, stinger);

    public void GoToSection(string section) => Service.GoToMusicSection(Game, NodePath, section);

    /// <summary>Every connected game's player whose music was baked from the song <paramref name="songUid"/>.</summary>
    public static List<GameMusicTarget> For(PlayService service, string? songUid)
    {
        ArgumentNullException.ThrowIfNull(service);
        var targets = new List<GameMusicTarget>();
        if (string.IsNullOrEmpty(songUid))
            return targets;
        foreach (var game in service.Instances)
        {
            if (!game.IsAlive || game.GameId is null || !game.CanMusic)
                continue;
            foreach (var music in game.Music)
            {
                if (string.Equals(music.Song, songUid, StringComparison.Ordinal))
                    targets.Add(new GameMusicTarget(service, game, music.NodePath));
            }
        }

        return targets;
    }

    /// <summary>Whether <paramref name="other"/> names the same game and player.</summary>
    public bool Same(GameMusicTarget? other) =>
        other is not null && ReferenceEquals(other.Game, Game) && string.Equals(other.NodePath, NodePath, StringComparison.Ordinal);

    private static string ShortPath(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash >= 0 && slash < path.Length - 1 ? path[(slash + 1)..] : path;
    }
}
