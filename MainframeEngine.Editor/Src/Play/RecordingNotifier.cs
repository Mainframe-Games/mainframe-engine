using System.Globalization;
using MainframeEngine.EditorLink;

namespace MainframeEngine.Editor;

/// <summary>
/// Tells the user a gameplay recording finished and offers the next step: Open in Video Editor (a timeline made from the
/// recording in the project's <c>Assets/Video/Timelines</c>, ADR 0241) or showing the file in the file browser. Failures show the recorder's own message, with the kept
/// <c>.partial</c> folder one click away when the message names one or the folder exists (a game that exited mid-recording
/// sends no message, but leaves the folder). Everything it touches is a delegate, so it is tested without RmlUi, a process,
/// a browser or the disk.
/// </summary>
internal sealed class RecordingNotifier(
    Action<MessageRequest> show,
    Action<string> openInVideoEditor,
    Action<string> reveal,
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
            Buttons = ["Open in Video Editor", "Show in Folder", "Close"],
            DefaultButton = 0,
            CancelButton = 2,
            Callback = (button, _) =>
            {
                if (button == 0)
                    openInVideoEditor(path);
                else if (button == 1)
                    reveal(path);
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
