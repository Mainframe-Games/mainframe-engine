namespace MainframeEngine.Editor.Video;

/// <summary>
/// Video mode's view (<c>Content/Editor/video.rml</c>), over the viewport panel's view area below its tab strip and shown
/// only in video mode (<see cref="EditorWorkspace.SetMode"/>). Without a video tab it shows the empty state: how to open
/// or record a recording, and the recent videos.
/// </summary>
public sealed class VideoPanel : EditorDocument
{
    public VideoPanel(EditorWorkspace workspace)
        : base(workspace, "video.rml")
    {
        Visible = false;
    }

    protected override void OnReady() => CreateDataModel("video");
}
