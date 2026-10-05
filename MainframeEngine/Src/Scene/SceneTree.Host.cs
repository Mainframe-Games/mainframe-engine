namespace MainframeEngine;

/// <summary>Requests a node makes of the engine that runs the tree (Godot's <c>get_tree().quit()</c>, viewport capture).</summary>
public sealed partial class SceneTree
{
    private readonly List<Action<FrameCapture>> _captureCallbacks = [];

    /// <summary>Raised by <see cref="Quit"/>; the engine running the tree closes after the current frame.</summary>
    public event Action<int>? QuitRequested;

    /// <summary>Raised by <see cref="CaptureFrame"/>; the engine copies the frame being built back to the CPU.</summary>
    internal event Action? CaptureRequested;

    /// <summary>True when the engine was started with frame capture (<c>--frame-capture</c>, <c>--screenshot</c>).</summary>
    public bool CanCaptureFrame { get; internal set; }

    /// <summary>
    /// Asks the engine to quit with <paramref name="exitCode"/> after the current frame (Godot's <c>SceneTree.quit</c>).
    /// A tree with no engine (tests, tools) only raises <see cref="QuitRequested"/>.
    /// </summary>
    public void Quit(int exitCode = 0) => QuitRequested?.Invoke(exitCode);

    /// <summary>
    /// Copies the frame being built in this iteration back to the CPU; <paramref name="onCaptured"/> runs on the main thread
    /// once it has been rendered (a frame later than Godot's synchronous viewport read). Requires
    /// <see cref="CanCaptureFrame"/>.
    /// </summary>
    public void CaptureFrame(Action<FrameCapture> onCaptured)
    {
        ArgumentNullException.ThrowIfNull(onCaptured);
        if (!CanCaptureFrame)
            throw new InvalidOperationException("Frame capture is disabled; start the game with --frame-capture.");
        _captureCallbacks.Add(onCaptured);
        CaptureRequested?.Invoke();
    }

    /// <summary>Hands a finished capture to every pending <see cref="CaptureFrame"/> callback.</summary>
    internal void DeliverCapture(FrameCapture capture)
    {
        if (_captureCallbacks.Count == 0)
            return;
        var callbacks = _captureCallbacks.ToArray();
        _captureCallbacks.Clear();
        foreach (var callback in callbacks)
            callback(capture);
    }
}
