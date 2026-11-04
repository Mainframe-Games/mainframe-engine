using MainframeEngine.Video.Models;
using MainframeEngine.Video.Timelines;

namespace MainframeEngine.Editor.Video;

/// <summary>
/// The Source viewer (Resolve's left viewer): one media file from the pool, played and scrubbed on its own, with in and
/// out marks that insert (<c>,</c>) and overwrite (<c>.</c>) place on the timeline. Marks are remembered per file for the
/// session. Its preview is a <see cref="VideoPreview"/> over a <see cref="SourceProgram"/>.
/// </summary>
public sealed class SourceViewer : IDisposable
{
    private static readonly Dictionary<string, (double? In, double? Out)> Marks = new(StringComparer.Ordinal);
    private static int _counter;

    private readonly IVideoDecoderFactory _decoders;
    private readonly Func<AudioServer?> _audio;
    private readonly Action<SubViewport> _attach;
    private IReadOnlyList<Segment> _whole = [];

    public SourceViewer(IVideoDecoderFactory decoders, Func<AudioServer?> audio, Action<SubViewport> attach)
    {
        _decoders = decoders ?? throw new ArgumentNullException(nameof(decoders));
        _audio = audio ?? throw new ArgumentNullException(nameof(audio));
        _attach = attach ?? throw new ArgumentNullException(nameof(attach));
    }

    /// <summary>The file shown, or null.</summary>
    public MediaRef? Media { get; private set; }

    public VideoPreview? Preview { get; private set; }

    public double Position => Preview?.Position ?? 0;

    public bool IsPlaying => Preview?.IsPlaying ?? false;

    public double? In => Media is { } m && Marks.TryGetValue(m.Hash, out var marks) ? marks.In : null;

    public double? Out => Media is { } m && Marks.TryGetValue(m.Hash, out var marks) ? marks.Out : null;

    /// <summary>The range insert and overwrite place: in to out, defaulting to the whole file.</summary>
    public (double In, double Out) Range => Media is { } m ? (In ?? 0, Out ?? m.Duration) : (0, 0);

    /// <summary>Shows <paramref name="media"/> (read from <paramref name="fullPath"/>), at its first frame.</summary>
    public void Load(MediaRef media, string fullPath)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (Media == media && Preview is not null)
            return;
        Preview?.Dispose();
        Media = media;
        _whole = [Segment.Create(0, media.Duration, enabled: true, SegmentOrigin.Auto)];
        Preview = new VideoPreview(_decoders, _audio, new SourceProgram(fullPath, media.Width, media.Height, media.FrameRate), ++_counter);
        _attach(Preview.Viewport);
        Preview.Seek(In ?? 0);
    }

    public void TogglePlay()
    {
        if (Preview is not { } preview)
            return;
        if (preview.IsPlaying)
        {
            preview.Pause();
            return;
        }

        var from = Position >= Media!.Duration - 0.01 ? 0 : Position;
        preview.Play(_whole, from);
    }

    public void Pause() => Preview?.Pause();

    public void Seek(double time)
    {
        if (Preview is not { } preview || Media is not { } media)
            return;
        time = Math.Clamp(time, 0, media.Duration);
        if (preview.IsPlaying)
            preview.Play(_whole, time);
        else
            preview.Seek(time);
    }

    /// <summary>I: marks in at the playhead (an out before it is cleared).</summary>
    public void MarkIn()
    {
        if (Media is not { } media)
            return;
        var at = Position;
        Marks[media.Hash] = (at, Out is { } o && o > at ? o : null);
    }

    /// <summary>O: marks out at the playhead (an in after it is cleared).</summary>
    public void MarkOut()
    {
        if (Media is not { } media)
            return;
        var at = Position;
        Marks[media.Hash] = (In is { } i && i < at ? i : null, at);
    }

    public void ClearMarks()
    {
        if (Media is { } media)
            Marks.Remove(media.Hash);
    }

    /// <summary>Main thread, every frame while shown.</summary>
    public void Tick() => Preview?.Update(_whole, 0);

    public void Dispose()
    {
        Preview?.Dispose();
        Preview = null;
        Media = null;
    }
}
