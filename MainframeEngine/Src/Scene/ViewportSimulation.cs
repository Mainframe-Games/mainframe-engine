namespace MainframeEngine;

/// <summary>
/// How a <see cref="SubViewport"/>'s subtree behaves while the tree is in <see cref="SceneTree.EditMode"/> (the editor's
/// Simulate, editor-viewport-tools G7.5).
/// </summary>
public enum ViewportSimulation : byte
{
    /// <summary>Edit-mode rules: only <see cref="ToolAttribute">[Tool]</see> nodes process and nothing steps (the default).</summary>
    None,

    /// <summary>
    /// The subtree runs as in a game although the tree is in edit mode: every process and lifecycle callback runs, game
    /// scripts included, and its physics spaces step. Input is never dispatched to it.
    /// </summary>
    Running,

    /// <summary>As a paused tree, for this subtree only: nothing processes or steps (<see cref="ProcessMode.WhenPaused"/> and <see cref="ProcessMode.Always"/> nodes aside).</summary>
    Paused,
}
