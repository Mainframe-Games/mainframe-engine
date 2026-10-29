namespace MainframeEngine;

/// <summary>
/// <c>--profile-trace &lt;file&gt;</c> for <see cref="GameHost"/> and <see cref="HeadlessHost"/> (G10): records from the first
/// frame and writes the trace on exit, or after <c>--profile-frames n</c> loaded frames (loading frames excluded, like
/// <c>--max-frames</c>) and then quits. With a renderer it waits two more frames so the last frames' GPU zones are in.
/// </summary>
internal sealed class ProfilerTraceRun(string path, int frames, string title, int gpuLatencyFrames)
{
    private int _loadedUpdates;
    private bool _saved;

    public string Path { get; } = path;

    /// <summary>Call at each update (the frame before it is recorded); true once the trace is written and the host should quit.</summary>
    public bool Update(bool loading)
    {
        if (_saved || frames <= 0 || loading)
            return false;
        // This update's frame is not in the history yet: frames 1 .. n + latency are, and frame n's GPU zones with them.
        if (++_loadedUpdates <= frames + gpuLatencyFrames)
            return false;
        Save(frames, gpuLatencyFrames);
        return true;
    }

    /// <summary>The host is exiting: writes the whole history unless the trace was written already.</summary>
    public void Exit()
    {
        if (!_saved)
            Save(frames > 0 ? frames : 0, 0);
    }

    private void Save(int lastFrames, int skipNewest)
    {
        _saved = true;
        try
        {
            var trace = Profiler.CaptureTrace(lastFrames, title, skipNewest);
            ChromeTraceWriter.Save(Path, trace);
            Log.Info($"[Profiler] Trace of {trace.Frames.Count} frames written to {System.IO.Path.GetFullPath(Path)}.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"[Profiler] Could not write the trace '{Path}': {e.Message}");
        }
    }

    /// <summary>The run for <paramref name="options"/> (null without <c>--profile-trace</c>).</summary>
    public static ProfilerTraceRun? For(GameHostOptions options, string title, bool hasGpu) =>
        options.ProfileTracePath is { } path ? new ProfilerTraceRun(path, options.ProfileFrames, title, hasGpu ? 2 : 0) : null;
}
