using System.Globalization;
using MainframeEngine.UI.Rml;

namespace MainframeEngine.Editor;

/// <summary>
/// The editor's next-start crash dialog (G15, docs/design/editor.md): after the editor itself crashed, its summary, the
/// recovery copies of unsaved scenes written on the way down (each with Open), Copy Report, Open Folder and Report Issue
/// (copies the report and opens the repository's new-issue page with only the version in the title: no report data goes
/// into the link). Closing marks the reports handled, so each is offered once.
/// </summary>
public sealed class CrashDialog : EditorDocument
{
    /// <summary>The <see cref="CrashReport.Context"/> key the editor's crash handler lists recovery copies under (one per line).</summary>
    public const string RecoveryCopiesKey = "recoveryCopies";

    /// <summary>Where Report Issue goes (the version is the only thing added).</summary>
    public const string NewIssueUrl = "https://github.com/Mainframe-Games/mainframe-engine/issues/new";

    private sealed record Copy(string Name, string Path);

    private static readonly RmlStructType<Copy> CopyType = new RmlStructType<Copy>()
        .Member("name", static c => c.Name);

    private readonly List<Copy> _copies = [];
    private IReadOnlyList<CrashReportInfo> _reports = [];
    private CrashReport? _report;
    private RmlDataModel? _model;
    private string _summary = "";
    private string _detail = "";
    private string _status = "";

    public CrashDialog(EditorWorkspace workspace)
        : base(workspace, "crash_dialog.rml")
    {
        Visible = false;
        Modal = true;
    }

    /// <summary>The reports the open dialog stands for (newest first).</summary>
    public IReadOnlyList<CrashReportInfo> Reports => _reports;

    /// <summary>The newest report, shown.</summary>
    public CrashReport? Report => _report;

    /// <summary>The recovery copies that still exist (paths).</summary>
    public IReadOnlyList<string> RecoveryCopies => [.. _copies.Select(static c => c.Path)];

    /// <summary>The last action's note ("Report copied.").</summary>
    public string Status => _status;

    protected override void OnReady()
    {
        _model = CreateDataModel("crash_dialog")
            .Bind("summary", this, static d => d._summary)
            .Bind("detail", this, static d => d._detail)
            .Bind("status", this, static d => d._status)
            .BindList("copies", _copies, CopyType)
            .Event("open_copy", e => OpenCopy(e.GetArgument(0).GetInt32()))
            .Event("copy", _ => CopyReport())
            .Event("folder", _ => OpenFolder())
            .Event("issue", _ => ReportIssue())
            .Event("close", _ => Close());
    }

    /// <summary>Shows <paramref name="reports"/> (the editor's earlier crashes, newest first); false when there are none.</summary>
    public bool Open(IReadOnlyList<CrashReportInfo> reports)
    {
        ArgumentNullException.ThrowIfNull(reports);
        if (reports.Count == 0)
            return false;
        _reports = reports;
        _report = CrashReporter.Load(reports[0]);
        _summary = _report?.Summary ?? reports[0].Summary;
        var when = reports[0].TimestampUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        var version = _report?.App.Version is { Length: > 0 } v ? v : EngineInfo.Version;
        _detail = reports.Count > 1
            ? $"Mainframe Editor {version}, {when} ({reports.Count - 1} earlier crash report(s) in the same folder)."
            : $"Mainframe Editor {version}, {when}.";
        _copies.Clear();
        if (_report?.Context.TryGetValue(RecoveryCopiesKey, out var list) == true)
        {
            foreach (var path in list.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var full = ExpandHome(path);
                if (File.Exists(full))
                    _copies.Add(new Copy(Path.GetFileName(full), full));
            }
        }

        _status = "";
        Visible = true;
        _model?.DirtyAll();
        return true;
    }

    // Reports scrub the home folder to "~".
    private static string ExpandHome(string path) =>
        path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : path;

    /// <summary>Opens recovery copy <paramref name="index"/> in a new tab.</summary>
    public bool OpenCopy(int index)
    {
        if ((uint)index >= (uint)_copies.Count)
            return false;
        Workspace.Commands.OpenAny(_copies[index].Path);
        _status = $"Opened {_copies[index].Name}: save it over the original scene to keep it.";
        _model?.Dirty("status");
        return true;
    }

    /// <summary>Copies the report and the log to the clipboard; returns the text.</summary>
    public string CopyReport()
    {
        var text = _reports.Count > 0 ? CrashPrompt.ReportText(_reports[0].Folder) : "";
        if (Layer?.Server is { } server)
            server.ClipboardText = text;
        _status = "The crash report was copied to the clipboard.";
        _model?.Dirty("status");
        return text;
    }

    /// <summary>Shows the report's folder in the file browser.</summary>
    public bool OpenFolder() =>
        _reports.Count > 0 && Directory.Exists(_reports[0].Folder) && (Workspace.Options.OpenFolder ?? OsShell.OpenFolder)(_reports[0].Folder);

    /// <summary>The new-issue link: only the version goes in (the title), never report data.</summary>
    public static Uri IssueUri(string version) =>
        new($"{NewIssueUrl}?title={Uri.EscapeDataString($"Editor crash in v{version}")}");

    /// <summary>Copies the report, then opens the repository's new-issue page (the player pastes the report there).</summary>
    public Uri ReportIssue()
    {
        CopyReport();
        var uri = IssueUri(_report?.App.Version is { Length: > 0 } v ? v : EngineInfo.Version);
        (Workspace.Options.OpenUrl ?? OsShell.OpenUrl)(uri);
        _status = "The report was copied: paste it into the new issue.";
        _model?.Dirty("status");
        return uri;
    }

    /// <summary>Marks the reports handled (dismissed) and hides the dialog.</summary>
    public void Close()
    {
        foreach (var report in _reports)
            CrashReporter.MarkHandled(report, CrashDisposition.Dismissed);
        _reports = [];
        HideAndReleaseFocus();
    }
}
