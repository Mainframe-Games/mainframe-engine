using System.Globalization;
using MainframeEngine.Audio.Music;

namespace MainframeEngine.EditorLink;

/// <summary>
/// The game side of the editor link's adaptive music (protocol 6, <see cref="GameSession"/>): applies the music commands
/// to the tree's <see cref="AdaptiveMusicPlayer"/>s and, while the editor watches, reports the playing instances
/// (<see cref="EditorLinkMessageType.Music"/>) at most every <see cref="ReportInterval"/> and only when something it shows
/// changed. <see cref="EditorCommandKind.ReloadMusic"/> reads a new bake on a worker, then switches every player of that
/// music at its next pass (its next bar 1): the section starts again at its downbeat in the new bake, with the parameters'
/// values kept.
/// </summary>
/// <remarks>Game thread only. Nothing runs while nobody watches and no reload is pending; a report allocates its snapshot.</remarks>
internal sealed class MusicLink(SceneTree tree)
{
    /// <summary>Reports go out at most this often (10 Hz).</summary>
    public const float ReportInterval = 0.1f;

    private readonly List<Reload> _reloads = [];
    private float _sinceReport = ReportInterval;
    private int _reportedHash;
    private bool _mustReport;

    /// <summary>The editor asked for music reports (<see cref="EditorCommandKind.WatchMusic"/>).</summary>
    public bool Watching { get; private set; }

    /// <summary>Reloads waiting for their bake to load or for a player's next pass (tests).</summary>
    internal int PendingReloads => _reloads.Count;

    /// <summary>Applies a music command; false when <paramref name="command"/> is not one.</summary>
    public bool Apply(in EditorLinkCommand command)
    {
        switch (command.Kind)
        {
            case EditorCommandKind.WatchMusic:
                Watching = true;
                _mustReport = true;
                return true;
            case EditorCommandKind.UnwatchMusic:
                Watching = false;
                return true;
            case EditorCommandKind.SetMusicParameter:
                if (Split(command.Argument, 3) is [var path, var name, var value] && Find(path) is { } player)
                {
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
                        player.SetParameter(name, number);
                    else
                        player.SetParameter(name, value);
                }

                return true;
            case EditorCommandKind.TriggerMusicStinger:
                if (Split(command.Argument, 2) is [var stingerPath, var stinger] && Find(stingerPath) is { } stung)
                    stung.TriggerStinger(stinger);
                return true;
            case EditorCommandKind.GoToMusicSection:
                if (Split(command.Argument, 2) is [var sectionPath, var section] && Find(sectionPath) is { } moved)
                    moved.GoToSection(section);
                return true;
            case EditorCommandKind.ReloadMusic:
                StartReload(command.Argument);
                return true;
            default:
                return false;
        }
    }

    /// <summary>Once per frame: finishes reloads and, while watched, reports at <see cref="ReportInterval"/>.</summary>
    public void Update(float delta, EditorLinkClient? link)
    {
        if (_reloads.Count > 0)
            UpdateReloads();
        if (!Watching || link is null)
            return;
        _sinceReport += delta;
        if (_sinceReport < ReportInterval && !_mustReport)
            return;
        _sinceReport = 0f;
        var server = tree.Servers.Get<AudioServer>();
        var hash = Hash(server);
        if (!_mustReport && hash == _reportedHash)
            return;
        _mustReport = false;
        _reportedHash = hash;
        link.ReportMusic(Snapshot(server));
    }

    /// <summary>What the editor is shown of every playing player (at most <see cref="EditorLinkProtocol.MaxMusicInstances"/>).</summary>
    internal static EditorLinkMusicInstance[] Snapshot(AudioServer? server)
    {
        if (server is null)
            return [];
        var list = new List<EditorLinkMusicInstance>(server.MusicInstances.Count);
        foreach (var instance in server.MusicInstances)
        {
            if (instance.Player is not { IsInsideTree: true } player || instance.Director is not { } director || list.Count == EditorLinkProtocol.MaxMusicInstances)
                continue;
            var plan = director.Plan;
            var position = instance.GetPosition();
            var parameters = new (string, float)[Math.Min(director.Parameters.Count, EditorLinkProtocol.MaxMusicParameters)];
            for (var i = 0; i < parameters.Length; i++)
                parameters[i] = (director.Parameters.NameOf(i), director.Parameters.GetValue(i));
            var layers = new (string, float)[plan.Layers.Length];
            for (var l = 0; l < layers.Length; l++)
                layers[l] = (plan.Layers[l].Id, director.LayerGainDb(l));
            var music = instance.Music!;
            list.Add(new EditorLinkMusicInstance
            {
                NodePath = player.GetPath().ToString(),
                Music = music.Uid ?? music.Manifest.Uid,
                Song = music.Manifest.Song ?? "",
                Section = position.Section ?? "",
                Bar = position.Bar,
                Beat = position.Beat,
                Seconds = (float)position.Seconds,
                Pending = instance.PendingTransition ?? "",
                PendingSeconds = instance.PendingSeconds,
                Parameters = parameters,
                Layers = layers,
            });
        }

        return [.. list];
    }

    // What a report shows, hashed without allocating: when it is unchanged, nothing is sent.
    private static int Hash(AudioServer? server)
    {
        var hash = new HashCode();
        if (server is null)
            return 0;
        foreach (var instance in server.MusicInstances)
        {
            if (instance.Player is not { IsInsideTree: true } || instance.Director is not { } director)
                continue;
            var position = instance.GetPosition();
            hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(instance));
            hash.Add(position.Section);
            hash.Add(position.Bar);
            hash.Add(position.Beat);
            hash.Add(director.PendingSection);
            hash.Add((int)(instance.PendingSeconds * 10));
            for (var i = 0; i < director.Parameters.Count; i++)
                hash.Add((int)MathF.Round(director.Parameters.GetValue(i) * 1000));
            for (var l = 0; l < director.Plan.Layers.Length; l++)
                hash.Add((int)MathF.Round(director.LayerGainDb(l) * 10));
        }

        return hash.ToHashCode();
    }

    private AdaptiveMusicPlayer? Find(string path)
    {
        if (tree.Root.GetNodeOrNull(path) is AdaptiveMusicPlayer player)
            return player;
        Log.Warning($"[EditorLink] No AdaptiveMusicPlayer at '{path}'; the music command is ignored.");
        return null;
    }

    private static string[]? Split(string argument, int parts)
    {
        var fields = (argument ?? "").Split(EditorLinkProtocol.MusicArgumentSeparator);
        return fields.Length == parts ? fields : null;
    }

    // ── Reload after a bake ───────────────────────────────────────────────────────────────────────────────────

    private void StartReload(string argument)
    {
        var fields = (argument ?? "").Split(EditorLinkProtocol.MusicArgumentSeparator);
        var uid = fields[0];
        var projectFolder = fields.Length > 1 ? fields[1] : "";
        var players = new List<AdaptiveMusicPlayer>();
        Collect(tree.Root, uid, players);
        if (players.Count == 0)
            return; // nothing plays that music here
        var relative = AssetDatabase.Current.GetPath(uid) ?? players[0].Music!.ResourcePath;
        if (string.IsNullOrEmpty(relative))
        {
            Log.Warning($"[EditorLink] Reload music: '{uid}' has no file in this game; restart it to hear the new bake.");
            return;
        }

        var full = projectFolder.Length > 0 ? Path.GetFullPath(relative, projectFolder) : AssetDatabase.Current.ToAbsolutePath(relative);
        _reloads.RemoveAll(r => r.Uid == uid); // a newer bake replaces one still loading
        var reload = new Reload(uid, players, Task.Run(() =>
        {
            var music = projectFolder.Length > 0 ? AdaptiveMusic.LoadFromProject(full, projectFolder) : AdaptiveMusic.Load(full);
            music.Preload();
            return music;
        }));
        _reloads.Add(reload);
    }

    private static void Collect(Node node, string uid, List<AdaptiveMusicPlayer> into)
    {
        if (node is AdaptiveMusicPlayer { Music: { } music } player && (music.Uid ?? music.Manifest.Uid) == uid)
            into.Add(player);
        foreach (var child in node.Children)
            Collect(child, uid, into);
    }

    private void UpdateReloads()
    {
        for (var r = _reloads.Count - 1; r >= 0; r--)
        {
            var reload = _reloads[r];
            if (!reload.Load.IsCompleted)
                continue;
            if (!reload.Load.IsCompletedSuccessfully)
            {
                Log.Warning($"[EditorLink] Reload music '{reload.Uid}' failed: {reload.Load.Exception?.GetBaseException().Message}");
                _reloads.RemoveAt(r);
                continue;
            }

            var music = reload.Load.Result;
            for (var p = reload.Players.Count - 1; p >= 0; p--)
            {
                var player = reload.Players[p];
                if (!player.IsInsideTree || player.Music is not { } current || (current.Uid ?? current.Manifest.Uid) != reload.Uid)
                {
                    reload.Drop(p); // left the tree, or plays something else now
                    continue;
                }

                if (player.Playing && !reload.AtBoundary(p))
                    continue; // waits for its next pass
                Swap(player, music);
                reload.Drop(p);
            }

            if (reload.Players.Count == 0)
            {
                _reloads.RemoveAt(r);
                Log.Info($"[EditorLink] Reloaded adaptive music '{music.ResourceName}' from the new bake.");
            }
        }
    }

    // The new bake, the parameters' values kept, from the downbeat of the section that played (else the start).
    internal static void Swap(AdaptiveMusicPlayer player, AdaptiveMusic music)
    {
        var playing = player.Playing;
        var section = player.CurrentSection;
        var values = new List<(string, float)>();
        if (player.Instance?.Director is { } old)
        {
            for (var i = 0; i < old.Parameters.Count; i++)
                values.Add((old.Parameters.NameOf(i), old.Parameters.GetTarget(i)));
        }

        player.Music = music;
        foreach (var (name, value) in values)
        {
            if (player.GetParameterIndex(name) >= 0)
                player.SetParameterImmediate(name, value);
        }

        if (!playing)
            return;
        var index = section is null ? -1 : music.Plan.FindSection(section);
        player.Play(index >= 0 && music.Plan.Sections[index].Kind == MusicSectionKind.Section ? section : null);
    }

    // One bake loading for the players of its music; each player switches at its next bar 1 (a pass start).
    private sealed class Reload
    {
        private readonly List<bool> _boundary = [];
        private readonly List<Action<int>> _handlers = [];

        public Reload(string uid, List<AdaptiveMusicPlayer> players, Task<AdaptiveMusic> load)
        {
            Uid = uid;
            Players = players;
            Load = load;
            foreach (var player in players)
            {
                _boundary.Add(false);
                Action<int> handler = bar =>
                {
                    if (bar == 1 && Load.IsCompleted && Players.IndexOf(player) is var at and >= 0)
                        _boundary[at] = true;
                };
                _handlers.Add(handler);
                player.Bar += handler;
            }
        }

        public string Uid { get; }

        public List<AdaptiveMusicPlayer> Players { get; }

        public Task<AdaptiveMusic> Load { get; }

        public bool AtBoundary(int index) => _boundary[index];

        public void Drop(int index)
        {
            Players[index].Bar -= _handlers[index];
            Players.RemoveAt(index);
            _boundary.RemoveAt(index);
            _handlers.RemoveAt(index);
        }
    }
}
