using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// An RmlUi document (<c>.rml</c>) shown on the nearest ancestor <see cref="UiLayer"/>. The document loads lazily —
/// on first element access, or by the UI server before the next frame — so data models created in
/// <see cref="Node.OnReady"/> exist before the document binds to them:
/// <code>
/// protected override void OnReady()
/// {
///     Model = CreateDataModel("hud").Bind("health", () =&gt; _player.Health);
///     GetElementById("quit")!.Click += _ =&gt; Tree!.Quit();     // loads the document
/// }
/// protected override void OnProcess(in GameTime t) =&gt; Model.Dirty("health");
/// </code>
/// It unloads when the node leaves the tree. With hot reload the document is reloaded in place: data models (C# state)
/// and <see cref="UiElement"/> subscriptions survive.
/// </summary>
public class UiDocument : Node
{
    private readonly List<RmlDataModel> _models = [];
    private readonly Dictionary<string, UiElement> _elements = new(StringComparer.Ordinal);
    private RmlDocument _document;
    private UiLayer? _layer;
    private string _source = "";
    private bool _visible = true;
    private bool _modal;
    private bool _loadFailed;
    private bool _modelsChanged;

    /// <summary>The document file, e.g. <c>Content/UI/hud.rml</c> (through the UI file interface).</summary>
    [Export]
    public string Source
    {
        get => _source;
        set
        {
            value ??= "";
            if (_source == value)
                return;
            _source = value;
            _loadFailed = false;
            if (IsLoaded)
                Unload(keepModels: true);
        }
    }

    /// <summary>Inline RML used when <see cref="Source"/> is empty (tools, tests, generated UI).</summary>
    public string? Rml { get; set; }

    [Export]
    public bool Visible
    {
        get => _visible;
        set
        {
            if (_visible == value)
                return;
            _visible = value;
            ApplyVisibility();
        }
    }

    /// <summary>A visible modal document takes all input: lower layers and the game see none.</summary>
    [Export]
    public bool Modal
    {
        get => _modal;
        set
        {
            if (_modal == value)
                return;
            _modal = value;
            ApplyVisibility();
        }
    }

    /// <summary>Focus the document when shown (keyboard and gamepad navigation start in it). Off for passive HUDs.</summary>
    [Export]
    public bool AutoFocus { get; set; } = true;

    /// <summary>Raised after the document loads (first time and after <see cref="Source"/> changes).</summary>
    [Signal]
    public event Action? Loaded;

    /// <summary>Raised after a hot reload replaced the document.</summary>
    [Signal]
    public event Action? Reloaded;

    public bool IsLoaded => !_document.IsNull;

    /// <summary>The layer this document is shown on.</summary>
    public UiLayer? Layer => _layer;

    /// <summary>The loaded document (loads it if needed; null document when it cannot load).</summary>
    public RmlDocument Document => EnsureLoaded() ? _document : default;

    /// <summary>The document's data models (created with <see cref="CreateDataModel"/>).</summary>
    public IReadOnlyList<RmlDataModel> DataModels => _models;

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        _layer = FindLayer();
        if (_layer is null)
        {
            Log.Error($"[UI] UiDocument '{Name}' ({Source}) is not below a UiLayer; it will not be shown.");
            return;
        }

        _layer.AddDocument(this);
    }

    protected override void OnExitTree()
    {
        DetachFromLayer();
        base.OnExitTree();
    }

    private UiLayer? FindLayer()
    {
        for (var node = Parent; node is not null; node = node.Parent)
            if (node is UiLayer layer)
                return layer;
        return null;
    }

    internal void DetachFromLayer()
    {
        Unload(keepModels: false);
        _layer?.RemoveDocument(this);
        _layer = null;
    }

    // ── Data models ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a data model in this document's layer (names are unique per layer). Bind it before the document loads
    /// — in <see cref="Node.OnReady"/> — or the document is reloaded once the frame starts so views pick it up. The
    /// model is removed when the document leaves the tree.
    /// </summary>
    public RmlDataModel CreateDataModel(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        var context = _layer?.Context ?? throw new InvalidOperationException(
            $"UiDocument '{Name}' has no UI context (add it below a UiLayer in a tree with a UiServer).");
        var model = context.CreateDataModel(name);
        _models.Add(model);
        if (IsLoaded)
            _modelsChanged = true;
        return model;
    }

    // ── Elements ─────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The element with <paramref name="id"/> as a cached <see cref="UiElement"/>, or null.</summary>
    public UiElement? GetElementById(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (!EnsureLoaded())
            return _elements.GetValueOrDefault(id);
        var element = _document.GetElementById(id);
        if (_elements.TryGetValue(id, out var cached))
        {
            // The DOM may have replaced the element (inner RML, data-for, data-if): follow it, never keep a dangling one.
            if (element != cached.Element)
                cached.Rebind(element);
            return element.IsNull ? null : cached;
        }

        if (element.IsNull)
            return null;
        var wrapper = new UiElement(this, element, id);
        _elements.Add(id, wrapper);
        return wrapper;
    }

    /// <summary>The live element with <paramref name="id"/> in the loaded document (no loading), or a null element.</summary>
    internal RmlElement FindLive(string id) => IsLoaded ? _document.GetElementById(id) : default;

    /// <summary>The first element matching a CSS selector, or null (not cached; prefer ids for subscriptions).</summary>
    public UiElement? QuerySelector(string selector)
    {
        ArgumentException.ThrowIfNullOrEmpty(selector);
        if (!EnsureLoaded())
            return null;
        var element = _document.QuerySelector(selector);
        return element.IsNull ? null : new UiElement(this, element, null);
    }

    public void Show() => Visible = true;

    public void Hide() => Visible = false;

    // ── Loading ──────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads the document if it is not loaded yet; false when there is no context or loading failed.</summary>
    public bool EnsureLoaded()
    {
        if (IsLoaded)
            return true;
        if (_loadFailed || _layer?.Context is not { IsDisposed: false } context)
            return false;
        if (string.IsNullOrEmpty(Source) && string.IsNullOrEmpty(Rml))
            return false;

        try
        {
            _document = string.IsNullOrEmpty(Source)
                ? context.LoadDocumentFromMemory(_layer.Server?.Files.PrepareDocument(Rml!) ?? Rml!, $"memory/{Name}.rml")
                : context.LoadDocument(Source);
        }
        catch (RmlException e)
        {
            _loadFailed = true;
            Log.Error($"[UI] Could not load '{(string.IsNullOrEmpty(Source) ? Name : Source)}': {e.Message}");
            return false;
        }

        _modelsChanged = false;
        ApplyPanelTitle();
        ApplyVisibility();
        RebindElements();
        OnLoaded();
        Loaded?.Invoke();
        return true;
    }

    /// <summary>Called after the document loads (before <see cref="Loaded"/>).</summary>
    protected virtual void OnLoaded()
    {
    }

    /// <summary>Reloads the document from its source (hot reload); data models and element subscriptions survive.</summary>
    public void Reload()
    {
        if (!IsLoaded)
        {
            _loadFailed = false;
            EnsureLoaded();
            return;
        }

        var reloaded = _document.Reload();
        if (reloaded.IsNull)
        {
            // No reloadable source (in-memory document): load it again from scratch.
            Unload(keepModels: true);
            _loadFailed = false;
            EnsureLoaded();
        }
        else
        {
            _document = reloaded;
            _modelsChanged = false;
            ApplyPanelTitle();
            ApplyVisibility();
            RebindElements();
        }

        Reloaded?.Invoke();
    }

    /// <summary>Re-reads the style sheets only, keeping the DOM (hot reload of <c>.rcss</c>).</summary>
    public void ReloadStyleSheet()
    {
        if (IsLoaded && !_document.ReloadStyleSheet())
            Log.Warning($"[UI] Re-reading the style sheets of '{Source}' failed.");
    }

    /// <summary>Called by the UI server once per frame before its contexts update.</summary>
    internal void PrepareFrame()
    {
        if (!IsLoaded)
        {
            EnsureLoaded();
            return;
        }

        if (_modelsChanged)
            Reload(); // a data model was created after loading: rebind the views
    }

    /// <summary>After a failed load (e.g. a syntax error fixed by hot reload), allows another attempt.</summary>
    internal void ClearLoadFailure() => _loadFailed = false;

    private void ApplyVisibility()
    {
        if (!IsLoaded)
            return;
        if (_visible)
            _document.Show(_modal ? RmlModal.Modal : RmlModal.None, AutoFocus || _modal ? RmlFocus.Auto : RmlFocus.None);
        else
            _document.Hide();
    }

    /// <summary>Panel windows (<c>widgets/panel.rml</c>): the document's &lt;title&gt; goes into the title bar.</summary>
    private void ApplyPanelTitle()
    {
        var titleElement = _document.GetElementById(PanelTitleId);
        if (titleElement.IsNull)
            return;
        var title = _document.Title;
        if (title.Length == 0)
            return;
        var rml = title.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal);
        // The title is already translated (RmlUi translates <title>): the opt-out marker keeps it from a second lookup.
        if (_layer?.Server is { TextTranslator: not null, Translator: not null })
            rml = Localization.RmlLocalization.OptOutMarker + rml;
        titleElement.SetInnerRml(rml);
    }

    /// <summary>The title element of the widget library's panel template.</summary>
    public const string PanelTitleId = "mf-panel-title";

    private void RebindElements()
    {
        foreach (var (id, element) in _elements)
            element.Rebind(IsLoaded ? _document.GetElementById(id) : default);
    }

    private void Unload(bool keepModels)
    {
        if (IsLoaded)
        {
            var document = _document;
            _document = default;
            RebindElements();
            if (_layer?.Context is { IsDisposed: false })
                document.Close();
        }

        if (keepModels)
            return;
        foreach (var model in _models)
            model.Dispose();
        _models.Clear();
    }
}
