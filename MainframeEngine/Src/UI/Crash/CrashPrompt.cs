using System.Text;
using MainframeEngine.Localization;
using Silk.NET.Input;

namespace MainframeEngine;

/// <summary>How <see cref="CrashPrompt.Show"/> builds the prompt.</summary>
public sealed class CrashPromptOptions
{
    /// <summary>The document (a restyled copy of <see cref="CrashPrompt.DefaultSource"/> that keeps its data model).</summary>
    public string Source { get; init; } = CrashPrompt.DefaultSource;

    /// <summary>The prompt layer's order (95: above game menus and the settings menu, under the developer overlay).</summary>
    public int Layer { get; init; } = 95;

    /// <summary>The game's name in the title; null uses the running project's.</summary>
    public string? GameName { get; init; }

    /// <summary>The player's settings that keep "Always send reports"; null uses <see cref="UserSettings.Current"/>.</summary>
    public UserSettings? Settings { get; init; }
}

/// <summary>
/// The player's next-launch prompt (G15, docs/design/crash-reporting.md#next-launch-prompt-games): "{game} closed
/// unexpectedly last time", the report's one-line summary, and Open folder (desktop), Copy report, Send report (only with a
/// <c>crash.upload.url</c>), Close, and "Always send reports". Every choice marks the shown report — and the earlier ones
/// it mentions — handled (<see cref="CrashReporter.MarkHandled"/>), so a report is offered once. <see cref="GameHost"/>
/// shows it once the first scene is on screen when <c>crash.prompt</c> is <c>"ask"</c>; games with their own flow set
/// <c>"none"</c> and use <see cref="CrashReporter.PendingReports"/>. Gamepad-usable (B or Escape closes); strings go
/// through <see cref="Tr"/> (context <c>"crash"</c>; merge <c>Assets/UI/crash/crash.pot</c>).
/// </summary>
public sealed class CrashPrompt : UiDocument
{
    /// <summary>The engine's prompt document.</summary>
    public const string DefaultSource = "Assets/UI/crash/crash_prompt.rml";

    /// <summary>The name of the layer <see cref="Show"/> creates.</summary>
    public const string LayerName = "CrashPrompt";

    private CrashPromptOptions _options = new();
    private IReadOnlyList<CrashReportInfo> _reports = [];
    private CrashReport? _report;
    private UI.Rml.RmlDataModel? _model;
    private Task<CrashUploadResult>? _sending;
    private string _title = "";
    private string _summary = "";
    private string _more = "";
    private string _status = "";
    private bool _closing;

    /// <summary>The reports this prompt stands for, newest first (the first is shown).</summary>
    public IReadOnlyList<CrashReportInfo> Reports => _reports;

    /// <summary>The newest report (shown), or null when it could not be read.</summary>
    public CrashReport? Report => _report;

    /// <summary>What the last action said ("Report copied.", "Sending…").</summary>
    public string Status => _status;

    /// <summary>Raised once the prompt closed (its layer is being freed).</summary>
    public event Action? Closed;

    /// <summary>
    /// Shows the prompt for <paramref name="reports"/> (default: <see cref="CrashReporter.PendingReports"/>) on its own
    /// layer; null when there is nothing to show or a prompt is already open.
    /// </summary>
    public static CrashPrompt? Show(SceneTree tree, CrashPromptOptions? options = null, IReadOnlyList<CrashReportInfo>? reports = null)
    {
        ArgumentNullException.ThrowIfNull(tree);
        reports ??= CrashReporter.PendingReports;
        if (reports.Count == 0 || tree.Root.GetNodeOrNull(LayerName) is UiLayer { IsQueuedForDeletion: false })
            return null;
        options ??= new CrashPromptOptions();
        var layer = new UiLayer { Name = LayerName, Layer = options.Layer, ProcessMode = ProcessMode.Always };
        var prompt = new CrashPrompt { Name = "Prompt", _options = options, _reports = reports, Source = options.Source };
        layer.AddChild(prompt);
        tree.Root.AddChild(layer);
        return prompt;
    }

    protected override void OnReady()
    {
        _report = CrashReporter.Load(_reports[0]);
        Translate();
        _model = CreateDataModel("crash")
            .Bind("title", this, static p => p._title)
            .Bind("summary", this, static p => p._summary)
            .Bind("more", this, static p => p._more)
            .Bind("canOpen", this, static _ => CanOpenFolder)
            .Bind("canSend", this, static _ => CrashReporter.Uploader is not null)
            .Bind("sending", this, static p => p._sending is not null)
            .Bind("status", this, static p => p._status)
            .Bind("alwaysSend", this, static _ => CrashReporter.UploadConsent, static (p, v) => p.SetAlwaysSend(v))
            .Event("open", _ => OpenFolder())
            .Event("copy", _ => Copy())
            .Event("send", _ => Send())
            .Event("close", _ => Close());
    }

    protected override void OnLocaleChanged()
    {
        Translate();
        _model?.DirtyAll();
    }

    private void Translate()
    {
        var game = _options.GameName ?? GameHost.Project?.Name ?? "The game";
        _title = Tr.P("crash", "{0} closed unexpectedly last time", game);
        _summary = _report is null ? _reports[0].Summary : SummaryText(_report);
        _more = _reports.Count > 1 ? Tr.N("There is {0} earlier report in the same folder.", "There are {0} earlier reports in the same folder.", _reports.Count - 1) : "";
    }

    /// <summary>The summary the player reads: device loss and unclean exits in plain words, else the report's own line.</summary>
    private static string SummaryText(CrashReport report) => report.Kind switch
    {
        CrashKind.DeviceLost => Tr.P("crash", "The graphics driver stopped responding."),
        CrashKind.UncleanExit or CrashKind.NativeDump => Tr.P("crash", "The game stopped without saying why (a native crash, a forced quit or a power loss)."),
        CrashKind.Hang => Tr.P("crash", "The game stopped responding."),
        _ => report.Summary,
    };

    /// <summary>Opening a folder is possible here (desktop).</summary>
    public static bool CanOpenFolder => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    /// <summary>Opens the report's folder in the file manager (through SDL: no process is started); false when it failed.</summary>
    public bool OpenFolder()
    {
        if (!CanOpenFolder)
            return false;
        var folder = _reports[0].Folder;
        var opened = false;
        try
        {
            opened = Silk.NET.SDL.SdlProvider.SDL.Value.OpenURL(new Uri(folder).AbsoluteUri) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or UriFormatException or InvalidOperationException)
        {
            Log.Warning($"[Crash] Could not open '{folder}': {e.Message}");
        }

        _status = opened ? "" : Tr.P("crash", "The folder could not be opened: {0}", folder);
        _model?.Dirty("status");
        Handle(CrashDisposition.Dismissed);
        return opened;
    }

    /// <summary>Copies <c>report.json</c> and <c>log.txt</c> (never a dump) to the clipboard; returns the text.</summary>
    public string Copy()
    {
        var text = ReportText(_reports[0].Folder);
        if (Layer?.Server is { } server)
            server.ClipboardText = text;
        _status = Tr.P("crash", "The report was copied.");
        _model?.Dirty("status");
        Handle(CrashDisposition.Copied);
        return text;
    }

    /// <summary>The text Copy report puts on the clipboard: <c>report.json</c>, then the log (or the previous log's tail).</summary>
    public static string ReportText(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var text = new StringBuilder();
        foreach (var name in (ReadOnlySpan<string>)[CrashReport.FileName, CrashReport.LogFileName, CrashReport.LogTailFileName])
        {
            try
            {
                var path = Path.Combine(folder, name);
                if (!File.Exists(path))
                    continue;
                if (text.Length > 0)
                    text.Append("\n\n--- ").Append(name).Append(" ---\n");
                text.Append(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        return text.ToString();
    }

    /// <summary>Sends the shown report through <see cref="CrashReporter.Uploader"/> (no-op without one or while sending).</summary>
    public void Send()
    {
        if (CrashReporter.Uploader is not { } uploader || _sending is not null)
            return;
        _sending = uploader.SendAsync(_reports[0]);
        _status = Tr.P("crash", "Sending…");
        _model?.Dirty("status");
        _model?.Dirty("sending");
    }

    protected override void OnProcess(in GameTime gameTime)
    {
        if (_sending is not { IsCompleted: true } sending)
            return;
        _sending = null;
        var result = sending.IsCompletedSuccessfully ? sending.Result : new CrashUploadResult(false, 0, sending.Exception?.GetBaseException().Message);
        if (result.Success)
        {
            _status = Tr.P("crash", "The report was sent. Thank you!");
            Handle(CrashDisposition.Sent);
        }
        else
        {
            _status = Tr.P("crash", "The report could not be sent: {0}", result.Error ?? "");
            Log.Warning($"[Crash] Sending the report failed: {result.Error}");
        }

        _model?.Dirty("status");
        _model?.Dirty("sending");
    }

    private void SetAlwaysSend(bool value)
    {
        CrashReporter.UploadConsent = value;
        if ((_options.Settings ?? UserSettings.Current) is { } settings)
        {
            settings.CrashUploadConsent = value;
            if (settings.FilePath is not null)
                settings.Save();
        }
    }

    protected override void OnInput(InputEvent inputEvent)
    {
        switch (inputEvent)
        {
            case InputEventKey { Pressed: true, Key: Key.Escape }:
            case InputEventGamepadButton { Pressed: true, Button: ButtonName.B }:
                Close();
                GetViewport()?.SetInputAsHandled();
                break;
        }
    }

    /// <summary>Marks the reports handled (dismissed unless something else happened) and frees the prompt's layer.</summary>
    public void Close()
    {
        if (_closing)
            return;
        _closing = true;
        Handle(CrashDisposition.Dismissed);
        Closed?.Invoke();
        if (Parent is UiLayer layer)
            layer.QueueFree();
        else
            QueueFree();
    }

    private bool _handled;

    // The first choice decides the shown report's disposition; the earlier ones it mentions are dismissed with it.
    private void Handle(CrashDisposition how)
    {
        if (_handled && how != CrashDisposition.Sent)
            return;
        _handled = true;
        for (var i = 0; i < _reports.Count; i++)
            CrashReporter.MarkHandled(_reports[i], i == 0 ? how : CrashDisposition.Dismissed);
    }
}
