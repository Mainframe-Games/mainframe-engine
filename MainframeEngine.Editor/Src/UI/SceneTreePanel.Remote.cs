using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The scene tree panel's Remote view (Godot's Local/Remote switch): while a game launched from Play is connected,
/// the header offers its running scene tree, read-only, asked for over the editor link about once a second
/// (<see cref="PlayService.RequestTree"/>). One chip per connected instance picks whose tree is shown.
/// </summary>
public sealed partial class SceneTreePanel
{
    /// <summary>Seconds between two snapshot requests while the Remote view is shown.</summary>
    public const double RemoteRefreshSeconds = 1.0;

    private static readonly RmlStructType<RemoteTreeRow> RemoteRowType = new RmlStructType<RemoteTreeRow>()
        .Member("name", static r => r.Name)
        .Member("type", static r => r.TypeName)
        .Member("icon", static r => r.Icon)
        .Member("index", static r => r.Index)
        .Member("indent", static r => r.Indent)
        .Member("has_children", static r => r.HasChildren)
        .Member("expanded", static r => r.Expanded)
        .Member("tooltip", static r => r.Tooltip);

    private static readonly RmlStructType<RemoteChip> ChipType = new RmlStructType<RemoteChip>()
        .Member("label", static c => c.Label)
        .Member("index", static c => c.Index)
        .Member("active", static c => c.Active);

    private readonly List<RemoteTreeRow> _remoteRows = [];
    private readonly List<RemoteChip> _chips = [];
    private readonly List<PlayInstance> _connected = [];
    private PlayInstance? _remoteInstance;
    private bool _remoteMode;
    private int _shownVersion = -1;
    private double _sinceRequest = double.MaxValue;
    private string _remoteStatus = "";

    /// <summary>The Remote view's model (the shown instance's last snapshot).</summary>
    public RemoteSceneTreeModel Remote { get; } = new();

    /// <summary>True while the Remote view replaces the edited scene's tree.</summary>
    public bool RemoteMode => _remoteMode;

    /// <summary>The instance whose tree the Remote view shows.</summary>
    public PlayInstance? RemoteInstance => _remoteInstance;

    private sealed record RemoteChip(string Label, int Index, bool Active);

    private void BindRemote(RmlDataModel model) =>
        model.BindList("remote", _remoteRows, RemoteRowType)
            .BindList("remote_chips", _chips, ChipType)
            .Bind("remote_available", () => _connected.Count > 0)
            .Bind("remote_mode", () => _remoteMode)
            .Bind("remote_status", () => _remoteStatus)
            .Event("show_local", () => ShowRemote(false))
            .Event("show_remote", () => ShowRemote(true))
            .Event("pick_remote", e => PickRemote(e.GetArgument(0).GetInt32()))
            .Event("remote_toggle", e => ToggleRemote(e.GetArgument(0).GetInt32()));

    /// <summary>Switches between the edited scene (false) and the running game's tree (true, only while one is connected).</summary>
    public void ShowRemote(bool remote)
    {
        remote &= _connected.Count > 0;
        if (remote == _remoteMode)
            return;
        _remoteMode = remote;
        _sinceRequest = double.MaxValue; // ask right away
        _model?.Dirty("remote_mode");
    }

    /// <summary>Shows connected instance <paramref name="index"/>'s tree (the chips' order).</summary>
    public void PickRemote(int index)
    {
        if ((uint)index >= (uint)_connected.Count || ReferenceEquals(_connected[index], _remoteInstance))
            return;
        SetRemoteInstance(_connected[index]);
    }

    /// <summary>Expands or collapses remote row <paramref name="index"/>.</summary>
    public void ToggleRemote(int index)
    {
        if ((uint)index >= (uint)_remoteRows.Count)
            return;
        Remote.Toggle(_remoteRows[index].Path);
        RebuildRemote();
    }

    /// <summary>Per frame: tracks the connected instances, asks the shown one for a snapshot every <see cref="RemoteRefreshSeconds"/>, shows new snapshots.</summary>
    public void TickRemote(double deltaTime)
    {
        if (UpdateConnected())
            _model?.Dirty("remote_available");
        if (_remoteInstance is not null && !_connected.Contains(_remoteInstance))
            SetRemoteInstance(_connected.Count > 0 ? _connected[0] : null);
        else if (_remoteInstance is null && _connected.Count > 0)
            SetRemoteInstance(_connected[0]);
        if (_connected.Count == 0 && _remoteMode)
            ShowRemote(false); // the game stopped: back to the edited scene, like Godot
        if (!_remoteMode || _remoteInstance is not { } instance)
            return;
        _sinceRequest += deltaTime;
        if (_sinceRequest >= RemoteRefreshSeconds && Workspace.Play.Service.RequestTree(instance))
            _sinceRequest = 0;
        if (instance.RemoteTreeVersion != _shownVersion)
            RebuildRemote();
    }

    private bool UpdateConnected()
    {
        var instances = Workspace.Play.Service.Instances;
        var count = 0;
        var changed = false;
        foreach (var instance in instances)
        {
            if (!instance.IsAlive || instance.GameId is null)
                continue;
            if (count >= _connected.Count || !ReferenceEquals(_connected[count], instance))
            {
                changed = true;
                if (count < _connected.Count)
                    _connected[count] = instance;
                else
                    _connected.Add(instance);
            }

            count++;
        }

        if (_connected.Count > count)
        {
            _connected.RemoveRange(count, _connected.Count - count);
            changed = true;
        }

        if (changed)
            RebuildChips();
        return changed;
    }

    private void SetRemoteInstance(PlayInstance? instance)
    {
        _remoteInstance = instance;
        _shownVersion = -1;
        _sinceRequest = double.MaxValue;
        Remote.ClearIcons();
        RebuildChips();
        RebuildRemote();
    }

    private void RebuildChips()
    {
        _chips.Clear();
        if (_connected.Count > 1)
            for (var i = 0; i < _connected.Count; i++)
                _chips.Add(new RemoteChip(_connected[i].Label, i, ReferenceEquals(_connected[i], _remoteInstance)));
        _model?.Dirty("remote_chips");
    }

    private void RebuildRemote()
    {
        var instance = _remoteInstance;
        _shownVersion = instance?.RemoteTreeVersion ?? -1;
        Remote.Rebuild(instance?.RemoteTree);
        _remoteRows.Clear();
        _remoteRows.AddRange(Remote.Rows);
        _remoteStatus = instance switch
        {
            null => "No game is running.",
            { RemoteTree: null } => "Waiting for the game…",
            { RemoteTreeTruncated: true } => $"The first {Remote.NodeCount} nodes",
            _ => "",
        };
        _model?.Dirty("remote");
        _model?.Dirty("remote_status");
    }
}
