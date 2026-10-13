using System.Text.Json.Serialization;

namespace MainframeEngine.Video.Models;

/// <summary>
/// One contiguous region of the source timeline, <c>[Start, End)</c> in seconds.
/// Segments are immutable; <see cref="SegmentList"/> replaces them wholesale on edit.
/// </summary>
/// <param name="Id">Stable identity, preserved across edits that keep "the same" region.</param>
/// <param name="Start">Inclusive start, seconds from the beginning of the source.</param>
/// <param name="End">Exclusive end, seconds. Always greater than <paramref name="Start"/>.</param>
/// <param name="Enabled">True if the region is kept in the export, false if it is cut.</param>
/// <param name="Origin">Who produced this segment. See <see cref="SegmentOrigin"/>.</param>
/// <param name="Reason">Human-readable explanation shown in the UI, e.g. "silence 1.65s". Null when there is none.</param>
public sealed record Segment(
    string Id,
    double Start,
    double End,
    bool Enabled,
    SegmentOrigin Origin,
    string? Reason)
{
    [JsonIgnore]
    public double Duration => End - Start;

    /// <summary>True if <paramref name="time"/> falls within <c>[Start, End)</c>.</summary>
    public bool Contains(double time) => time >= Start && time < End;

    public static Segment Create(double start, double end, bool enabled, SegmentOrigin origin, string? reason = null)
        => new(NewId(), start, end, enabled, origin, reason);

    public static string NewId() => Guid.NewGuid().ToString("N");
}
