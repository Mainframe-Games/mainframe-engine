using System.Globalization;

namespace MainframeEngine.Sandbox;

/// <summary>
/// <c>--qa-capture &lt;dir&gt; [--qa-frames 30,90,180]</c>: runs the Sandbox deterministically (fixed
/// 60 Hz timestep), saves the listed frames as PNGs into the directory, then exits. Used by
/// <c>just qa</c> for screenshot review.
/// </summary>
public sealed class QaCapture
{
    public static IReadOnlyList<uint> DefaultFrames { get; } = [30, 90, 180];

    private readonly uint[] _frames;
    private uint _pendingFrame;

    private QaCapture(string outputDirectory, uint[] frames)
    {
        OutputDirectory = outputDirectory;
        _frames = frames;
    }

    public string OutputDirectory { get; }

    /// <summary>Frames to capture, ascending; frame 1 is the first update.</summary>
    public IReadOnlyList<uint> Frames => _frames;

    /// <summary>Returns null when <c>--qa-capture</c> is absent; throws on malformed arguments.</summary>
    public static QaCapture? FromArgs(IReadOnlyList<string> args)
    {
        string? dir = null;
        uint[] frames = [.. DefaultFrames];

        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--qa-capture":
                    dir = i + 1 < args.Count ? args[++i] : throw new ArgumentException("--qa-capture needs a directory.");
                    break;
                case "--qa-frames":
                    var list = i + 1 < args.Count ? args[++i] : throw new ArgumentException("--qa-frames needs a list, e.g. 30,90,180.");
                    frames = ParseFrames(list);
                    break;
            }
        }

        return dir is null ? null : new QaCapture(Path.GetFullPath(dir), frames);
    }

    private static uint[] ParseFrames(string list)
    {
        var frames = list
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(f => uint.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : throw new ArgumentException($"Invalid frame number '{f}' in --qa-frames."))
            .Distinct()
            .Order()
            .ToArray();

        return frames.Length > 0 ? frames : throw new ArgumentException("--qa-frames is empty.");
    }

    /// <summary>Deterministic, capture-enabled options that stop one frame after the last capture.</summary>
    public EngineOptions Apply(EngineOptions options) => options with
    {
        EnableFrameCapture = true,
        FixedDeltaTime = 1f / 60f,
        VSync = false,
        MaxFrames = (int)_frames[^1] + 1,
    };

    /// <summary>True when <paramref name="frameCount"/> should be captured; remembers it for <see cref="Save"/>.</summary>
    public bool ShouldCapture(uint frameCount)
    {
        if (Array.BinarySearch(_frames, frameCount) < 0)
            return false;

        _pendingFrame = frameCount;
        return true;
    }

    public void Save(FrameCapture capture)
    {
        var path = Path.Combine(OutputDirectory, $"sandbox_frame{_pendingFrame:D4}.png");
        capture.SavePng(path);
        Log.Info($"[QA] Saved {capture.Width}x{capture.Height} capture: {path}");
    }
}
