using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's splash screen and loading window: the logo, the "Mainframe Editor" wordmark, "vX.Y.Z", the job's title,
/// a status line (the step), a thin progress bar, a detail line with the elapsed time and Cancel, on the brand navy,
/// shown over everything (modal) while the editor starts. Opening a project or a scene later shows it as a panel over the
/// dimmed editor (<see cref="Show"/> with <c>panel</c>). <see cref="EditorLoader"/> drives it each frame
/// (<see cref="SetLoading"/>); <see cref="Finish"/> fades it out (RCSS opacity transition) and hides it. It never keeps
/// the editor blocked longer than the loading takes, except on the very first launch (a short minimum display).
/// </summary>
public sealed class SplashScreen : EditorDocument
{
    /// <summary>Seconds the fade-out lasts (matches the RCSS transition).</summary>
    public const double FadeSeconds = 0.35;

    private double _hideAt = -1;
    private double _visibleUntil;
    private double _time;
    private string _status = "Loading…";
    private string _title = "";
    private string _detail = "";
    private string _elapsed = "";
    private bool _canCancel;
    private bool _panel;
    private float _progress;

    public SplashScreen(EditorWorkspace workspace)
        : base(workspace, "splash.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>True from <see cref="Show"/> until the fade-out has finished.</summary>
    public bool IsShowing => Visible;

    /// <summary>The status line (the loading job's step).</summary>
    public string Status => _status;

    /// <summary>The loading job's title ("Opening Forest Demo"), or empty.</summary>
    public string Title => _title;

    /// <summary>The detail line ("112 / 274 textures"), or empty.</summary>
    public string Detail => _detail;

    /// <summary>Progress 0..1 of the bar.</summary>
    public float Progress => _progress;

    /// <summary>True after <see cref="Finish"/> until it is hidden (the minimum display time, then the fade).</summary>
    public bool IsFinishing => Visible && _hideAt >= 0;

    /// <summary>True when shown as the loading panel over the editor rather than the full start-up splash.</summary>
    public bool IsPanel => _panel;

    /// <summary>
    /// Shows the splash; <paramref name="minimumSeconds"/> keeps it up at least that long (first launch). With
    /// <paramref name="panel"/> it is the loading window over the dimmed editor instead of the full-window splash.
    /// </summary>
    public void Show(string status, float progress = 0f, double minimumSeconds = 0, bool panel = false)
    {
        _hideAt = -1;
        _visibleUntil = _time + minimumSeconds;
        _panel = panel;
        _title = "";
        _detail = "";
        _elapsed = "";
        _canCancel = false;
        Visible = true;
        if (EnsureLoaded())
        {
            Document.AsElement().SetClass("done", false);
            Document.AsElement().SetClass("panel", panel);
            ApplyLoading();
        }

        SetStatus(status, progress);
    }

    /// <summary>Updates the status line and progress bar (only touches the DOM when they changed).</summary>
    public void SetStatus(string status, float progress)
    {
        var changed = !string.Equals(status, _status, StringComparison.Ordinal);
        _status = status;
        _progress = Math.Clamp(progress, 0f, 1f);
        if (!IsLoaded)
            return;
        if (changed || !Visible)
            SetText("status", status);
        Document.GetElementById("fill").SetProperty("width", (_progress * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%");
    }

    /// <summary>
    /// A loading job's frame (<see cref="EditorLoader"/>): its title, step, detail, progress, elapsed seconds, and whether
    /// Cancel is offered. Touches the DOM only for what changed.
    /// </summary>
    public void SetLoading(string title, string step, string? detail, float progress, double elapsedSeconds, bool canCancel)
    {
        var elapsed = elapsedSeconds >= 1 ? ((int)elapsedSeconds).ToString(CultureInfo.InvariantCulture) + " s" : "";
        var changed = !string.Equals(title, _title, StringComparison.Ordinal) || !string.Equals(detail ?? "", _detail, StringComparison.Ordinal) ||
                      !string.Equals(elapsed, _elapsed, StringComparison.Ordinal) || canCancel != _canCancel;
        _title = title;
        _detail = detail ?? "";
        _elapsed = elapsed;
        _canCancel = canCancel;
        if (changed && IsLoaded)
            ApplyLoading();
        SetStatus(step, progress);
    }

    /// <summary>Loading is done: fade out (after the minimum display time, if any).</summary>
    public void Finish(string status = "Ready")
    {
        _canCancel = false;
        if (IsLoaded)
            ApplyLoading();
        SetStatus(status, 1f);
        _hideAt = Math.Max(_time, _visibleUntil);
    }

    protected override void OnAttach(RmlDocument document)
    {
        SetText("version", EditorBrand.Version);
        document.AsElement().SetClass("panel", _panel);
        ApplyLoading();
        SetText("status", _status);
        SetStatus(_status, _progress);
    }

    protected override void OnClickElement(RmlEvent e)
    {
        if (e.Target.Id == "cancel" && _canCancel)
            Workspace.Loader.Cancel();
    }

    private void ApplyLoading()
    {
        Document.AsElement().SetClass("titled", _title.Length > 0); // no title: the start-up splash as it always was
        SetText("title", _title);
        SetText("detail", _detail);
        SetText("elapsed", _elapsed);
        SetElementClass("cancel", "hidden", !_canCancel);
    }

    /// <summary>Called every frame by the workspace (drives the fade and the hide).</summary>
    public void Tick(double deltaTime)
    {
        _time += deltaTime;
        if (!Visible || _hideAt < 0 || _time < _hideAt)
            return;
        if (IsLoaded && !Document.AsElement().IsClassSet("done"))
        {
            Document.AsElement().SetClass("done", true);
            _hideAt = _time + FadeSeconds;
            return;
        }

        Visible = false;
        _hideAt = -1;
    }
}
