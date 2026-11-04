namespace MainframeEngine.Editor;

/// <summary>
/// What the one editor workspace is laid out for (<see cref="EditorWorkspace.SetMode"/>). Each mode has its own panels,
/// menus, toolbar and tab strip; tabs of the hidden mode stay open, and each mode remembers its active tab.
/// </summary>
public enum EditorMode
{
    /// <summary>Scenes and UI previews: the scene tree, inspector, file system, output, play and record.</summary>
    Game,

    /// <summary>Editing video (<c>.mftimeline</c> tabs, the timeline editor) over the whole window; timelines belong to the game project.</summary>
    Video,

    /// <summary>Songs (<c>.msong</c> tabs, the music editor) over the whole window; songs belong to the game project.</summary>
    Music,
}

/// <summary>Helpers on <see cref="EditorMode"/>.</summary>
public static class EditorModes
{
    /// <summary>
    /// Whether tabs of <paramref name="mode"/> belong to the open game project: every mode's do since video mode edits the
    /// project's timelines (ADR 0241), so a project switch asks about and closes them all. Kept as a seam for a mode that
    /// would not.
    /// </summary>
    public static bool UsesProject(this EditorMode mode) => Enum.IsDefined(mode);
}
