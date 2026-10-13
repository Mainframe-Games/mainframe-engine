using System.Globalization;
using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor;

/// <summary>
/// Tells the user a gameplay recording finished and offers the next step: open it in Cutback (the trimmer that reads the
/// <c>.mfcap.json</c> sidecar) or show it in the file browser. Failures show the recorder's own message, with the kept
/// <c>.partial</c> folder one click away when the message names one or the folder exists (a game that exited mid-recording
/// sends no message, but leaves the folder). Everything it touches is a delegate, so it is tested without RmlUi, a process,
/// a browser or the disk.
/// </summary>
internal sealed class RecordingNotifier(
    Action<MessageRequest> show,
    Func<string?> cutbackSetting,
    Func<string?, string?> findCutback,
    Func<string, string, bool> launchCutback,
    Action<string> reveal,
    Action<Uri> openUrl,
    Func<string, bool> directoryExists)
{
    /// <summary>A recording event from a game: <c>Stopped</c> and <c>Failed</c> get a dialog, <c>Started</c> none.</summary>
    public void OnFinished(EditorLinkRecording recording)
    {
        switch (recording.State)
        {
            case EditorLinkRecordingState.Failed:
                ShowFailure(recording);
                break;
            case EditorLinkRecordingState.Stopped:
                ShowSaved(recording);
                break;
        }
    }

    private void ShowSaved(EditorLinkRecording recording)
    {
        var path = recording.Path;
        var elapsed = TimeSpan.FromSeconds(recording.ElapsedSeconds);
        show(new MessageRequest
        {
            Title = "Recording saved",
            Message = string.Create(CultureInfo.InvariantCulture,
                $"{Path.GetFileName(path)} ({(int)elapsed.TotalMinutes}:{elapsed.Seconds:00})\n{Path.GetDirectoryName(path)}"),
            Buttons = ["Open in Cutback", "Show in Folder", "Close"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 0)
                    OpenInCutback(path);
                else if (button == 1)
                    reveal(path);
            },
        });
    }

    private void OpenInCutback(string path)
    {
        if (findCutback(cutbackSetting()) is { } app && launchCutback(app, path))
            return;

        show(new MessageRequest
        {
            Title = "Cutback is not installed",
            Message = "Cutback trims recordings using what the game recorded. Download it, or set its path in Editor Settings.",
            Buttons = ["Download", "Close"],
            DefaultButton = 0,
            CancelButton = 1,
            Callback = (button, _) =>
            {
                if (button == 0)
                    openUrl(new Uri(CutbackLocator.ReleasesUrl));
            },
        });
    }

    private void ShowFailure(EditorLinkRecording recording)
    {
        var partial = recording.Path + ".partial";
        var hasPartial = recording.Error.Contains(".partial", StringComparison.Ordinal) || (recording.Path.Length > 0 && directoryExists(partial));
        show(new MessageRequest
        {
            Title = "Recording failed",
            Message = recording.Error.Length > 0 ? recording.Error : "The recording could not be saved.",
            Buttons = hasPartial ? ["Show in Folder", "Close"] : ["Close"],
            DefaultButton = 0,
            CancelButton = hasPartial ? 1 : 0,
            Callback = (button, _) =>
            {
                if (hasPartial && button == 0)
                    reveal(partial);
            },
        });
    }
}
