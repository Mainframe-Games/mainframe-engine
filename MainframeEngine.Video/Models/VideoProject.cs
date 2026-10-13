namespace MainframeEngine.Video.Models;

/// <summary>
/// The in-memory form of a <c>.mfcut</c> file. Immutable; the editor works on a
/// <see cref="SegmentList"/> and produces a new project via <c>with</c> on save.
/// </summary>
public sealed record VideoProject
{
    private readonly IReadOnlyList<Segment> _segments;

    /// <exception cref="InvalidPartitionException">The segments do not partition <c>[0, Source.DurationSeconds]</c>.</exception>
    public VideoProject(
        SourceInfo source,
        IReadOnlyList<Segment> segments,
        IReadOnlyList<Word> transcript,
        DetectionSettings settings)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(transcript);
        ArgumentNullException.ThrowIfNull(settings);

        Source = source;
        _segments = Normalize(segments, source.DurationSeconds);
        Transcript = transcript;
        Settings = settings;
    }

    public SourceInfo Source { get; init; }

    /// <summary>Sorted partition of the timeline. Validated on construction and on every <c>with</c>.</summary>
    public IReadOnlyList<Segment> Segments
    {
        get => _segments;
        init => _segments = Normalize(value, Source.DurationSeconds);
    }

    /// <summary>Word-level transcript. Empty until Phase 2.</summary>
    public IReadOnlyList<Word> Transcript { get; init; }

    public DetectionSettings Settings { get; init; }

    /// <summary>A fresh project: one enabled segment over the whole source, default settings.</summary>
    public static VideoProject CreateNew(SourceInfo source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var segments = new SegmentList(source.DurationSeconds).Segments;
        return new VideoProject(source, segments, [], DetectionSettings.Default);
    }

    private static Segment[] Normalize(IReadOnlyList<Segment> segments, double duration)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var sorted = segments.OrderBy(s => s.Start).ToArray();
        SegmentList.ValidatePartition(sorted, duration);
        return sorted;
    }
}
