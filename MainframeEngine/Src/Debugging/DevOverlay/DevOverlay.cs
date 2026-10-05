namespace MainframeEngine;

/// <summary>
/// The F12 developer overlay: an RmlUi layer on top of everything with collapsible panels (renderer, shadows, GPU memory,
/// audio, physics, network, plus game panels from <see cref="AddPanel"/>). Values refresh at <c>refreshInterval</c>
/// while visible; hidden, it does no work. Reach it with <c>Tree.Servers.Get&lt;DevOverlay&gt;()</c>.
/// </summary>
public sealed class DevOverlay : IServer, IFrameServer
{
    public const int LayerOrder = int.MaxValue;

    private readonly SceneTree _tree;
    private readonly float _refreshInterval;
    private readonly List<DevOverlayPanel> _panels = [];
    private UiLayer? _layer;
    private DevOverlayDocument? _document;
    private float _sinceRefresh;
    private bool _visible;

    public DevOverlay(SceneTree tree, float refreshInterval = 0.25f)
    {
        _tree = tree ?? throw new ArgumentNullException(nameof(tree));
        _refreshInterval = refreshInterval;
    }

    public IReadOnlyList<DevOverlayPanel> Panels => _panels;

    /// <summary>The servers of the overlay's tree (the built-in panels read their numbers from them).</summary>
    internal ServerRegistry Servers => _tree.Servers;

    internal SceneTree Tree => _tree;

    /// <summary>Raised at the refresh interval (default 4 Hz) while visible; built-in panels update their values here.</summary>
    public event Action? Refreshed;

    /// <summary>Raised every frame while visible with the frame's delta time (never while hidden).</summary>
    public event Action<float>? Frame;

    public bool Visible
    {
        get => _visible;
        set
        {
            _visible = value;
            if (value)
                EnsureLayer();
            if (_layer is not null)
                _layer.Visible = value;
            _sinceRefresh = _refreshInterval; // refresh on the next frame after showing
        }
    }

    /// <summary>
    /// Adds a collapsible section whose body is <paramref name="rml"/>. <paramref name="bind"/> runs once the overlay
    /// document can bind models (<see cref="DevOverlayPanel.Model"/> is set): bind values there, call
    /// <see cref="DevOverlayPanel.Dirty"/> from <see cref="Refreshed"/>.
    /// </summary>
    public DevOverlayPanel AddPanel(string id, string title, string rml, Action<DevOverlayPanel>? bind = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (_panels.Exists(p => p.Id == id))
            throw new ArgumentException($"A dev overlay panel '{id}' already exists.", nameof(id));
        var panel = new DevOverlayPanel(id, title, rml, bind);
        _panels.Add(panel);
        if (_document is { IsReady: true } document)
        {
            document.BindPanel(panel);
            document.Recompose();
        }

        return panel;
    }

    public bool RemovePanel(string id)
    {
        var panel = _panels.Find(p => p.Id == id);
        if (panel is null)
            return false;
        _panels.Remove(panel);
        if (_document is { IsReady: true } document)
            document.Recompose();
        panel.Model?.Dispose();
        panel.Model = null;
        return true;
    }

    public void Process(in GameTime gameTime)
    {
        if (!_visible)
            return;
        Frame?.Invoke(gameTime.DeltaTime);
        _sinceRefresh += gameTime.DeltaTime;
        if (_sinceRefresh < _refreshInterval)
            return;
        _sinceRefresh = 0f;
        Refreshed?.Invoke();
    }

    private void EnsureLayer()
    {
        if (_layer is not null)
            return;
        _layer = new UiLayer { Name = "DevOverlay", Layer = LayerOrder, Visible = _visible };
        _document = new DevOverlayDocument(this);
        _layer.AddChild(_document);
        _tree.Root.AddChild(_layer);
    }

    public void Dispose()
    {
        if (_layer is { IsInsideTree: true })
            _layer.Free();
        _layer = null;
        _document = null;
    }
}
