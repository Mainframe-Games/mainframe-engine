using MainframeEngine.UI.Rml;

namespace MainframeEngine;

/// <summary>
/// The loading screen (ADR 0183): an RmlUi document drawn over everything while a <see cref="SceneLoad"/> runs, showing
/// the game's name, a progress bar driven by the load's real progress and its stage label, then fading out into the
/// scene and freeing itself. <see cref="GameHost"/> shows one for the start scene (the project's <c>loading.screen</c>,
/// else <see cref="DefaultSource"/>, the engine's branded one); games can show one for any
/// <see cref="SceneTree.ChangeSceneToFileAsync"/> with <see cref="Show"/>.
/// </summary>
/// <remarks>
/// <para>
/// A document of its own binds the <c>loading</c> data model: <c>title</c> (string), <c>stage</c> (string),
/// <c>progress</c> (0–1, eased towards the load's value so the bar glides) and <c>percent</c> (0–100, whole). For example
/// <c>&lt;div class="fill" data-style-width="percent + '%'"/&gt;</c> and <c>{{ stage }}</c>. RCSS animations run on the UI's
/// clock, so they keep moving while the load works on other threads (and stay deterministic under <c>--fixed-fps</c>).
/// </para>
/// <para>
/// The document is modal while loading (the game below gets no input) and the tree reports <see cref="SceneTree.IsLoading"/>
/// until the screen has faded out, so hosts start counting frames once the game is visible.
/// </para>
/// </remarks>
public class LoadingScreen : UiLayer
{
    /// <summary>The engine's loading screen (Mainframe brand).</summary>
    public const string DefaultSource = "Content/UI/loading/loading.rml";

    /// <summary>The layer it draws on: above game UI layers.</summary>
    public const int DefaultLayer = 10_000;

    private UiDocument? _document;
    private RmlDataModel? _model;
    private float _shown;
    private int _percent = -1;
    private string _stage = "";
    private string _title = "";
    private float _fade = -1f; // < 0: not fading; else seconds into the fade
    private bool _registered;

    public LoadingScreen()
    {
        Name = "LoadingScreen";
        Layer = DefaultLayer;
        ProcessMode = ProcessMode.Always;
    }

    /// <summary>The document (<c>.rml</c>); default <see cref="DefaultSource"/>.</summary>
    [Export]
    public string Source { get; set; } = DefaultSource;

    /// <summary>The name shown (the game's).</summary>
    [Export]
    public string Title { get; set; } = "";

    /// <summary>Seconds the screen takes to fade out once the load completes (0: it disappears at once).</summary>
    [Export(Range = "0,3,0.05")]
    public float FadeSeconds { get; set; } = 0.45f;

    /// <summary>The load it follows; without one, <see cref="Progress"/> and <see cref="Stage"/> are shown as set (tests, previews).</summary>
    public SceneLoad? Load { get; set; }

    /// <summary>The progress shown when there is no <see cref="Load"/> (0–1).</summary>
    public float Progress { get; set; }

    /// <summary>The stage label shown when there is no <see cref="Load"/>.</summary>
    public string Stage { get; set; } = "";

    /// <summary>The bar's displayed value (eased towards the load's progress), 0–1.</summary>
    public float ShownProgress => _shown;

    /// <summary>True once the screen has started to fade out.</summary>
    public bool IsFading => _fade >= 0f;

    /// <summary>
    /// Starts fading out now (then the screen frees itself): for a screen without a <see cref="Load"/>, whose game decides
    /// when it is done. A screen following a load fades by itself once the load completes.
    /// </summary>
    public void FadeOut()
    {
        if (_fade < 0f)
            BeginFade();
    }

    /// <summary>The loaded document (null before it loads).</summary>
    public UiDocument? Document => _document;

    /// <summary>Adds a loading screen for <paramref name="load"/> under <paramref name="tree"/>'s root and returns it.</summary>
    public static LoadingScreen Show(SceneTree tree, SceneLoad load, string? source = null, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(load);
        var screen = new LoadingScreen { Load = load, Source = string.IsNullOrEmpty(source) ? DefaultSource : source, Title = title ?? "" };
        tree.Root.AddChild(screen);
        return screen;
    }

    protected override void OnEnterTree()
    {
        base.OnEnterTree();
        if (Tree is { } tree && !_registered)
        {
            tree.AddLoadingScreen();
            _registered = true;
        }
    }

    protected override void OnExitTree()
    {
        if (_registered)
        {
            Tree?.RemoveLoadingScreen();
            _registered = false;
        }

        base.OnExitTree();
    }

    protected override void OnReady()
    {
        base.OnReady();
        _document = new UiDocument { Name = "Document", Source = Source, Modal = true, AutoFocus = false };
        AddChild(_document);
        _title = Title;
        Read(out var progress, out _stage);
        _shown = progress;
        _percent = Percent(_shown);
        if (Context is not null)
            _model = _document.CreateDataModel("loading")
                .Bind("title", this, static s => s._title)
                .Bind("stage", this, static s => s._stage)
                .Bind("progress", this, static s => s._shown)
                .Bind("percent", this, static s => s._percent);
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        var delta = Math.Max(0f, gameTime.DeltaTime);
        Read(out var target, out var stage);
        var done = Load is { IsCompleted: true };
        if (done && Load!.IsDone)
            target = 1f;

        // Glide towards the target (never back): fast enough to keep up, smooth enough not to jump between stages.
        var previous = _shown;
        if (target > _shown)
            _shown = Math.Min(target, _shown + Math.Max((target - _shown) * Math.Min(1f, delta * 6f), delta * 0.08f));
        if (done)
            _shown = Math.Max(_shown, target);
        var percent = Percent(_shown);
        if (_model is { } model)
        {
            if (_shown != previous)
                model.Dirty("progress");
            if (percent != _percent)
                model.Dirty("percent");
            if (!string.Equals(stage, _stage, StringComparison.Ordinal))
                model.Dirty("stage");
            if (!string.Equals(Title, _title, StringComparison.Ordinal))
            {
                _title = Title;
                model.Dirty("title");
            }
        }

        _percent = percent;
        _stage = stage;

        if (done && _fade < 0f)
            BeginFade();
        if (_fade >= 0f)
            AdvanceFade(delta);
    }

    private void Read(out float progress, out string stage)
    {
        if (Load is { } load)
        {
            progress = load.Progress;
            stage = load.StageText;
        }
        else
        {
            progress = Math.Clamp(Progress, 0f, 1f);
            stage = Stage;
        }
    }

    private static int Percent(float progress) => (int)MathF.Floor(Math.Clamp(progress, 0f, 1f) * 100f);

    private void BeginFade()
    {
        _fade = 0f;
        if (_document is { } document)
            document.Modal = false; // the game gets input while the screen fades
    }

    private void AdvanceFade(float delta)
    {
        _fade += delta;
        var alpha = FadeSeconds <= 0f ? 0f : 1f - Math.Clamp(_fade / FadeSeconds, 0f, 1f);
        if (_document is { IsLoaded: true } document)
            document.Document.AsElement().SetProperty("opacity", alpha.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        if (alpha <= 0f && !IsQueuedForDeletion)
            QueueFree();
    }
}
