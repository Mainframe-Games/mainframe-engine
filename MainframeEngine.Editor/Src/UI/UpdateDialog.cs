using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The update dialog (docs/design/editor-updates.md): the new version, its release notes (plain text), View release, and
/// Update &amp; restart — progress and Cancel while downloading, the error and Retry on failure; Download (then show the
/// files) when the install cannot be replaced.
/// </summary>
public sealed class UpdateDialog : EditorDocument
{
    private RmlDataModel? _model;

    public UpdateDialog(EditorWorkspace workspace)
        : base(workspace, "update.rml")
    {
        Visible = false;
        Modal = true;
    }

    private UpdateController Updates => Workspace.Updates;

    /// <summary>The main button's text for the current state.</summary>
    public string PrimaryLabel => Updates.State switch
    {
        UpdateState.Failed => "Retry",
        UpdateState.Ready when Updates.CanReplace => "Restart now",
        UpdateState.Ready => OperatingSystem.IsMacOS() ? "Show in Finder" : OperatingSystem.IsWindows() ? "Show in Explorer" : "Show Files",
        _ => Updates.CanReplace ? "Update & restart" : "Download",
    };

    protected override void OnReady()
    {
        _model = CreateDataModel("update")
            .Bind("title", this, static d => d.Updates.Available?.Release is { } r ? $"Mainframe Engine v{r.Version}" : "Mainframe Engine")
            .Bind("current", this, static d => "You have v" + d.Updates.CurrentVersion)
            .Bind("notes", this, static d => d.Updates.Available?.Release?.Notes ?? "")
            .Bind("hint", this, static d => d.Updates.CanReplace ? "" : d.Updates.InstallHint ?? "")
            .Bind("downloading", this, static d => d.Updates.State == UpdateState.Downloading)
            .Bind("progress", this, static d => (Math.Clamp(d.Updates.Progress, 0, 1) * 100).ToString("0", CultureInfo.InvariantCulture) + "%")
            .Bind("error", this, static d => d.Updates.Error ?? "")
            .Bind("primary", this, static d => d.PrimaryLabel)
            .Event("primary", _ => Updates.Primary())
            .Event("cancel", _ => Updates.Cancel())
            .Event("view", _ => ViewRelease())
            .Event("later", _ => Close());
    }

    public void Open()
    {
        Visible = true;
        _model?.DirtyAll();
    }

    public void Close() => HideAndReleaseFocus();

    /// <summary>Re-reads the controller (its <see cref="UpdateController.Changed"/>).</summary>
    public void Refresh()
    {
        if (Visible)
            _model?.DirtyAll();
    }

    private void ViewRelease()
    {
        if (Updates.Available?.Release is { } release)
            OsShell.OpenUrl(release.PageUrl);
    }

    protected override void OnAttach(RmlDocument document) => document.AsElement().AddEventListener("keydown", e =>
    {
        if (Visible && (RmlKey)e.GetParameter("key_identifier", 0) == RmlKey.Escape)
            Close();
    });
}
