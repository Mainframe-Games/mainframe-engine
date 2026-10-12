namespace MainframeEngine;

/// <summary>
/// Decides how many times each rendered frame is written so a recording is constant frame rate.
/// Live: a frame stamped <c>t</c> covers output frames up to <c>floor(t·fps)</c>; slow frames repeat,
/// fast ones are skipped before they are even copied, and a write the encoder could not take is made
/// up by the next frame (length and sync hold, only smoothness suffers). Offline: one in, one out.
/// </summary>
internal sealed class FrameScheduler(int fps, bool offline)
{
    private long _promised;

    /// <summary>Output frames written so far.</summary>
    public long WrittenFrames { get; private set; }

    /// <summary>Called before a frame is copied off the GPU: false means it would be skipped anyway.</summary>
    public bool ShouldCapture(double time)
    {
        if (offline)
            return true;
        var target = (long)Math.Floor(time * fps) + 1;
        if (target <= _promised)
            return false;
        _promised = target;
        return true;
    }

    /// <summary>How many output frames the frame stamped <paramref name="time"/> fills (0: drop it).</summary>
    public int CopiesFor(double time) =>
        offline ? 1 : (int)Math.Max(0, (long)Math.Floor(time * fps) + 1 - WrittenFrames);

    /// <summary>The writer accepted the frame <paramref name="copies"/> times.</summary>
    public void Committed(int copies) => WrittenFrames += copies;
}
