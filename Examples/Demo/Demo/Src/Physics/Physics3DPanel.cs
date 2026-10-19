using System.Globalization;
using MainframeEngine;
using MainframeEngine.Localization;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>
/// The Physics 3D scene's controls: collision-shape overlay, crate count, rain and reset, and three save slots that save
/// and load the crates through the engine's <see cref="SaveServer"/> (with thumbnails, description and play time).
/// </summary>
public sealed class Physics3DPanel : UiDocument
{
    /// <summary>The save slots the panel shows.</summary>
    public static readonly string[] SlotNames = ["physics-1", "physics-2", "physics-3"];

    private sealed class Slot
    {
        public required Physics3DPanel Panel { get; init; }
        public required int Index { get; init; }
        public string Title { get; set; } = "";
        public string Detail { get; set; } = "";
        public string Thumb { get; set; } = EmptyThumb;
        public bool Filled { get; set; }
    }

    private const string EmptyThumb = "/Assets/icon.png";

    private static readonly RmlStructType<Slot> SlotType = new RmlStructType<Slot>()
        .Member("title", static s => s.Title)
        .Member("detail", static s => s.Detail)
        .Member("thumb", static s => s.Thumb)
        .Member("filled", static s => s.Filled)
        .Member("selected", static s => s.Panel._selected == s.Index);

    private readonly List<Slot> _slots = [];
    private RmlDataModel? _model;
    private Dropper3D? _crates;
    private PhysicsServer3D? _physics;
    private SaveServer? _saves;
    private bool _enterDebugDraw;
    private int _selected;
    private string _status = "";

    public Physics3DPanel()
    {
        Source = "Assets/Physics/panel3d.rml";
        AutoFocus = false;
    }

    /// <summary>The selected slot (0–2).</summary>
    public int SelectedSlot
    {
        get => _selected;
        set
        {
            _selected = Math.Clamp(value, 0, SlotNames.Length - 1);
            _model?.Dirty("slots");
        }
    }

    /// <summary>The last save/load message.</summary>
    public string Status => _status;

    protected override void OnReady()
    {
        var crates = _crates = Parent!.Parent!.GetNode<Dropper3D>("Crates");
        // The collision-shape overlay is server-global: remember it so leaving this scene restores the other scenes' look.
        _physics = Tree?.Servers.Get<PhysicsServer3D>();
        _enterDebugDraw = _physics?.DebugDrawEnabled ?? false;
        _saves = Tree?.Servers.Get<SaveServer>();
        if (_saves is not null)
            _saves.Saved += OnSaved;
        for (var i = 0; i < SlotNames.Length; i++)
            _slots.Add(new Slot { Panel = this, Index = i });
        RefreshSlots();
        _model = CreateDataModel("physics3d")
            .Bind("shapes", this, static d => d._physics?.DebugDrawEnabled ?? false,
                static (d, v) => { if (d._physics is { } p) p.DebugDrawEnabled = v; })
            .Bind("count", this, static d => d._crates?.Count ?? 0)
            .BindList("slots", _slots, SlotType)
            .Bind("status", this, static d => d._status)
            .Event("reset", () => crates.ResetBodies())
            .Event("rain", crates.Rain)
            .Event("select", e => SelectedSlot = (int)e.GetArgument(0).GetDouble())
            .Event("save", () => _ = SaveAsync())
            .Event("load", () => Load());
    }

    protected override void OnLocaleChanged()
    {
        RefreshSlots();
        _model?.DirtyAll();
    }

    protected override void OnProcess(in GameTime gameTime) => _model?.Dirty("count");

    protected override void OnExitTree()
    {
        if (_physics is { } p)
            p.DebugDrawEnabled = _enterDebugDraw;
        if (_saves is not null)
            _saves.Saved -= OnSaved;
        _physics = null;
        _crates = null;
        _saves = null;
        base.OnExitTree();
    }

    /// <summary>Saves the crates into the selected slot (serialized now, written on the save worker).</summary>
    public Task<SaveResult> SaveAsync()
    {
        if (_saves is null || _crates is null)
            return Task.FromResult(new SaveResult(SlotNames[_selected], SaveStatus.IoError, "No save server.", null));
        var snapshot = _crates.Snapshot();
        var description = Tr.N("{0} crate", "{0} crates", snapshot.Crates.Count);
        SetStatus(Tr._("Saving…"));
        var thumbnail = GameHost.Project?.Saves.Thumbnails ?? false;
        return _saves.SaveAsync(SlotNames[_selected], snapshot, CrateSave.Schema, new SaveInfo(description, Thumbnail: thumbnail));
    }

    /// <summary>Loads the selected slot and rebuilds the crates. Returns the load's status.</summary>
    public LoadStatus Load()
    {
        if (_saves is null || _crates is null)
            return LoadStatus.IoError;
        var result = _saves.Load(SlotNames[_selected], CrateSave.Schema);
        if (result.Data is { } save)
            _crates.Restore(save);
        SetStatus(result.Status switch
        {
            LoadStatus.Ok => Tr._("Loaded."),
            LoadStatus.RecoveredFromBackup => Tr._("The save was damaged: restored the copy from {0}.", Time(result.Info?.SavedUtc)),
            LoadStatus.NotFound => Tr._("This slot is empty."),
            LoadStatus.TooNew => Tr._("This save comes from a newer version of the game."),
            _ => Tr._("The save could not be loaded."),
        });
        RefreshSlots();
        return result.Status;
    }

    private void OnSaved(SaveResult result)
    {
        if (Array.IndexOf(SlotNames, result.Slot) < 0)
            return;
        SetStatus(result.Status == SaveStatus.Ok ? Tr._("Saved.") : Tr._("The game could not be saved."));
        // The thumbnail file was rewritten: drop RmlUi's cached copy so the slot shows the new one.
        Tree?.Servers.Get<UiServer>()?.Reload(this, UiReloadKind.Textures);
        RefreshSlots();
    }

    private void RefreshSlots()
    {
        var infos = _saves?.ListSlots() ?? [];
        foreach (var slot in _slots)
        {
            slot.Title = Tr._("Slot {0}", slot.Index + 1);
            var info = infos.FirstOrDefault(i => i.Slot == SlotNames[slot.Index]);
            slot.Filled = info is { IsReadable: true };
            slot.Thumb = info?.ThumbnailPath is { } thumb && File.Exists(thumb) ? thumb : EmptyThumb;
            slot.Detail = info is null
                ? Tr._("Empty")
                : info.IsReadable
                    ? $"{info.Description} · {PlayTime(info.PlayTime)} · {Time(info.SavedUtc)}"
                    : Tr._("Unreadable");
        }

        _model?.Dirty("slots");
    }

    private void SetStatus(string status)
    {
        _status = status;
        _model?.Dirty("status");
    }

    private static string PlayTime(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);

    private static string Time(DateTimeOffset? utc) =>
        utc is { } t ? t.ToLocalTime().ToString("t", Tr.Culture) : "";
}
