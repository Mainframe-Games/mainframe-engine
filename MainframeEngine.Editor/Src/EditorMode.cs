namespace MainframeEngine.Editor;

/// <summary>
/// What the one editor workspace is laid out for (<see cref="EditorWorkspace.SetMode"/>). Each mode has its own panels,
/// menus, toolbar and tab strip; tabs of the hidden mode stay open, and each mode remembers its active tab.
/// </summary>
public enum EditorMode
{
    /// <summary>Scenes, songs and UI previews: the scene tree, inspector, file system, output, play and record.</summary>
    Game,

    /// <summary>Trimming recordings (<c>.mfcut</c> tabs): the video panel, no project needed. Audio (the DAW) comes later.</summary>
    Video,
}
