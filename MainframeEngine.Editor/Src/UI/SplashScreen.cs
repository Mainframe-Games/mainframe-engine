using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's splash screen: the logo, the "Mainframe Engine" wordmark, "Editor vX.Y.Z", a status line and a thin
/// progress bar on the brand navy, shown over everything (modal) while the editor starts and while a scene loads.
/// <see cref="Finish"/> fades it out (RCSS opacity transition) and hides it. It never keeps the editor blocked longer
/// than the loading takes, except on the very first launch (a short minimum display).
/// </summary>
public sealed class SplashScreen : EditorDocument
{
    /// <summary>Seconds the fade-out lasts (matches the RCSS transition).</summary>
    public const double FadeSeconds = 0.35;

    private double _hideAt = -1;
    private double _visibleUntil;
    private double _time;
    private string _status = "Loading…";
    private float _progress;

    public SplashScreen(EditorWorkspace workspace)
        : base(workspace, "splash.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>True from <see cref="Show"/> until the fade-out has finished.</summary>
    public bool IsShowing => Visible;

    /// <summary>The status line.</summary>
    public string Status => _status;

    /// <summary>Progress 0..1 of the bar.</summary>
    public float Progress => _progress;

    /// <summary>Shows the splash; <paramref name="minimumSeconds"/> keeps it up at least that long (first launch).</summary>
    public void Show(string status, float progress = 0f, double minimumSeconds = 0)
    {
        _hideAt = -1;
        _visibleUntil = _time + minimumSeconds;
        Visible = true;
        if (EnsureLoaded())
            Document.AsElement().SetClass("done", false);
        SetStatus(status, progress);
    }

    /// <summary>Updates the status line and progress bar (only touches the DOM when they changed).</summary>
    public void SetStatus(string status, float progress)
    {
        _status = status;
        _progress = Math.Clamp(progress, 0f, 1f);
        if (!IsLoaded)
            return;
        SetText("status", status);
        Document.GetElementById("fill").SetProperty("width", (_progress * 100f).ToString("0.#", CultureInfo.InvariantCulture) + "%");
    }

    /// <summary>Loading is done: fade out (after the minimum display time, if any).</summary>
    public void Finish(string status = "Ready")
    {
        SetStatus(status, 1f);
        _hideAt = Math.Max(_time, _visibleUntil);
    }

    protected override void OnAttach(RmlDocument document)
    {
        SetText("version", "Editor " + EditorBrand.Version);
        SetStatus(_status, _progress);
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
