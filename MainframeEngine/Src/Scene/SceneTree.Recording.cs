namespace MainframeEngine;

/// <summary>Gameplay recording: <see cref="Recorder"/>, fed every input event by <c>DispatchInput</c>.</summary>
public sealed partial class SceneTree
{
    /// <summary>
    /// The gameplay recorder (<c>--record</c>, or <see cref="GameRecorder.Start"/> from game code). Null under
    /// <c>--headless</c> and in hosts that are not a <see cref="GameHost"/> (the editor, tools, tests).
    /// </summary>
    public GameRecorder? Recorder { get; internal set; }
}
