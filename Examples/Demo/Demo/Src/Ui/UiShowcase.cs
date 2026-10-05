using System.Globalization;
using MainframeEngine;
using MainframeEngine.UI.Rml;

namespace Demo;

/// <summary>Widget gallery + live data binding; counts UI hot reloads (edit showcase.rcss/.rml and save).</summary>
public sealed class UiShowcase : UiDocument
{
    private RmlDataModel? _model;
    private UiServer? _ui;
    private int _reloads;
    private string _last = "—";

    public UiShowcase()
    {
        Source = "Content/UI/showcase.rml";
        AutoFocus = false;
    }

    public float Volume { get; set; } = 0.7f;
    public int Quality { get; set; } = 2;
    public string PlayerName { get; set; } = "Ada";
    public string Accent { get; set; } = "#2563eb";

    /// <summary>How many UI hot reloads this document has seen since the last reset.</summary>
    public int Reloads => _reloads;

    protected override void OnReady()
    {
        _ui = Tree?.Servers.Get<UiServer>();
        if (_ui is not null)
            _ui.HotReloaded += OnHotReloaded;
        _model = CreateDataModel("showcase")
            .Bind("hot", this, static d => d._ui?.HotReloadEnabled ?? false)
            .Bind("reloads", this, static d => d._reloads)
            .Bind("last", this, static d => d._last)
            .Bind("volume", this, static d => d.Volume, static (d, v) => d.Volume = v)
            .Bind("quality", this, static d => d.Quality, static (d, v) => d.Quality = v)
            .Bind("name", this, static d => d.PlayerName, static (d, v) => d.PlayerName = v)
            .Bind("accent", this, static d => d.Accent, static (d, v) => d.Accent = v)
            .Event("reset", () =>
            {
                _reloads = 0;
                _last = "—";
                _model?.Dirty("reloads");
                _model?.Dirty("last");
            });
    }

    protected override void OnExitTree()
    {
        if (_ui is not null)
            _ui.HotReloaded -= OnHotReloaded;
        _ui = null;
        base.OnExitTree();
    }

    private void OnHotReloaded(UiReloadKind kind, string? path)
    {
        _reloads++;
        _last = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        _model?.Dirty("reloads");
        _model?.Dirty("last");
    }
}
